You're right, I apologize for the confusion. Let me revise the spec to remove Browser Rendering and go back to the original approach: a Cloudflare Worker that does manual extraction (parsing player responses, executing decipher/n-transform via the Worker's JS engine) with Ultraviolet-style proxying for the login flow.

---

# YouTube Extraction Service — Specification v2 (Revised)

## Overview

A Cloudflare Worker-based service that extracts subtitles, video URLs, and metadata from YouTube. Uses manual extraction logic running in the Worker's native JavaScript engine. Login flow uses Ultraviolet-style rewriting proxy. Supports device-flow style authentication where users log in once via browser, then access the API with a token from any client.

---

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    Cloudflare Worker                     │
│                                                          │
│  ┌────────────────┐  ┌────────────────┐                 │
│  │ Proxy Mode     │  │ API Mode       │                 │
│  │ (browser)      │  │ (token-based)  │                 │
│  │                │  │                │                 │
│  │ - OAuth login  │  │ - /api/*       │                 │
│  │ - URL rewrite  │  │ - Stateless    │                 │
│  │ - Cookie       │  │ - Any client   │                 │
│  │   capture      │  │                │                 │
│  └───────┬────────┘  └───────┬────────┘                 │
│          │                   │                          │
│          ▼                   ▼                          │
│  ┌─────────────────────────────────────┐                │
│  │            Extraction Core          │                │
│  │                                      │                │
│  │  - Fetch YouTube pages              │                │
│  │  - Parse playerResponse             │                │
│  │  - Fetch + parse base.js            │                │
│  │  - Execute decipher via eval()      │                │
│  │  - Execute n-transform via eval()   │                │
│  │  - Build final URLs                 │                │
│  └─────────────────────────────────────┘                │
│                      │                                   │
│                      ▼                                   │
│  ┌─────────────────────────────────────┐                │
│  │         Workers KV Storage          │                │
│  │                                      │                │
│  │  tokens/{token} → {                 │                │
│  │    youtube_cookies: "...",          │                │
│  │    created_at: ...,                 │                │
│  │    last_used: ...                   │                │
│  │  }                                  │                │
│  └─────────────────────────────────────┘                │
└─────────────────────────────────────────────────────────┘
```

---

## Reference Implementations

Clone and study these for proxy and rewriting patterns:

**Ultraviolet** (https://github.com/titaniumnetwork-dev/Ultraviolet)
- URL rewriting in HTML, JS, CSS
- Request interception and forwarding
- Cookie domain rewriting
- Google OAuth redirect chain handling

**Womginx** (https://github.com/nicehulv/womginx)
- Simpler architecture, closer to stateless
- Google OAuth specifically addressed

**Rammerhead** (https://github.com/nicehulv/rammerhead)
- Alternative URL encoding approach
- Session management patterns

For extraction logic, reference:

**yt-dlp** (https://github.com/yt-dlp/yt-dlp)
- `yt_dlp/extractor/youtube.py` — playerResponse parsing
- `yt_dlp/jsinterp.py` — JS interpreter patterns (though we use native eval)
- Signature and n-parameter extraction patterns

---

## Authentication Model

### Device Flow Pattern

1. User visits the service in a browser
2. User clicks login, enters proxied YouTube/Google OAuth flow
3. Proxy captures YouTube cookies on successful auth
4. Service generates an API token, stores cookies in KV
5. User receives token (displayed on page)
6. User can now call API from any HTTP client using that token

### Token Properties

- Format: cryptographically random string (32-byte hex)
- Serves as both identity and authorization
- Maps to stored YouTube cookies in KV
- Long-lived until revoked or cookies expire

### KV Storage Schema

```
Key: tokens/{token}
Value: {
  youtube_cookies: string,
  created_at: ISO timestamp,
  last_used: ISO timestamp,
  label: string (optional)
}
```

---

## URL Routes

### Proxy Mode (browser login flow)

| Route | Purpose |
|-------|---------|
| `GET /` | Landing page with login button |
| `GET /login` | Redirects to proxied Google OAuth |
| `GET /proxy/{encoded_url}` | Rewriting proxy for OAuth flow |
| `GET /token` | Displays API token after successful login |
| `POST /token/revoke` | Revokes a token |

### API Mode (token-based, any HTTP client)

| Route | Purpose |
|-------|---------|
| `GET /api/subtitles` | Extract subtitles |
| `GET /api/video` | Extract video metadata and URLs |
| `GET /api/playlist` | Extract playlist contents |
| `GET /api/thumbnail` | Get thumbnail URL |
| `GET /api/status` | Check token validity |

All API routes require: `Authorization: Bearer {token}`

---

## Proxy Implementation

### URL Encoding Scheme

```
Original: https://www.youtube.com/watch?v=xyz
Proxied:  https://your-worker.dev/proxy/https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3Dxyz
```

### Rewriting Rules

The proxy must rewrite:

1. **HTML attributes**: `href`, `src`, `action`, `data-*` URLs
2. **JavaScript strings**: URL literals, template strings
3. **CSS**: `url()` references
4. **HTTP headers**: `Location`, `Set-Cookie` domain

Domains to rewrite:
- `youtube.com`, `www.youtube.com`, `m.youtube.com`
- `accounts.google.com`
- `googlevideo.com` and `*.googlevideo.com`
- `ytimg.com`, `i.ytimg.com`
- `ggpht.com`
- `googleusercontent.com`

### Injected JavaScript

Inject at top of proxied HTML to catch dynamic URLs:

```js
(function() {
  const PROXY = 'https://your-worker.dev/proxy/';
  const rewrite = (url) => PROXY + encodeURIComponent(url);
  
  // Patch fetch
  const _fetch = fetch;
  window.fetch = (url, opts) => _fetch(rewrite(url), opts);
  
  // Patch XMLHttpRequest
  const _open = XMLHttpRequest.prototype.open;
  XMLHttpRequest.prototype.open = function(m, url, ...a) {
    return _open.call(this, m, rewrite(url), ...a);
  };
  
  // Patch WebSocket, location, history, etc.
})();
```

### Cookie Handling

1. Google/YouTube sets cookies on their domains
2. Proxy intercepts `Set-Cookie` headers
3. Rewrites domain to Worker's domain
4. On successful OAuth completion, captures all cookies
5. Stores in KV keyed by generated token

---

## Extraction Core

### Player Response Extraction

1. Fetch YouTube watch page with cookies
2. Parse HTML for `ytInitialPlayerResponse` variable
3. Extract as JSON

### Base.js Parsing

1. Extract player JS URL from page
2. Fetch base.js
3. Locate decipher function via regex patterns
4. Locate n-transform function via regex patterns
5. Extract function bodies as strings

### Signature Deciphering

YouTube obfuscates video URLs with a signature parameter. The decipher function is in base.js.

Pattern to locate:
```
/\b([a-zA-Z0-9$]+)\s*=\s*function\(\s*a\s*\)\s*{\s*a\s*=\s*a\.split\(\s*""\s*\)/
```

Execute via:
```js
const decipherFn = new Function('sig', extractedFunctionBody);
const deciphered = decipherFn(encryptedSig);
```

### N-Parameter Transform

YouTube throttles downloads if `n` parameter isn't transformed.

Pattern to locate:
```
/\b([a-zA-Z0-9$]+)\s*=\s*function\(\s*a\s*\)\s*{\s*var\s+b=a\.split\(""\)/
```

Execute similarly via `new Function()`.

### Building Final URLs

1. Start with format URL from playerResponse
2. Apply deciphered signature
3. Apply transformed n-parameter
4. Result is direct downloadable URL

---

## API Specifications

### Authentication

All `/api/*` endpoints require:
```
Authorization: Bearer {token}
```

### GET /api/subtitles

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `v` | Yes | Video ID |
| `lang` | No | Language code (omit for list only) |
| `format` | No | `vtt`, `srt`, `json3` (default: `vtt`) |

**Response:**
```json
{
  "video_id": "...",
  "title": "...",
  "available": [
    { "code": "en", "name": "English", "auto": false }
  ],
  "subtitles": {
    "lang": "en",
    "format": "vtt",
    "content": "WEBVTT\n\n..."
  }
}
```

### GET /api/video

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `v` | Yes | Video ID |

**Response:**
```json
{
  "video_id": "...",
  "title": "...",
  "duration_seconds": 212,
  "thumbnail": "https://...",
  "formats": [
    {
      "itag": 137,
      "quality": "1080p",
      "mime_type": "video/mp4",
      "has_video": true,
      "has_audio": false,
      "url": "https://..."
    }
  ],
  "recommended": {
    "video_itag": 137,
    "audio_itag": 140,
    "needs_muxing": true
  },
  "subtitles": [...]
}
```

### GET /api/playlist

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `list` | Yes | Playlist ID |

**Response:**
```json
{
  "playlist_id": "...",
  "title": "...",
  "videos": [
    { "video_id": "...", "title": "...", "duration_seconds": 180 }
  ]
}
```

### GET /api/thumbnail

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `v` | Yes | Video ID |
| `quality` | No | `default`, `medium`, `high`, `max` |

**Response:**
```json
{
  "video_id": "...",
  "url": "https://i.ytimg.com/..."
}
```

### GET /api/status

**Response:**
```json
{
  "valid": true,
  "created_at": "...",
  "last_used": "...",
  "cookies_valid": true
}
```

---

## Cookie Refresh Strategy

1. Every API request uses stored cookies
2. Check response for `Set-Cookie` headers
3. If present, merge and update KV
4. Update `last_used` timestamp

If cookies become invalid, return 403 with `cookies_expired` error.

---

## Error Handling

| Scenario | Status | Code |
|----------|--------|------|
| Missing auth header | 401 | `missing_auth` |
| Invalid token | 401 | `invalid_token` |
| Expired cookies | 403 | `cookies_expired` |
| Video not found | 404 | `video_not_found` |
| Private video | 403 | `video_private` |
| Rate limited | 429 | `rate_limited` |
| Extraction failed | 500 | `extraction_failed` |

---

## Security Considerations

1. Tokens are secrets — use Authorization header only
2. HTTPS only
3. Rate limiting per token
4. Cookie isolation per token

---

## Testing Checklist

- [ ] Proxy login flow completes
- [ ] Token issued and stored in KV
- [ ] Public video extraction works
- [ ] Age-restricted video works with cookies
- [ ] Subtitle extraction returns valid VTT
- [ ] Playlist extraction returns all videos
- [ ] Signature deciphering works
- [ ] N-transform works
- [ ] Cookie refresh on YouTube response
- [ ] Invalid token returns 401
- [ ] Expired cookies return 403

---

## Deployment

**Requirements:**
- Cloudflare Workers (free tier may work for low volume)
- Workers KV namespace

**wrangler.toml:**
```
name = "youtube-extractor"
main = "src/index.ts"
compatibility_date = "2024-01-01"

[[kv_namespaces]]
binding = "TOKENS"
id = "..."
```

---

## File Structure

```
/
├── src/
│   ├── index.ts
│   ├── router.ts
│   ├── auth/
│   │   ├── login.ts
│   │   ├── token.ts
│   │   └── oauth.ts
│   ├── proxy/
│   │   ├── handler.ts
│   │   ├── rewriter.ts
│   │   └── inject.ts
│   ├── api/
│   │   ├── subtitles.ts
│   │   ├── video.ts
│   │   ├── playlist.ts
│   │   └── thumbnail.ts
│   ├── extraction/
│   │   ├── player.ts
│   │   ├── decipher.ts
│   │   ├── ntransform.ts
│   │   └── formats.ts
│   ├── storage/
│   │   └── kv.ts
│   └── utils/
│       ├── cookies.ts
│       └── errors.ts
├── wrangler.toml
└── README.md
```

You're right. Let me add the web interface specification to complement the API.

---

## Web Interface

### Routes (Browser)

| Route | Purpose |
|-------|---------|
| `GET /` | Landing page with URL input |
| `GET /video/{video_id}` | Video detail page |
| `GET /playlist/{playlist_id}` | Playlist page with video list |

### Landing Page (`/`)

- URL input field (accepts youtube.com URLs, youtu.be, playlist URLs)
- Parse input to detect video vs playlist
- Redirect to appropriate detail page
- Login button if not authenticated
- Display current token status if logged in

### Video Page (`/video/{video_id}`)

Displays:
- Video title and thumbnail
- Duration
- Available subtitle languages (each with download button)
- Available video qualities (table showing itag, resolution, format, has audio)
- Recommended download option (best video + best audio)
- Download buttons:
  - "Download Subtitles" dropdown (select language + format)
  - "Download Video" (if format has audio)
  - "Download Video + Audio" (triggers client-side muxing)

If muxing required:
- Browser fetches both streams via `/api/video` URLs
- Uses ffmpeg.wasm to mux
- Uses File System Access API to save

### Playlist Page (`/playlist/{playlist_id}`)

Displays:
- Playlist title
- Video count
- List of videos with:
  - Thumbnail
  - Title
  - Duration
  - Link to individual video page
- "Download All Subtitles" button (zip file)
- Option to queue batch downloads

---

## Updated File Structure

```
/
├── src/
│   ├── index.ts
│   ├── router.ts
│   ├── pages/
│   │   ├── landing.ts
│   │   ├── video.ts
│   │   └── playlist.ts
│   ├── auth/
│   │   └── ...
│   ├── proxy/
│   │   └── ...
│   ├── api/
│   │   └── ...
│   ├── extraction/
│   │   └── ...
│   ├── storage/
│   │   └── kv.ts
│   └── utils/
│       └── ...
├── static/
│   ├── styles.css
│   └── client.js        # ffmpeg.wasm + muxing + download logic
├── wrangler.toml
└── README.md
```

---

## Client-Side JavaScript (`static/client.js`)

Responsibilities:
- Parse pasted YouTube URLs
- Redirect to video/playlist pages
- Fetch from `/api/*` endpoints
- Handle muxing via ffmpeg.wasm when needed
- Trigger downloads via File System Access API or fallback

---

## URL Parsing Logic

Accept these formats:
```
https://www.youtube.com/watch?v=VIDEO_ID
https://youtu.be/VIDEO_ID
https://m.youtube.com/watch?v=VIDEO_ID
https://www.youtube.com/playlist?list=PLAYLIST_ID
https://www.youtube.com/watch?v=VIDEO_ID&list=PLAYLIST_ID
```

Extract `v` param → route to `/video/{id}`
Extract `list` param → route to `/playlist/{id}`

I'll add comment support to the specification:

---

## Additional API Endpoints

### GET /api/comments

Retrieve comments for a video.

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `v` | Yes | Video ID |
| `sort` | No | `top` or `newest` (default: `top`) |
| `continuation` | No | Token for pagination |

**Response:**
```json
{
  "video_id": "...",
  "comment_count": 1234,
  "comments": [
    {
      "id": "...",
      "author": "Username",
      "author_channel_id": "...",
      "text": "Comment text here",
      "likes": 42,
      "published": "2 days ago",
      "reply_count": 5,
      "replies_continuation": "..."
    }
  ],
  "continuation": "token_for_next_page"
}
```

### GET /api/comments/replies

Retrieve replies to a specific comment.

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `continuation` | Yes | Replies continuation token from parent comment |

**Response:**
```json
{
  "replies": [
    {
      "id": "...",
      "author": "...",
      "text": "...",
      "likes": 10,
      "published": "1 day ago"
    }
  ],
  "continuation": "..."
}
```

### POST /api/comments

Post a new comment (requires authenticated token).

**Body:**
```json
{
  "video_id": "...",
  "text": "Comment text"
}
```

**Response:**
```json
{
  "success": true,
  "comment_id": "..."
}
```

### POST /api/comments/reply

Reply to an existing comment.

**Body:**
```json
{
  "comment_id": "...",
  "text": "Reply text"
}
```

**Response:**
```json
{
  "success": true,
  "reply_id": "..."
}
```

---

## Extraction Notes

Comments are fetched via YouTube's `/youtubei/v1/next` endpoint. Requires proper `INNERTUBE_API_KEY` and context headers. Posting comments requires valid authenticated session cookies.

---

## Updated File Structure Addition

```
├── src/
│   ├── api/
│   │   ├── comments.ts      # GET comments + replies
│   │   └── post-comment.ts  # POST comment + reply
│   ├── extraction/
│   │   └── comments.ts      # Comment parsing logic
```

I'll add these clarifications to the specification:

---

## Media Handling

### Web Codecs API (preferred)

Use WebCodecs where available for:
- Demuxing MP4/WebM containers
- Decoding video/audio frames
- Re-encoding if needed
- Lower overhead than ffmpeg.wasm for simple operations

Check availability:
```js
if ('VideoDecoder' in window && 'AudioDecoder' in window) {
  // Use WebCodecs
} else {
  // Fall back to ffmpeg.wasm
}
```

### ffmpeg.wasm (fallback)

Required for:
- Muxing separate video + audio streams into single file
- Subtitle embedding
- Format conversions WebCodecs can't handle
- Browsers without WebCodecs support

Dependencies:
```json
{
  "@ffmpeg/ffmpeg": "^0.12.x",
  "@ffmpeg/util": "^0.12.x"
}
```

---

## Embedded Video Player Component

### Login-Gated Video Element

A custom element that:
1. Initially shows login prompt if user not authenticated
2. After login, replaces with functional video player
3. Plays video through proxy, handling cookies transparently

Usage:
```html
<yt-player video-id="dQw4w9WgXcQ"></yt-player>
```

Behavior:

| State | Display |
|-------|---------|
| No token | "Login to watch" button |
| Token exists, cookies valid | Video player with controls |
| Token exists, cookies expired | "Session expired, re-login" prompt |

### Implementation

```js
class YTPlayer extends HTMLElement {
  connectedCallback() {
    const videoId = this.getAttribute('video-id');
    const token = localStorage.getItem('yt_extractor_token');
    
    if (!token) {
      this.renderLoginPrompt();
    } else {
      this.initPlayer(videoId, token);
    }
  }
  
  renderLoginPrompt() {
    this.innerHTML = `
      <div class="login-prompt">
        <button onclick="location.href='/login'">Login to watch</button>
      </div>
    `;
  }
  
  async initPlayer(videoId, token) {
    const data = await fetch(`/api/video?v=${videoId}`, {
      headers: { 'Authorization': `Bearer ${token}` }
    }).then(r => r.json());
    
    if (data.error === 'cookies_expired') {
      this.renderLoginPrompt();
      return;
    }
    
    // Find format with both video + audio, or use recommended
    const format = data.formats.find(f => f.has_video && f.has_audio) 
                   || data.recommended;
    
    this.innerHTML = `
      <video controls>
        <source src="${format.url}" type="${format.mime_type}">
      </video>
    `;
  }
}

customElements.define('yt-player', YTPlayer);
```

### Streaming Through Proxy

If direct URLs are blocked by CORS, stream through:
```
/proxy/media?token={token}&v={video_id}&itag={itag}
```

Worker streams bytes directly, no buffering.

---

## Updated File Structure

```
/
├── src/
│   ├── api/
│   │   └── media-stream.ts    # Proxied media streaming endpoint
│   └── ...
├── static/
│   ├── client.js
│   ├── yt-player.js           # Custom element
│   ├── muxer.js               # WebCodecs + ffmpeg.wasm logic
│   └── styles.css
└── ...
```

---

## Updated Testing Checklist

- [ ] WebCodecs muxing works in Chrome/Edge
- [ ] ffmpeg.wasm fallback works in Firefox/Safari
- [ ] `<yt-player>` shows login when unauthenticated
- [ ] `<yt-player>` plays video after login
- [ ] `<yt-player>` prompts re-login on expired cookies
- [ ] Media streaming through proxy works
- [ ] Large video playback doesn't buffer entire file

I'll add an option to force ffmpeg.wasm usage to the specification:

---

## Media Handling Options

### Force ffmpeg Mode

Allow explicit selection of muxing backend via:

1. **URL parameter**: `?muxer=ffmpeg`
2. **localStorage setting**: `yt_extractor_muxer = 'ffmpeg' | 'webcodecs' | 'auto'`
3. **UI toggle** in settings/preferences

### Selection Logic

```js
function getMuxer() {
  const override = localStorage.getItem('yt_extractor_muxer');
  
  if (override === 'ffmpeg') {
    return 'ffmpeg';
  }
  
  if (override === 'webcodecs' && 'VideoDecoder' in window) {
    return 'webcodecs';
  }
  
  // Auto: prefer WebCodecs, fall back to ffmpeg
  if ('VideoDecoder' in window && 'AudioDecoder' in window) {
    return 'webcodecs';
  }
  
  return 'ffmpeg';
}
```

### UI Addition

On video page, add settings panel or dropdown:

```
Muxing Backend: [Auto ▾]
                 ├── Auto (use WebCodecs if available)
                 ├── WebCodecs
                 └── ffmpeg.wasm (force)
```

### Testing Convenience

For testing, support query param override:

```
/video/dQw4w9WgXcQ?muxer=ffmpeg
```

This bypasses localStorage and forces ffmpeg for that session.

---

## Updated Testing Checklist Addition

- [ ] `?muxer=ffmpeg` forces ffmpeg.wasm even when WebCodecs available
- [ ] localStorage preference persists across sessions
- [ ] UI toggle updates localStorage correctly
- [ ] Auto mode correctly detects WebCodecs support