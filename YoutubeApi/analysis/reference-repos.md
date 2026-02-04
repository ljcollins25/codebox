# Reference Repositories

## Overview

These repositories are cloned into `refs/` folder for reference during implementation. They are git-ignored and not committed to this repository.

---

## Ultraviolet

**Repository:** https://github.com/titaniumnetwork-dev/Ultraviolet
**Location:** `refs/Ultraviolet/`
**Purpose:** URL rewriting and proxy patterns

### Key Files to Study
- `/src/rewrite/` - URL rewriting logic
- `/src/uv.handler.js` - Request handling
- `/src/uv.config.js` - Configuration patterns
- Cookie domain rewriting
- Google OAuth redirect chain handling

### Relevant Patterns
- See [proxy-patterns.md](./proxy-patterns.md) for extracted patterns

---

## yt-dlp

**Repository:** https://github.com/yt-dlp/yt-dlp
**Location:** `refs/yt-dlp/`
**Purpose:** YouTube extraction logic

### Key Files to Study
- `/yt_dlp/extractor/youtube.py` - Main YouTube extractor
- `/yt_dlp/jsinterp.py` - JavaScript interpreter (for understanding patterns)
- Signature extraction patterns
- N-parameter transform patterns
- Player response parsing

### Relevant Patterns
- See [extraction-patterns.md](./extraction-patterns.md) for extracted patterns

---

## Additional References (Not Cloned)

### Womginx
**Repository:** https://github.com/nicehulv/womginx
**Purpose:** Simpler proxy architecture, Google OAuth handling

### Rammerhead
**Repository:** https://github.com/nicehulv/rammerhead
**Purpose:** Alternative URL encoding, session management

---

## Clone Commands

```bash
# Clone Ultraviolet
git clone https://github.com/titaniumnetwork-dev/Ultraviolet refs/Ultraviolet

# Clone yt-dlp
git clone https://github.com/yt-dlp/yt-dlp refs/yt-dlp
```
