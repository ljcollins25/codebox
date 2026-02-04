/**
 * Service Worker for YouTube OAuth proxy
 * 
 * This intercepts all fetch requests within the /auth/ scope and proxies them
 * through our worker, rewriting URLs and handling cookies.
 */

const SW_VERSION = '1.0.0';
const PROXY_PREFIX = '/auth/';
const BACKEND_PROXY = '/proxy/';

// Domains we need to proxy for OAuth
const PROXY_DOMAINS = [
  'accounts.google.com',
  'www.google.com',
  'google.com',
  'youtube.com',
  'www.youtube.com',
  'ssl.gstatic.com',
  'accounts.youtube.com',
  'myaccount.google.com',
  'ogs.google.com',
  'play.google.com',
];

// CSP headers to strip
const CSP_HEADERS = [
  'content-security-policy',
  'content-security-policy-report-only',
  'x-content-security-policy',
  'x-frame-options',
  'x-xss-protection',
];

/**
 * Simple XOR encoding for URLs (like Ultraviolet)
 */
function xorEncode(str) {
  const key = 2; // Simple key
  return btoa(str.split('').map((c, i) => 
    String.fromCharCode(c.charCodeAt(0) ^ key)
  ).join(''));
}

function xorDecode(encoded) {
  const key = 2;
  try {
    const decoded = atob(encoded);
    return decoded.split('').map((c, i) => 
      String.fromCharCode(c.charCodeAt(0) ^ key)
    ).join('');
  } catch {
    return null;
  }
}

/**
 * Check if URL should be proxied
 */
function shouldProxy(url) {
  try {
    const { hostname } = new URL(url);
    return PROXY_DOMAINS.some(d => hostname === d || hostname.endsWith('.' + d));
  } catch {
    return false;
  }
}

/**
 * Encode URL for our proxy
 */
function encodeUrl(url) {
  return PROXY_PREFIX + xorEncode(url);
}

/**
 * Decode URL from proxy path
 */
function decodeUrl(path) {
  const encoded = path.replace(PROXY_PREFIX, '');
  return xorDecode(encoded);
}

/**
 * Rewrite URL in content
 */
function rewriteUrl(url, baseUrl) {
  if (!url) return url;
  
  // Skip special URLs
  if (url.startsWith('data:') || 
      url.startsWith('blob:') || 
      url.startsWith('javascript:') ||
      url.startsWith('#') ||
      url.startsWith('about:')) {
    return url;
  }
  
  try {
    const absolute = new URL(url, baseUrl).href;
    if (shouldProxy(absolute)) {
      return encodeUrl(absolute);
    }
    return url;
  } catch {
    return url;
  }
}

/**
 * Rewrite HTML content
 */
function rewriteHtml(html, baseUrl) {
  // Rewrite common URL attributes
  const urlAttrs = [
    { attr: 'href', tags: ['a', 'link', 'area', 'base'] },
    { attr: 'src', tags: ['script', 'img', 'iframe', 'frame', 'embed', 'audio', 'video', 'source', 'track'] },
    { attr: 'action', tags: ['form'] },
    { attr: 'formaction', tags: ['button', 'input'] },
    { attr: 'poster', tags: ['video'] },
    { attr: 'data', tags: ['object'] },
  ];
  
  let result = html;
  
  for (const { attr, tags } of urlAttrs) {
    // Match attribute in any of the specified tags
    const pattern = new RegExp(
      `(<(?:${tags.join('|')})[^>]*\\s${attr}=["'])([^"']+)(["'])`,
      'gi'
    );
    
    result = result.replace(pattern, (match, prefix, url, suffix) => {
      const rewritten = rewriteUrl(url, baseUrl);
      return prefix + rewritten + suffix;
    });
  }
  
  // Also handle srcset
  result = result.replace(
    /(<(?:img|source)[^>]*\ssrcset=["'])([^"']+)(["'])/gi,
    (match, prefix, srcset, suffix) => {
      const rewritten = srcset.split(',').map(part => {
        const [url, ...rest] = part.trim().split(/\s+/);
        return [rewriteUrl(url, baseUrl), ...rest].join(' ');
      }).join(', ');
      return prefix + rewritten + suffix;
    }
  );
  
  // Rewrite inline styles with url()
  result = result.replace(
    /url\s*\(\s*["']?([^"')]+)["']?\s*\)/gi,
    (match, url) => {
      if (url.startsWith('data:')) return match;
      return `url("${rewriteUrl(url, baseUrl)}")`;
    }
  );
  
  return result;
}

