# Proxy Rewriting Patterns from Ultraviolet

## Overview

Key patterns for URL rewriting proxy implementation based on Ultraviolet architecture.

---

## URL Encoding Scheme

### Basic Encoding

```javascript
// Encode URL for proxying
function encodeUrl(url) {
  return encodeURIComponent(url);
}

// Decode proxied URL
function decodeUrl(encoded) {
  return decodeURIComponent(encoded);
}

// Proxy URL format
// Original: https://www.youtube.com/watch?v=xyz
// Proxied:  https://worker.dev/proxy/https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3Dxyz
```

### XOR Encoding (Ultraviolet style)

```javascript
// More obfuscated encoding
function xorEncode(str, key = 2) {
  return str.split('').map(c => 
    String.fromCharCode(c.charCodeAt(0) ^ key)
  ).join('');
}
```

---

## HTML Rewriting

### Attributes to Rewrite

```javascript
const HTML_ATTRS = {
  'a': ['href'],
  'link': ['href'],
  'script': ['src'],
  'img': ['src', 'srcset'],
  'video': ['src', 'poster'],
  'audio': ['src'],
  'source': ['src', 'srcset'],
  'iframe': ['src'],
  'form': ['action'],
  'object': ['data'],
  'embed': ['src'],
  'base': ['href'],
  'meta': ['content'],  // for refresh redirects
  'input': ['formaction']
};
```

### HTML Rewriter Implementation

```javascript
class HTMLRewriter {
  constructor(proxyBase) {
    this.proxyBase = proxyBase;
  }

  rewrite(html, baseUrl) {
    // Use regex or proper HTML parser
    
    // Rewrite href attributes
    html = html.replace(
      /(<a[^>]*\s+href=["'])([^"']+)(["'])/gi,
      (match, pre, url, post) => {
        const absolute = new URL(url, baseUrl).href;
        return `${pre}${this.proxyBase}${encodeURIComponent(absolute)}${post}`;
      }
    );
    
    // Similar for other attributes...
    return html;
  }
}
```

---

## JavaScript Rewriting

### URL Patterns to Catch

```javascript
const JS_URL_PATTERNS = [
  // String literals
  /"(https?:\/\/[^"]+)"/g,
  /'(https?:\/\/[^']+)'/g,
  
  // Template literals
  /`(https?:\/\/[^`]+)`/g,
  
  // location assignments
  /location\s*=\s*["']([^"']+)["']/g,
  /location\.href\s*=\s*["']([^"']+)["']/g,
  
  // window.open
  /window\.open\s*\(\s*["']([^"']+)["']/g,
];
```

### Import/Require Rewriting

```javascript
// ES modules
/import\s+.*?\s+from\s+["']([^"']+)["']/g
/import\s*\(\s*["']([^"']+)["']\s*\)/g

// Dynamic imports
/import\s*\(\s*([^)]+)\s*\)/g
```

---

## CSS Rewriting

### URL Patterns

```javascript
const CSS_URL_PATTERNS = [
  // url() function
  /url\s*\(\s*["']?([^"')]+)["']?\s*\)/gi,
  
  // @import
  /@import\s+["']([^"']+)["']/gi,
  /@import\s+url\s*\(\s*["']?([^"')]+)["']?\s*\)/gi,
];
```

---

## Request Interception

### Headers to Modify

**Request headers:**
```javascript
const REQUEST_HEADERS_TO_REMOVE = [
  'x-forwarded-for',
  'x-real-ip',
  'cf-connecting-ip'
];

const REQUEST_HEADERS_TO_SET = {
  'origin': targetOrigin,
  'referer': targetReferer,
};
```

**Response headers:**
```javascript
const RESPONSE_HEADERS_TO_MODIFY = {
  'location': rewriteUrl,      // Redirects
  'set-cookie': rewriteCookie, // Domain rewriting
  'content-security-policy': remove,
  'x-frame-options': remove,
};
```

---

## Cookie Handling

### Domain Rewriting

```javascript
function rewriteSetCookie(header, workerDomain) {
  return header
    // Remove domain restriction
    .replace(/;\s*domain=[^;]+/gi, '')
    // Remove secure if worker is on different domain
    // .replace(/;\s*secure/gi, '')
    // Add SameSite
    .replace(/;\s*samesite=[^;]+/gi, '')
    + '; SameSite=None; Secure';
}
```

### Cookie Storage

```javascript
// Capture cookies during OAuth flow
function captureCookies(responseHeaders) {
  const cookies = {};
  
  responseHeaders.getAll('set-cookie').forEach(header => {
    const [nameValue] = header.split(';');
    const [name, value] = nameValue.split('=');
    cookies[name.trim()] = value;
  });
  
  return cookies;
}
```

---

## Injected Client Script

```javascript
// Inject at top of every proxied HTML page
const INJECTED_SCRIPT = `
<script>
(function() {
  const PROXY_BASE = '{{PROXY_BASE}}';
  const CURRENT_ORIGIN = '{{CURRENT_ORIGIN}}';
  
  // Rewrite helper
  const rewrite = (url) => {
    if (!url || url.startsWith('data:') || url.startsWith('blob:')) return url;
    try {
      const absolute = new URL(url, CURRENT_ORIGIN).href;
      return PROXY_BASE + encodeURIComponent(absolute);
    } catch {
      return url;
    }
  };
  
  // Patch fetch
  const originalFetch = window.fetch;
  window.fetch = function(input, init) {
    if (typeof input === 'string') {
      input = rewrite(input);
    } else if (input instanceof Request) {
      input = new Request(rewrite(input.url), input);
    }
    return originalFetch.call(this, input, init);
  };
  
  // Patch XMLHttpRequest
  const originalOpen = XMLHttpRequest.prototype.open;
  XMLHttpRequest.prototype.open = function(method, url, ...args) {
    return originalOpen.call(this, method, rewrite(url), ...args);
  };
  
  // Patch WebSocket
  const OriginalWebSocket = window.WebSocket;
  window.WebSocket = function(url, protocols) {
    // WebSocket needs special handling - ws:// or wss://
    const wsUrl = rewrite(url).replace(/^http/, 'ws');
    return new OriginalWebSocket(wsUrl, protocols);
  };
  
  // Patch location
  const locationProxy = new Proxy(location, {
    set(target, prop, value) {
      if (prop === 'href') {
        target[prop] = rewrite(value);
        return true;
      }
      target[prop] = value;
      return true;
    }
  });
  
  // More patches as needed...
})();
</script>
`;
```

---

## Domains to Proxy

```javascript
const YOUTUBE_DOMAINS = [
  'youtube.com',
  'www.youtube.com',
  'm.youtube.com',
  'music.youtube.com',
  'accounts.google.com',
  'googlevideo.com',
  'ytimg.com',
  'i.ytimg.com',
  'ggpht.com',
  'googleusercontent.com',
  'googleapis.com'
];

function shouldProxy(url) {
  try {
    const { hostname } = new URL(url);
    return YOUTUBE_DOMAINS.some(d => 
      hostname === d || hostname.endsWith('.' + d)
    );
  } catch {
    return false;
  }
}
```

---

## Service Worker Pattern

For more complete interception, Ultraviolet uses a Service Worker:

```javascript
// Register service worker for complete request interception
navigator.serviceWorker.register('/uv-sw.js', {
  scope: '/proxy/'
});
```

This allows intercepting all requests from the proxied page, including those made by iframes and workers.
