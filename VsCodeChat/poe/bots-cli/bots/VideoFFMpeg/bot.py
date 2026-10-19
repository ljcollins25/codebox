# poe: name=Video-FFmpeg
# poe: privacy_shield=half

import subprocess
import json
import os
import shlex
import shutil
import mimetypes
import tempfile
import threading
from http.server import HTTPServer, BaseHTTPRequestHandler
import requests

from fastapi_poe.types import (
    SettingsResponse, ParameterControls, Section,
    TextArea, DropDown, ValueNamePair,
)

poe.update_settings(SettingsResponse(
    introduction_message=(
        "Hi! I'm Video FFmpeg. Send me a video attachment or paste a video URL, "
        "and I'll run FFmpeg on it with the arguments you specify.\n\n"
        "**How to use:**\n"
        "1. Set your FFmpeg arguments via the settings icon (gear) below the "
        "message input box. These are the arguments *after* `-i <input>` "
        "(e.g. `-vframes 1 frame.png` or `-ss 5 -t 10 -c copy clip.mp4`).\n"
        "2. Send a video attachment or paste a video URL.\n"
        "3. All output files are returned as downloadable attachments with a "
        "file-to-URL mapping.\n\n"
        "You can also switch between raw JSON and formatted table output in the settings."
    ),
    parameter_controls=ParameterControls(
        sections=[
            Section(
                name="FFmpeg",
                controls=[
                    TextArea(
                        label="FFmpeg Arguments",
                        parameter_name="ffmpeg_args",
                        default_value="-c copy output.mp4",
                        placeholder="Args after -i <input>. e.g. -ss 5 -t 10 -c copy clip.mp4",
                    ),
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


# ---- HTTPS-to-HTTP proxy (same as Video-Probe) ----

def create_proxy_handler(target_url):
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
                    "Content-Type", "Content-Length",
                    "Content-Range", "Accept-Ranges",
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
    def handle_error(self, request, client_address):
        pass


def start_proxy(target_url):
    handler_cls = create_proxy_handler(target_url)
    server = QuietHTTPServer(("127.0.0.1", 0), handler_cls)
    port = server.server_address[1]
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    return server, port, thread


def stop_proxy(server, thread):
    try:
        server.shutdown()
    except Exception:
        pass
    try:
        server.server_close()
    except Exception:
        pass
    thread.join(timeout=5)


# ---- Bot ----

class VideoFFmpeg:
    def run(self):
        output_format = poe.query.parameters.get("output_format", "json")
        ffmpeg_args_str = poe.query.parameters.get("ffmpeg_args", "").strip()

        if not ffmpeg_args_str:
            raise poe.BotError(
                "Please provide FFmpeg arguments in the settings (gear icon)."
            )

        video_url = self._get_video_url()

        # Start proxy and prepare a temp working directory for FFmpeg output
        server, port, thread = start_proxy(video_url)
        proxy_url = f"http://127.0.0.1:{port}/"
        workdir = tempfile.mkdtemp()

        try:
            cmd = ["ffmpeg", "-y", "-i", proxy_url] + shlex.split(ffmpeg_args_str)
            result = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                cwd=workdir,
                timeout=300,
            )
        finally:
            stop_proxy(server, thread)

        if result.returncode != 0:
            shutil.rmtree(workdir, ignore_errors=True)
            stderr = result.stderr.strip() if result.stderr else "Unknown error"
            if len(stderr) > 1000:
                stderr = "..." + stderr[-1000:]
            raise poe.BotError(f"FFmpeg failed:\n{stderr}")

        # Collect every file FFmpeg wrote into the working directory
        file_map = {}
        attachments = []
        for filename in sorted(os.listdir(workdir)):
            filepath = os.path.join(workdir, filename)
            if os.path.isfile(filepath) and os.path.getsize(filepath) > 0:
                with open(filepath, "rb") as f:
                    contents = f.read()
                ct, _ = mimetypes.guess_type(filename)
                att = poe.Attachment(
                    name=filename, contents=contents, content_type=ct
                )
                file_map[filename] = att.url
                attachments.append(att)

        shutil.rmtree(workdir, ignore_errors=True)

        if not file_map:
            raise poe.BotError("FFmpeg produced no output files.")

        if output_format == "json":
            self._display_json(file_map, attachments)
        else:
            self._display_formatted(file_map, attachments)

    def _get_video_url(self):
        for att in poe.query.attachments:
            if att.url:
                return att.url
        text = poe.query.text.strip()
        if text.startswith("http://") or text.startswith("https://"):
            return text
        raise poe.BotError(
            "Please attach a video file or paste a video URL."
        )

    # ---- Output modes ----

    def _display_json(self, file_map, attachments):
        with poe.start_message(content_type="text/plain") as msg:
            msg.write(json.dumps(file_map, indent=2))
            for att in attachments:
                msg.add_attachment(att, add_inline_ref=False)

    def _display_formatted(self, file_map, attachments):
        with poe.start_message() as msg:
            msg.write("## FFmpeg Output\n\n")
            msg.write("| File | Download |\n")
            msg.write("|------|----------|\n")
            for filename, url in file_map.items():
                msg.write(f"| `{filename}` | [{filename}]({url}) |\n")
            msg.write("\n")
            for att in attachments:
                msg.add_attachment(att, add_inline_ref=False)


if __name__ == "__main__":
    bot = VideoFFmpeg()
    bot.run()
