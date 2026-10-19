# poe: name=Video-Probe
# poe: privacy_shield=half

import subprocess
import json
import threading
from http.server import HTTPServer, BaseHTTPRequestHandler
import requests

from fastapi_poe.types import (
    SettingsResponse, ParameterControls, Section,
    DropDown, ValueNamePair,
)

poe.update_settings(SettingsResponse(
    introduction_message=(
        "Hi! I'm Video Probe. Send me a video attachment or paste a video URL, "
        "and I'll analyze it with FFprobe to show you detailed media information "
        "(codec, resolution, duration, bitrate, keyframe positions for safe split "
        "boundaries, and more).\n\n"
        "You can switch between raw JSON and formatted output via the settings "
        "icon below the message input box."
    ),
    parameter_controls=ParameterControls(
        sections=[
            Section(
                name="Output",
                controls=[
                    DropDown(
                        label="Output Format",
                        parameter_name="output_format",
                        default_value="json",
                        options=[
                            ValueNamePair(value="json", name="Raw JSON"),
                            ValueNamePair(value="formatted", name="Formatted"),
                        ],
                    ),
                ],
            )
        ]
    ),
))


def create_proxy_handler(target_url):
    """Create an HTTP handler that proxies requests to the given HTTPS URL."""

    class ProxyHandler(BaseHTTPRequestHandler):
        def do_GET(self):
            headers = {}
            if "Range" in self.headers:
                headers["Range"] = self.headers["Range"]
            try:
                resp = requests.get(
                    target_url, headers=headers, stream=True, timeout=60
                )
                self.send_response(resp.status_code)
                for h in [
                    "Content-Type",
                    "Content-Length",
                    "Content-Range",
                    "Accept-Ranges",
                ]:
                    if h in resp.headers:
                        self.send_header(h, resp.headers[h])
                self.end_headers()
                for chunk in resp.iter_content(chunk_size=65536):
                    self.wfile.write(chunk)
            except Exception:
                try:
                    self.send_response(502)
                    self.end_headers()
                except Exception:
                    pass

        def do_HEAD(self):
            try:
                resp = requests.head(
                    target_url, timeout=30, allow_redirects=True
                )
                self.send_response(resp.status_code)
                for h in ["Content-Type", "Content-Length", "Accept-Ranges"]:
                    if h in resp.headers:
                        self.send_header(h, resp.headers[h])
                self.end_headers()
            except Exception:
                try:
                    self.send_response(502)
                    self.end_headers()
                except Exception:
                    pass

        def log_message(self, format, *args):
            pass

    return ProxyHandler


class QuietHTTPServer(HTTPServer):
    """HTTPServer that silently swallows handler errors."""

    def handle_error(self, request, client_address):
        pass


def start_proxy(target_url):
    """Start a local HTTP proxy for the given URL. Returns (server, port, thread)."""
    handler_cls = create_proxy_handler(target_url)
    server = QuietHTTPServer(("127.0.0.1", 0), handler_cls)
    port = server.server_address[1]
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    return server, port, thread


def stop_proxy(server, thread):
    """Cleanly shut down the proxy server and wait for its thread to exit."""
    try:
        server.shutdown()
    except Exception:
        pass
    try:
        server.server_close()
    except Exception:
        pass
    thread.join(timeout=5)


def format_duration(seconds):
    """Format seconds into a human-readable duration string."""
    m, s = divmod(seconds, 60)
    h, m = divmod(m, 60)
    if h > 0:
        return f"{int(h)}h {int(m)}m {s:.1f}s"
    elif m > 0:
        return f"{int(m)}m {s:.1f}s"
    return f"{s:.1f}s"


def format_timestamp(seconds):
    """Format seconds into HH:MM:SS.mmm for keyframe display."""
    m, s = divmod(seconds, 60)
    h, m = divmod(m, 60)
    return f"{int(h):02d}:{int(m):02d}:{s:06.3f}"


