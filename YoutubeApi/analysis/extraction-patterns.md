# Extraction Patterns from yt-dlp

## Overview

Key patterns extracted from `yt-dlp/yt_dlp/extractor/youtube.py` for YouTube data extraction.

---

## Player Response Extraction

### Location in Page

The player response is embedded in the YouTube watch page as a JavaScript variable:

```javascript
var ytInitialPlayerResponse = {...};
```

### Regex Pattern

```javascript
/var\s+ytInitialPlayerResponse\s*=\s*(\{.+?\});/s
```

Alternative patterns:
```javascript
/ytInitialPlayerResponse\s*=\s*(\{.+?\})\s*;/
/window\["ytInitialPlayerResponse"\]\s*=\s*(\{.+?\});/
```

### Structure

```json
{
  "videoDetails": {
    "videoId": "...",
    "title": "...",
    "lengthSeconds": "...",
    "thumbnail": { "thumbnails": [...] }
  },
  "streamingData": {
    "formats": [...],
    "adaptiveFormats": [...],
    "expiresInSeconds": "..."
  },
  "captions": {
    "playerCaptionsTracklistRenderer": {
      "captionTracks": [...]
    }
  }
}
```

---

## Base.js URL Extraction

### Pattern

The player JavaScript URL is in the page:

```javascript
/"jsUrl":"(\/s\/player\/[^"]+\/base\.js)"/
```

Or in playerResponse:
```javascript
/"PLAYER_JS_URL":"([^"]+)"/
```

Full URL: `https://www.youtube.com{jsUrl}`

---

## Signature Decipher Function

### Locating the Function

Pattern to find the main decipher function:

```javascript
/\b([a-zA-Z0-9$]+)\s*=\s*function\(\s*a\s*\)\s*{\s*a\s*=\s*a\.split\(\s*""\s*\)/
```

This finds functions like:
```javascript
var Xo = function(a) {
  a = a.split("");
  Wo.Vh(a, 2);
  Wo.Gc(a, 4);
  Wo.Vh(a, 3);
  Wo.rT(a, 24);
  return a.join("")
};
```

### Helper Object Pattern

The decipher function calls a helper object with methods. Find it via:

```javascript
/var\s+([a-zA-Z0-9$]+)\s*=\s*\{[^}]*(?:reverse|splice|split)/
```

Helper methods are typically:
- `reverse` - Reverses the array
- `splice` - Removes elements
- `swap` - Swaps first element with element at index

### Full Extraction Strategy

1. Find decipher function name
2. Extract function body
3. Find helper object name from function body
4. Extract helper object
5. Combine into executable code

---

## N-Transform Function

### Purpose

YouTube throttles downloads if the `n` parameter isn't transformed. Each request needs `n` parameter to be run through a transform function.

### Locating Pattern

```javascript
/\b([a-zA-Z0-9$]+)\s*=\s*function\(\s*a\s*\)\s*\{\s*var\s+b\s*=\s*a\.split\(\s*""\s*\)/
```

Or newer pattern:
```javascript
/[a-zA-Z0-9$]+\s*=\s*function\(\s*a\s*\)\s*\{\s*var\s+b\s*=\s*a\.split\(\s*""\s*\),\s*c\s*=/
```

### Extraction

Similar to decipher - extract function body and any helpers.

---

## Format Information

### Format Fields

```json
{
  "itag": 137,
  "url": "https://...",
  "mimeType": "video/mp4; codecs=\"avc1.640028\"",
  "bitrate": 4000000,
  "width": 1920,
  "height": 1080,
  "quality": "hd1080",
  "qualityLabel": "1080p",
  "audioQuality": "AUDIO_QUALITY_MEDIUM",
  "signatureCipher": "s=...&sp=sig&url=..."
}
```

### Signature Cipher Parsing

When `signatureCipher` is present instead of direct `url`:

```javascript
const params = new URLSearchParams(format.signatureCipher);
const encryptedSig = params.get('s');
const sigParam = params.get('sp') || 'signature';
const baseUrl = params.get('url');

// After deciphering:
const finalUrl = `${baseUrl}&${sigParam}=${decipheredSig}`;
```

---

## Caption Tracks

### Location

```json
playerResponse.captions.playerCaptionsTracklistRenderer.captionTracks
```

### Structure

```json
{
  "baseUrl": "https://www.youtube.com/api/timedtext?...",
  "name": { "simpleText": "English" },
  "vssId": ".en",
  "languageCode": "en",
  "kind": "asr",  // "asr" = auto-generated
  "isTranslatable": true
}
```

### Subtitle URL Parameters

- `fmt=vtt` - WebVTT format
- `fmt=srv3` - YouTube's JSON format
- `fmt=ttml` - TTML format

---

## Comments Extraction

### Endpoint

`https://www.youtube.com/youtubei/v1/next`

### Request Body

```json
{
  "context": {
    "client": {
      "clientName": "WEB",
      "clientVersion": "2.20240101.00.00"
    }
  },
  "continuation": "..."
}
```

### Getting Initial Continuation Token

From watch page, find:
```javascript
/"continuationCommand":\s*\{\s*"token":\s*"([^"]+)"/
```

---

## Useful Constants

### Client Context

```json
{
  "client": {
    "clientName": "WEB",
    "clientVersion": "2.20240101.00.00",
    "hl": "en",
    "gl": "US"
  }
}
```

### API Key

Found in page as `INNERTUBE_API_KEY`:
```javascript
/"INNERTUBE_API_KEY":"([^"]+)"/
```
