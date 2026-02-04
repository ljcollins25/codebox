# YouTube Extraction Service - Agent Resume Document

## Purpose

This document enables another AI agent to resume or extend the YouTube Extraction Service implementation. It provides context, current state, and guidance for continuing development.

---

## Project Overview

**Goal:** Build a Cloudflare Worker-based service that extracts subtitles, video URLs, and metadata from YouTube using:
- Manual extraction (parsing player responses, executing decipher/n-transform)
- Ultraviolet-style proxying for login flow
- Device-flow authentication pattern

**Primary Specification:** [specs/youtube-extraction-service.md](specs/youtube-extraction-service.md)

---

## Current State

### Completed
- ✅ Analysis folder structure created
- ✅ Reference repositories cloned (Ultraviolet, yt-dlp)
- ✅ Extraction patterns documented
- ✅ Proxy patterns documented
- ✅ .gitignore configured
- ✅ Cloudflare Worker project initialized
- ✅ Router and core structure
- ✅ KV storage layer
- ✅ Extraction core (player, decipher, n-transform, formats, comments)
- ✅ Proxy/rewriter system with XOR encoding (like Ultraviolet)
- ✅ Authentication/login flow (dual method: OAuth + manual cookies)
- ✅ Service worker for OAuth proxy (`static/uv-sw.js`)
- ✅ All API endpoints
- ✅ Web interface pages
- ✅ Static client files
- ✅ Deployed to Cloudflare Workers

### Deployed At
**URL:** https://youtube-extractor.ref12cf.workers.dev

### Future Enhancements
- ⬜ Improved error recovery
- ⬜ Rate limiting
- ⬜ Caching layer
- ⬜ WebCodecs muxing implementation
- ⬜ Post comment functionality
- ⬜ Improve OAuth reliability (Google's anti-bot measures are challenging)

---

## Key Files & Locations

### Analysis & Documentation
| File | Purpose |
|------|---------|
| [analysis/project-overview.md](analysis/project-overview.md) | Implementation tracking |
| [analysis/reference-repos.md](analysis/reference-repos.md) | Reference repository info |
| [analysis/extraction-patterns.md](analysis/extraction-patterns.md) | YouTube extraction patterns |
| [analysis/proxy-patterns.md](analysis/proxy-patterns.md) | Proxy rewriting patterns |
| [analysis/implementation-notes.md](analysis/implementation-notes.md) | Design decisions & notes |

### Reference Code
| Location | Purpose |
|----------|---------|
| `refs/Ultraviolet/` | Proxy/rewriting reference |
| `refs/yt-dlp/yt_dlp/extractor/youtube.py` | YouTube extraction logic |
| `refs/yt-dlp/yt_dlp/jsinterp.py` | JS interpreter patterns |

### Implementation
| Location | Purpose |
|----------|---------|
| `projects/youtube-extractor/` | Main worker code |
| `projects/youtube-extractor/src/proxy/` | Proxy with XOR/URL encoding |
| `projects/youtube-extractor/src/auth/login.ts` | Dual auth (OAuth + manual) |
| `projects/youtube-extractor/static/uv-sw.js` | Service worker for OAuth |

---

## Architecture Summary

```
┌─────────────────────────────────────────────────────────┐
│                    Cloudflare Worker                     │
│                                                          │
│  Proxy Mode (browser)    │    API Mode (token-based)    │
│  - OAuth login           │    - /api/*                  │
│  - URL rewrite           │    - Stateless               │
│  - Cookie capture        │    - Any client              │
│                                                          │
│  ┌─────────────────────────────────────┐                │
│  │         Extraction Core             │                │
│  │  - Parse playerResponse             │                │
│  │  - Fetch + parse base.js            │                │
│  │  - Execute decipher via new Function│                │
│  │  - Execute n-transform              │                │
│  └─────────────────────────────────────┘                │
│                                                          │
│  ┌─────────────────────────────────────┐                │
│  │       Workers KV Storage            │                │
│  │  tokens/{token} → cookies, metadata │                │
│  └─────────────────────────────────────┘                │
└─────────────────────────────────────────────────────────┘
```

---

## API Endpoints to Implement

| Route | Method | Purpose |
|-------|--------|---------|
| `/` | GET | Landing page |
| `/login` | GET | Start OAuth flow |
| `/proxy/{url}` | GET | Rewriting proxy |
| `/token` | GET | Display token after login |
| `/api/subtitles` | GET | Extract subtitles |
| `/api/video` | GET | Extract video info |
| `/api/playlist` | GET | Extract playlist |
| `/api/thumbnail` | GET | Get thumbnail URL |
| `/api/comments` | GET | Get comments |
| `/api/status` | GET | Check token validity |
| `/video/{id}` | GET | Video detail page |
| `/playlist/{id}` | GET | Playlist page |

---

## Implementation Order (Recommended)

1. **Core Infrastructure** (router, KV storage, error handling)
2. **Extraction Engine** (can be tested independently)
3. **API Endpoints** (use extraction engine)
4. **Proxy System** (for auth flow)
5. **Authentication** (depends on proxy)
6. **Web Interface** (depends on API)

---

## Key Implementation Details

### Signature Deciphering

YouTube obfuscates video URLs. The decipher function is in `base.js`:

```javascript
// Pattern to locate
/\b([a-zA-Z0-9$]+)\s*=\s*function\(\s*a\s*\)\s*{\s*a\s*=\s*a\.split\(\s*""\s*\)/

// Execute via
const decipherFn = new Function('sig', extractedFunctionBody);
```

### N-Transform

YouTube throttles if `n` parameter isn't transformed:

```javascript
// Pattern to locate  
/\b([a-zA-Z0-9$]+)\s*=\s*function\(\s*a\s*\)\s*{\s*var\s+b=a\.split\(""\)/
```

### Cookie Handling

1. Capture cookies during OAuth
2. Store in KV: `tokens/{token} → { youtube_cookies, created_at, last_used }`
3. Send with every YouTube request
4. Update on new Set-Cookie headers

---

## Testing Priorities

1. Public video extraction without auth
2. Signature deciphering
3. N-transform
4. Subtitle extraction
5. Login flow (requires browser testing)
6. Authenticated extraction

---

## Commands

```bash
# Development
cd projects/youtube-extractor
npm install
npm run dev

# Deploy
npm run deploy

# Tail logs
npx wrangler tail
```

---

## Troubleshooting

### Extraction Fails
- Check if base.js URL extraction pattern is outdated
- Check if decipher/n-transform patterns need updating
- Reference yt-dlp for current patterns

### Login Fails
- Check proxy URL rewriting
- Check cookie domain handling
- Reference Ultraviolet for patterns

### Rate Limited
- Implement delays between requests
- Consider residential proxy
- Use authenticated requests

---

## External Dependencies

- Cloudflare Workers
- Cloudflare KV
- ffmpeg.wasm (client-side muxing)

---

## Contact & Resources

- **Spec:** [specs/youtube-extraction-service.md](specs/youtube-extraction-service.md)
- **Ultraviolet:** https://github.com/titaniumnetwork-dev/Ultraviolet
- **yt-dlp:** https://github.com/yt-dlp/yt-dlp