/**
 * Rewrite JavaScript URLs (basic)
 */
function rewriteJs(js, baseUrl) {
  // Rewrite quoted URLs
  return js.replace(
    /(["'])(https?:\/\/[^"']+)\1/g,
    (match, quote, url) => {
      if (shouldProxy(url)) {
        return quote + encodeUrl(url) + quote;
      }
      return match;
    }
  );
}

/**
 * Rewrite CSS URLs
 */
function rewriteCss(css, baseUrl) {
  return css.replace(
    /url\s*\(\s*["']?([^"')]+)["']?\s*\)/gi,
    (match, url) => {
      if (url.startsWith('data:') || url.startsWith('blob:')) return match;
      return `url("${rewriteUrl(url, baseUrl)}")`;
    }
  );
}

/**
 * Get client injection script
 */
function getInjectionScript(baseUrl) {
  return `
<script>
(function() {
  'use strict';
  
  const PROXY_PREFIX = ${JSON.stringify(PROXY_PREFIX)};
  const BASE_URL = ${JSON.stringify(baseUrl)};
  
  const PROXY_DOMAINS = ${JSON.stringify(PROXY_DOMAINS)};
  
  function shouldProxy(url) {
    try {
      const hostname = new URL(url, BASE_URL).hostname;
      return PROXY_DOMAINS.some(d => hostname === d || hostname.endsWith('.' + d));
    } catch { return false; }
  }
  
  function xorEncode(str) {
    const key = 2;
    return btoa(str.split('').map(c => String.fromCharCode(c.charCodeAt(0) ^ key)).join(''));
  }
  
  function rewriteUrl(url) {
    if (!url || typeof url !== 'string') return url;
    if (url.startsWith('data:') || url.startsWith('blob:') || 
        url.startsWith('javascript:') || url.startsWith('#') || url.startsWith('about:')) {
      return url;
    }
    try {
      const absolute = new URL(url, BASE_URL).href;
      if (shouldProxy(absolute)) {
        return PROXY_PREFIX + xorEncode(absolute);
      }
    } catch {}
    return url;
  }
  
  // Store originals
  const origFetch = window.fetch.bind(window);
  const origXHROpen = XMLHttpRequest.prototype.open;
  const origWindowOpen = window.open.bind(window);
  
  // Patch fetch
  window.fetch = function(input, init) {
    if (typeof input === 'string') {
      input = rewriteUrl(input);
    } else if (input instanceof Request) {
      input = new Request(rewriteUrl(input.url), input);
    }
    return origFetch(input, init);
  };
  
  // Patch XHR
  XMLHttpRequest.prototype.open = function(method, url, ...args) {
    return origXHROpen.call(this, method, rewriteUrl(url), ...args);
  };
  
  // Patch window.open
  window.open = function(url, ...args) {
    return origWindowOpen(rewriteUrl(url), ...args);
  };
  
  // Intercept form submissions
  document.addEventListener('submit', function(e) {
    const form = e.target;
    if (form && form.action) {
      const newAction = rewriteUrl(form.action);
      if (newAction !== form.action) {
        form.action = newAction;
      }
    }
  }, true);
  
  // Intercept link clicks
  document.addEventListener('click', function(e) {
    const link = e.target.closest('a[href]');
    if (link) {
      const newHref = rewriteUrl(link.href);
      if (newHref !== link.href) {
        link.href = newHref;
      }
    }
  }, true);
  
  // Patch history API
  const origPushState = history.pushState.bind(history);
  const origReplaceState = history.replaceState.bind(history);
  
  history.pushState = function(state, title, url) {
    return origPushState(state, title, url ? rewriteUrl(url) : url);
  };
  
  history.replaceState = function(state, title, url) {
    return origReplaceState(state, title, url ? rewriteUrl(url) : url);
  };
  
  // MutationObserver to catch dynamically added elements
  const observer = new MutationObserver(function(mutations) {
    mutations.forEach(function(mutation) {
      mutation.addedNodes.forEach(function(node) {
        if (node.nodeType === 1) {
          // Rewrite href/src on new elements
          if (node.href) node.href = rewriteUrl(node.href);
          if (node.src) node.src = rewriteUrl(node.src);
          if (node.action) node.action = rewriteUrl(node.action);
          
          // Check children too
          node.querySelectorAll && node.querySelectorAll('[href],[src],[action]').forEach(function(el) {
            if (el.href) el.href = rewriteUrl(el.href);
            if (el.src) el.src = rewriteUrl(el.src);
            if (el.action) el.action = rewriteUrl(el.action);
          });
        }
      });
    });
  });
  
  observer.observe(document.documentElement, {
    childList: true,
    subtree: true
  });
  
  // Signal proxy is active
  window.__UV_PROXY__ = true;
  console.log('[UV Proxy] Client-side hooks installed');
})();
</script>
`;
}

// Install event
self.addEventListener('install', (event) => {
  console.log('[UV SW] Installing version', SW_VERSION);
  self.skipWaiting();
});

// Activate event  
self.addEventListener('activate', (event) => {
  console.log('[UV SW] Activating');
  event.waitUntil(clients.claim());
});

// Fetch event - main interception point
self.addEventListener('fetch', (event) => {
  const url = new URL(event.request.url);
  
  // Only intercept requests within our proxy prefix
  if (!url.pathname.startsWith(PROXY_PREFIX)) {
    return;
  }
  
  event.respondWith(handleProxyRequest(event.request));
});

/**
 * Handle proxied request
 */
async function handleProxyRequest(request) {
  const url = new URL(request.url);
  
  // Decode target URL
  const targetUrl = decodeUrl(url.pathname);
  
  if (!targetUrl) {
    return new Response('Invalid proxy URL', { status: 400 });
  }
  
  // Build target URL with query params
  const targetUrlObj = new URL(targetUrl);
  url.searchParams.forEach((value, key) => {
    targetUrlObj.searchParams.set(key, value);
  });
  
  // Build headers for the target request
  const headers = new Headers();
  
  // Copy relevant headers
  const copyHeaders = ['accept', 'accept-language', 'content-type', 'content-length'];
  for (const h of copyHeaders) {
    const val = request.headers.get(h);
    if (val) headers.set(h, val);
  }
  
  // Set proper origin/referer
  headers.set('origin', targetUrlObj.origin);
  headers.set('referer', targetUrlObj.origin + '/');
  headers.set('user-agent', 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36');
  
  // Get cookies from storage and add to request
  const cookies = await getCookies();
  if (cookies) {
    headers.set('cookie', cookies);
  }
  
  // Build fetch options
  const fetchOpts = {
    method: request.method,
    headers: headers,
    redirect: 'manual',
    credentials: 'omit',
  };
  
  if (request.method !== 'GET' && request.method !== 'HEAD') {
    fetchOpts.body = await request.arrayBuffer();
  }
  
  try {
    // Make request to actual target via our backend proxy
    // We use the backend proxy to avoid CORS issues
    const backendUrl = `${url.origin}${BACKEND_PROXY}${encodeURIComponent(targetUrlObj.toString())}`;
    
    const response = await fetch(backendUrl, {
      method: request.method,
      headers: request.headers,
      body: fetchOpts.body,
      redirect: 'manual',
    });
    
    // Handle redirects
    if (response.status >= 300 && response.status < 400) {
      const location = response.headers.get('location');
      if (location) {
        // Rewrite redirect location
        const rewrittenLocation = rewriteUrl(location, targetUrlObj.href);
        return new Response(null, {
          status: response.status,
          headers: { 'Location': rewrittenLocation }
        });
      }
    }
    
    // Store cookies
    const setCookie = response.headers.get('set-cookie');
    if (setCookie) {
      await storeCookies(setCookie);
    }
    
    // Process response based on content type
    const contentType = response.headers.get('content-type') || '';
    let body = null;
    
    if (contentType.includes('text/html')) {
      let html = await response.text();
      html = rewriteHtml(html, targetUrlObj.href);
      // Inject client-side script
      html = html.replace(/<head[^>]*>/i, '$&' + getInjectionScript(targetUrlObj.href));
      body = html;
    } else if (contentType.includes('javascript')) {
      body = rewriteJs(await response.text(), targetUrlObj.href);
    } else if (contentType.includes('text/css')) {
      body = rewriteCss(await response.text(), targetUrlObj.href);
    } else {
      body = response.body;
    }
    
    // Build response headers
    const respHeaders = new Headers();
    const safeHeaders = ['content-type', 'cache-control', 'expires', 'last-modified'];
    for (const h of safeHeaders) {
      const val = response.headers.get(h);
      if (val) respHeaders.set(h, val);
    }
    
    // Remove CSP headers
    for (const h of CSP_HEADERS) {
      respHeaders.delete(h);
    }
    
    return new Response(body, {
      status: response.status,
      headers: respHeaders
    });
    
  } catch (error) {
    console.error('[UV SW] Proxy error:', error);
    return new Response('Proxy error: ' + error.message, { status: 500 });
  }
}

/**
 * Cookie storage using IndexedDB
 */
const DB_NAME = 'uv-proxy-cookies';
const STORE_NAME = 'cookies';

async function openDb() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, 1);
    request.onerror = () => reject(request.error);
    request.onsuccess = () => resolve(request.result);
    request.onupgradeneeded = (event) => {
      const db = event.target.result;
      if (!db.objectStoreNames.contains(STORE_NAME)) {
        db.createObjectStore(STORE_NAME, { keyPath: 'id' });
      }
    };
  });
}

