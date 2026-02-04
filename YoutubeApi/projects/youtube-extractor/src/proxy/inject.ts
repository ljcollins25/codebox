/**
 * Injected JavaScript for client-side request interception
 */

/**
 * Get the script to inject into proxied HTML pages
 */
export function getInjectedScript(proxyBase: string, currentOrigin: string): string {
  return `
<script>
(function() {
  'use strict';
  
  const PROXY_BASE = ${JSON.stringify(proxyBase)};
  const CURRENT_ORIGIN = ${JSON.stringify(currentOrigin)};
  
  // URL patterns that should be proxied
  const PROXY_DOMAINS = [
    'youtube.com',
    'www.youtube.com',
    'm.youtube.com',
    'accounts.google.com',
    'googlevideo.com',
    'ytimg.com',
    'i.ytimg.com',
    'ggpht.com',
    'googleusercontent.com',
    'googleapis.com',
    'google.com',
    'www.google.com',
  ];
  
  function shouldProxy(url) {
    try {
      const hostname = new URL(url, CURRENT_ORIGIN).hostname;
      return PROXY_DOMAINS.some(d => hostname === d || hostname.endsWith('.' + d));
    } catch {
      return false;
    }
  }
  
  function rewriteUrl(url) {
    if (!url) return url;
    if (typeof url !== 'string') {
      if (url instanceof URL) url = url.href;
      else return url;
    }
    
    // Skip special URLs
    if (url.startsWith('data:') || 
        url.startsWith('blob:') || 
        url.startsWith('javascript:') ||
        url.startsWith('#')) {
      return url;
    }
    
    try {
      const absolute = new URL(url, CURRENT_ORIGIN).href;
      if (shouldProxy(absolute)) {
        return PROXY_BASE + '/proxy/' + encodeURIComponent(absolute);
      }
    } catch (e) {
      // Invalid URL
    }
    
    return url;
  }
  
  // Store originals
  const originalFetch = window.fetch;
  const originalXHROpen = XMLHttpRequest.prototype.open;
  const originalXHRSend = XMLHttpRequest.prototype.send;
  
  // Patch fetch
  window.fetch = function(input, init) {
    if (typeof input === 'string') {
      input = rewriteUrl(input);
    } else if (input instanceof Request) {
      const rewritten = rewriteUrl(input.url);
      if (rewritten !== input.url) {
        input = new Request(rewritten, input);
      }
    } else if (input instanceof URL) {
      input = rewriteUrl(input.href);
    }
    return originalFetch.call(this, input, init);
  };
  
  // Patch XMLHttpRequest
  XMLHttpRequest.prototype.open = function(method, url, ...args) {
    this._url = url;
    return originalXHROpen.call(this, method, rewriteUrl(url), ...args);
  };
  
  // Patch window.open
  const originalWindowOpen = window.open;
  window.open = function(url, ...args) {
    return originalWindowOpen.call(this, rewriteUrl(url), ...args);
  };
  
  // Patch form submissions
  document.addEventListener('submit', function(e) {
    const form = e.target;
    if (form.action) {
      form.action = rewriteUrl(form.action);
    }
  }, true);
  
  // Patch anchor clicks
  document.addEventListener('click', function(e) {
    const anchor = e.target.closest('a');
    if (anchor && anchor.href) {
      const rewritten = rewriteUrl(anchor.href);
      if (rewritten !== anchor.href) {
        anchor.href = rewritten;
      }
    }
  }, true);
  
  // Patch history.pushState and replaceState
  const originalPushState = history.pushState;
  const originalReplaceState = history.replaceState;
  
  history.pushState = function(state, title, url) {
    return originalPushState.call(this, state, title, rewriteUrl(url));
  };
  
  history.replaceState = function(state, title, url) {
    return originalReplaceState.call(this, state, title, rewriteUrl(url));
  };
  
  // Patch location assignments
  const locationDescriptor = Object.getOwnPropertyDescriptor(window, 'location');
  
  // Create a Proxy for location to intercept assignments
  const locationHandler = {
    set(target, prop, value) {
      if (prop === 'href' || prop === 'assign' || prop === 'replace') {
        if (typeof value === 'function') {
          return Reflect.set(target, prop, function(url) {
            return value.call(target, rewriteUrl(url));
          });
        }
        return Reflect.set(target, prop, rewriteUrl(value));
      }
      return Reflect.set(target, prop, value);
    }
  };
  
  // Patch document.write to rewrite URLs in written content
  const originalDocumentWrite = document.write;
  document.write = function(html) {
    // Basic URL rewriting in written HTML (simplified)
    if (typeof html === 'string') {
      html = html.replace(/(href|src|action)=["']([^"']+)["']/gi, function(match, attr, url) {
        return attr + '="' + rewriteUrl(url) + '"';
      });
    }
    return originalDocumentWrite.call(this, html);
  };
  
  // Signal that proxy is active (for cookie capture)
  window.__PROXY_ACTIVE__ = true;
  window.__PROXY_BASE__ = PROXY_BASE;
  
  console.log('[Proxy] Client-side interception active');
})();
</script>
`;
}
