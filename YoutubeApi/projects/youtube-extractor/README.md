# YouTube Extraction Service

A Cloudflare Worker-based service that extracts subtitles, video URLs, and metadata from YouTube.

## Features

- 📝 **Subtitles** - Download subtitles in VTT, SRT, or JSON formats
- 🎥 **Video Info** - Get video metadata and direct download URLs
- 📋 **Playlists** - Extract all videos from a playlist
- 💬 **Comments** - Retrieve video comments and replies
- 🔐 **Authentication** - Device-flow OAuth for accessing restricted content

## Quick Start

### Prerequisites

- Node.js 18+
- Cloudflare account with Workers enabled
- Wrangler CLI (`npm install -g wrangler`)

### Setup

1. **Clone and install dependencies:**
   ```bash
   cd projects/youtube-extractor
   npm install
   ```

2. **Configure KV namespaces:**
   ```bash
   # Create KV namespaces
   wrangler kv:namespace create "TOKENS"
   wrangler kv:namespace create "CACHE"
   ```
   
   Update `wrangler.toml` with the namespace IDs.

3. **Set environment variables:**
   ```bash
   # In wrangler.toml or via dashboard
   WORKER_URL = "https://your-worker.workers.dev"
   ```

4. **Deploy:**
   ```bash
   npm run deploy
   ```

### Development

```bash
npm run dev
```

## API Reference

All API endpoints require authentication:
```
Authorization: Bearer {TOKEN}
```

### GET /api/video

Get video metadata and download URLs.

```
GET /api/video?v={VIDEO_ID}
```

### GET /api/subtitles

Get subtitles for a video.

```
GET /api/subtitles?v={VIDEO_ID}&lang={LANG}&format={FORMAT}
```

Parameters:
- `v` (required) - Video ID
- `lang` (optional) - Language code (e.g., `en`, `es`)
- `format` (optional) - `vtt`, `srt`, or `json3` (default: `vtt`)

### GET /api/playlist

Get all videos in a playlist.

```
GET /api/playlist?list={PLAYLIST_ID}
```

### GET /api/comments

Get video comments.

```
GET /api/comments?v={VIDEO_ID}&sort={SORT}&continuation={TOKEN}
```

Parameters:
- `v` (required) - Video ID
- `sort` (optional) - `top` or `newest` (default: `top`)
- `continuation` (optional) - Pagination token

### GET /api/thumbnail

Get video thumbnail URL.

```
GET /api/thumbnail?v={VIDEO_ID}&quality={QUALITY}
```

Parameters:
- `v` (required) - Video ID
- `quality` (optional) - `default`, `medium`, `high`, `max` (default: `high`)

### GET /api/status

Check token validity and cookie status.

```
GET /api/status
```

## Authentication Flow

1. Visit the service in a browser
2. Click "Login" to start OAuth flow
3. Complete Google sign-in
4. Receive API token
5. Use token in API requests

## Project Structure

```
src/
├── index.ts          # Entry point
├── router.ts         # Request routing
├── api/              # API endpoints
├── auth/             # Authentication
├── extraction/       # YouTube parsing
├── pages/            # Web interface
├── proxy/            # Proxy/rewriting
├── storage/          # KV operations
└── utils/            # Utilities
static/
├── client.js         # Client library
├── muxer.js          # Media muxing
├── styles.css        # Stylesheet
└── yt-player.js      # Custom video element
```

## Notes

- Video URLs expire after a few hours
- Some content requires authentication (age-restricted, members-only)
- Rate limiting may apply for heavy usage
- Signature algorithms change periodically; patterns may need updates

## License

MIT