async function getCookies() {
  try {
    const db = await openDb();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(STORE_NAME, 'readonly');
      const store = tx.objectStore(STORE_NAME);
      const request = store.get('main');
      request.onerror = () => resolve(null);
      request.onsuccess = () => resolve(request.result?.value || null);
    });
  } catch {
    return null;
  }
}

async function storeCookies(setCookieHeader) {
  try {
    const db = await openDb();
    
    // Get existing cookies
    let existing = await getCookies() || '';
    const existingMap = new Map();
    existing.split(';').forEach(c => {
      const [name, ...rest] = c.trim().split('=');
      if (name) existingMap.set(name, rest.join('='));
    });
    
    // Parse new cookies
    const cookies = Array.isArray(setCookieHeader) ? setCookieHeader : [setCookieHeader];
    for (const cookie of cookies) {
      const [nameValue] = cookie.split(';');
      const [name, ...rest] = nameValue.split('=');
      if (name) existingMap.set(name.trim(), rest.join('='));
    }
    
    // Build cookie string
    const cookieStr = Array.from(existingMap.entries())
      .map(([name, value]) => `${name}=${value}`)
      .join('; ');
    
    // Store
    return new Promise((resolve, reject) => {
      const tx = db.transaction(STORE_NAME, 'readwrite');
      const store = tx.objectStore(STORE_NAME);
      const request = store.put({ id: 'main', value: cookieStr });
      request.onerror = () => reject(request.error);
      request.onsuccess = () => resolve();
    });
  } catch (error) {
    console.error('[UV SW] Cookie store error:', error);
  }
}

// Message handler for cookie retrieval
self.addEventListener('message', async (event) => {
  if (event.data.type === 'GET_COOKIES') {
    const cookies = await getCookies();
    event.source.postMessage({ type: 'COOKIES', cookies });
  } else if (event.data.type === 'CLEAR_COOKIES') {
    try {
      const db = await openDb();
      const tx = db.transaction(STORE_NAME, 'readwrite');
      tx.objectStore(STORE_NAME).delete('main');
      event.source.postMessage({ type: 'COOKIES_CLEARED' });
    } catch {}
  }
});
