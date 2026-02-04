/**
 * Injected JavaScript for client-side request interception
 */

/**
 * Get the script to inject into proxied HTML pages
 * Uses XOR encoding for /auth/ routes (like Ultraviolet)
 */
export function getInjectedScript(proxyBase: string, currentOrigin: string, useXor: boolean = false): string {
  const prefix = useXor ? '/auth/' : '/proxy/';
  
  return `
<script>
(function() {
  'use strict';
  
  const PROXY_BASE = ${JSON.stringify(proxyBase)};
  const CURRENT_ORIGIN = ${JSON.stringify(currentOrigin)};
  const USE_XOR = ${useXor};
  const PREFIX = ${JSON.stringify(prefix)};
  
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
    'ssl.gstatic.com',
    'gstatic.com',
    'accounts.youtube.com',
    'myaccount.google.com',
    'ogs.google.com',
    'play.google.com',
  ];
  
  function shouldProxy(url) {
    try {
      const hostname = new URL(url, CURRENT_ORIGIN).hostname;
      return PROXY_DOMAINS.some(d => hostname === d || hostname.endsWith('.' + d));
    } catch {
      return false;
    }
  }
  
  // XOR encode for /auth/ routes
  function xorEncode(str) {
    const key = 2;
    return btoa(str.split('').map(c => String.fromCharCode(c.charCodeAt(0) ^ key)).join(''));
  }
  
  function encodeUrl(url) {
    if (USE_XOR) {
      return xorEncode(url);
    }
    return encodeURIComponent(url);
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
        url.startsWith('#') ||
        url.startsWith('about:')) {
      return url;
    }
    
    // Skip already-proxied URLs
    if (url.includes(PREFIX)) {
      return url;
    }
    
    try {
      const absolute = new URL(url, CURRENT_ORIGIN).href;
      if (shouldProxy(absolute)) {
        return PROXY_BASE + PREFIX + encodeUrl(absolute);
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
  
  // MutationObserver to catch dynamically added elements
  const observer = new MutationObserver(function(mutations) {
    mutations.forEach(function(mutation) {
      mutation.addedNodes.forEach(function(node) {
        if (node.nodeType === 1) {
          // Rewrite attributes on new elements
          ['href', 'src', 'action', 'formaction', 'poster', 'data'].forEach(function(attr) {
            if (node[attr]) {
              const rewritten = rewriteUrl(node[attr]);
              if (rewritten !== node[attr]) {
                node.setAttribute(attr, rewritten);
              }
            }
          });
          
          // Check children too
          if (node.querySelectorAll) {
            node.querySelectorAll('[href],[src],[action],[formaction]').forEach(function(el) {
              ['href', 'src', 'action', 'formaction'].forEach(function(attr) {
                if (el[attr]) {
                  const rewritten = rewriteUrl(el[attr]);
                  if (rewritten !== el[attr]) {
                    el.setAttribute(attr, rewritten);
                  }
                }
              });
            });
          }
        }
      });
    });
  });
  
  if (document.documentElement) {
    observer.observe(document.documentElement, { childList: true, subtree: true });
  }
  
  // Signal that proxy is active (for cookie capture)
  window.__PROXY_ACTIVE__ = true;
  window.__PROXY_BASE__ = PROXY_BASE;
  
  // Detect YouTube login completion and notify parent
  function checkLoginComplete() {
    const url = window.location.href;
    // If we're on YouTube after login
    if (url.includes('youtube.com') && !url.includes('accounts.google.com')) {
      // Check for login indicators in cookies
      if (document.cookie.includes('SID=') || document.cookie.includes('SAPISID=')) {
        // Notify parent window
        if (window.parent !== window) {
          window.parent.postMessage({ type: 'AUTH_COMPLETE', cookies: document.cookie }, '*');
        }
      }
    }
  }
  
  // Check on load and periodically
  window.addEventListener('load', checkLoginComplete);
  setInterval(checkLoginComplete, 2000);
  
  console.log('[Proxy] Client-side interception active');
})();
</script>
`;
}