class VideoProbe:
    def run(self):
        output_format = poe.query.parameters.get("output_format", "json")
        video_url = self._get_video_url()

        # Start local HTTP proxy for the HTTPS URL
        server, port, thread = start_proxy(video_url)
        proxy_url = f"http://127.0.0.1:{port}/"

        try:
            # Probe 1: format + streams
            result = subprocess.run(
                [
                    "ffprobe",
                    "-v", "quiet",
                    "-print_format", "json",
                    "-show_format",
                    "-show_streams",
                    proxy_url,
                ],
                capture_output=True,
                text=True,
                timeout=120,
            )

            # Probe 2: video packet flags for keyframe detection
            kf_result = subprocess.run(
                [
                    "ffprobe",
                    "-v", "quiet",
                    "-print_format", "json",
                    "-select_streams", "v:0",
                    "-show_entries", "packet=pts_time,flags",
                    proxy_url,
                ],
                capture_output=True,
                text=True,
                timeout=120,
            )
        finally:
            stop_proxy(server, thread)

        if result.returncode != 0:
            error_msg = result.stderr.strip() if result.stderr else "Unknown FFprobe error"
            raise poe.BotError(f"FFprobe analysis failed: {error_msg}")

        if not result.stdout.strip():
            raise poe.BotError("FFprobe returned no output. The file may not be a valid media file.")

        try:
            probe_data = json.loads(result.stdout)
        except json.JSONDecodeError:
            raise poe.BotError("FFprobe returned invalid data. The file may not be a supported media format.")

        # Parse keyframes (best-effort)
        keyframes = self._parse_keyframes(kf_result)

        # Merge keyframe info into the probe data
        probe_data["keyframes"] = self._build_keyframe_data(keyframes)

        if output_format == "json":
            self._display_json(probe_data)
        else:
            self._display_formatted(probe_data, keyframes)

    def _get_video_url(self):
        """Extract a video URL from the query attachments or text."""
        for att in poe.query.attachments:
            if att.url:
                return att.url

        text = poe.query.text.strip()
        if text.startswith("http://") or text.startswith("https://"):
            return text

        raise poe.BotError(
            "Please attach a video file or paste a video URL."
        )

    def _parse_keyframes(self, kf_result):
        """Extract keyframe timestamps from the packet probe result."""
        if kf_result.returncode != 0 or not kf_result.stdout.strip():
            return None
        try:
            kf_data = json.loads(kf_result.stdout)
        except json.JSONDecodeError:
            return None

        packets = kf_data.get("packets") or kf_data.get("packets_and_frames")
        if not packets:
            return None

        keyframe_times = []
        for pkt in packets:
            flags = pkt.get("flags", "")
            if "K" in flags:
                pts = pkt.get("pts_time")
                if pts is not None:
                    try:
                        keyframe_times.append(float(pts))
                    except (ValueError, TypeError):
                        pass

        keyframe_times.sort()
        return keyframe_times if keyframe_times else None

    def _build_keyframe_data(self, keyframes):
        """Build a keyframe summary dict for inclusion in the JSON output."""
        if not keyframes:
            return {"count": 0, "timestamps": [], "avg_interval": None}

        avg_interval = None
        if len(keyframes) >= 2:
            intervals = [
                keyframes[i + 1] - keyframes[i]
                for i in range(len(keyframes) - 1)
            ]
            avg_interval = round(sum(intervals) / len(intervals), 4)

        return {
            "count": len(keyframes),
            "avg_interval": avg_interval,
            "timestamps": [round(t, 4) for t in keyframes],
        }

    # ---- Raw JSON mode ----

    def _display_json(self, data):
        """Output the combined probe data as plain JSON text."""
        with poe.start_message(content_type="text/plain") as msg:
            msg.write(json.dumps(data, indent=2))

    # ---- Formatted mode ----

    def _display_formatted(self, data, keyframes):
        """Display a human-readable formatted report."""
        with poe.start_message() as msg:
            # --- Format section ---
            fmt = data.get("format", {})
            if fmt:
                msg.write("## Format\n\n")
                if fmt.get("format_long_name"):
                    msg.write(f"**Container:** {fmt['format_long_name']}\n\n")
                if fmt.get("duration"):
                    msg.write(f"**Duration:** {format_duration(float(fmt['duration']))}\n\n")
                if fmt.get("size"):
                    size_mb = int(fmt["size"]) / (1024 * 1024)
                    msg.write(f"**File Size:** {size_mb:.2f} MB\n\n")
                if fmt.get("bit_rate"):
                    br_kbps = int(fmt["bit_rate"]) / 1000
                    msg.write(f"**Overall Bitrate:** {br_kbps:.0f} kbps\n\n")

            # --- Streams section ---
            streams = data.get("streams", [])
            for i, stream in enumerate(streams):
                codec_type = stream.get("codec_type", "unknown")
                msg.write(f"## Stream #{i} — {codec_type.capitalize()}\n\n")

                codec_name = stream.get("codec_long_name") or stream.get("codec_name")
                if codec_name:
                    msg.write(f"**Codec:** {codec_name}\n\n")

                if codec_type == "video":
                    self._write_video_stream(msg, stream)
                elif codec_type == "audio":
                    self._write_audio_stream(msg, stream)

            # --- Keyframes section ---
            self._write_keyframes(msg, keyframes)

            # --- Raw JSON ---
            msg.write("## Raw FFprobe Output\n\n")
            msg.write(f"````json\n{json.dumps(data, indent=2)}\n````\n")

    def _write_video_stream(self, msg, stream):
        if stream.get("width") and stream.get("height"):
            msg.write(f"**Resolution:** {stream['width']}×{stream['height']}\n\n")
        if stream.get("display_aspect_ratio"):
            msg.write(f"**Aspect Ratio:** {stream['display_aspect_ratio']}\n\n")
        if stream.get("r_frame_rate"):
            try:
                num, den = stream["r_frame_rate"].split("/")
                fps = int(num) / int(den)
                msg.write(f"**Frame Rate:** {fps:.2f} fps\n\n")
            except (ValueError, ZeroDivisionError):
                msg.write(f"**Frame Rate:** {stream['r_frame_rate']}\n\n")
        if stream.get("pix_fmt"):
            msg.write(f"**Pixel Format:** {stream['pix_fmt']}\n\n")
        if stream.get("bit_rate"):
            msg.write(f"**Bitrate:** {int(stream['bit_rate']) / 1000:.0f} kbps\n\n")

    def _write_audio_stream(self, msg, stream):
        if stream.get("sample_rate"):
            msg.write(f"**Sample Rate:** {stream['sample_rate']} Hz\n\n")
        if stream.get("channels"):
            msg.write(f"**Channels:** {stream['channels']}\n\n")
        if stream.get("channel_layout"):
            msg.write(f"**Layout:** {stream['channel_layout']}\n\n")
        if stream.get("bit_rate"):
            msg.write(f"**Bitrate:** {int(stream['bit_rate']) / 1000:.0f} kbps\n\n")

    def _write_keyframes(self, msg, keyframes):
        """Display keyframe timestamps as safe split boundaries."""
        msg.write("## Keyframes (Safe Split Boundaries)\n\n")

        if not keyframes:
            msg.write("*No keyframe data available.*\n\n")
            return

        msg.write(f"**Total Keyframes:** {len(keyframes)}\n\n")

        if len(keyframes) >= 2:
            intervals = [
                keyframes[i + 1] - keyframes[i]
                for i in range(len(keyframes) - 1)
            ]
            avg_interval = sum(intervals) / len(intervals)
            msg.write(f"**Avg Keyframe Interval:** {avg_interval:.2f}s\n\n")

        msg.write("| # | Timestamp | Seconds |\n")
        msg.write("|---|-----------|--------:|\n")
        for i, t in enumerate(keyframes):
            msg.write(f"| {i + 1} | `{format_timestamp(t)}` | {t:.3f} |\n")
        msg.write("\n")


if __name__ == "__main__":
    bot = VideoProbe()
    bot.run()
