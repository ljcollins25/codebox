/**
 * Proxy handler for OAuth flow and URL rewriting
 */

import type { Env } from '../index';
import { rewriteHtml } from './rewriter';
import { getInjectedScript } from './inject';
import { rewriteSetCookie, extractCookiesFromResponse } from '../utils/cookies';

/**
 * Domains that should be proxied
 */
const PROXY_DOMAINS = [
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
  'googleapis.com',
  'google.com',
  'www.google.com',
];

/**
 * Check if a URL should be proxied
 */
function shouldProxy(url: string): boolean {
  try {
    const { hostname } = new URL(url);
    return PROXY_DOMAINS.some(d => hostname === d || hostname.endsWith('.' + d));
  } catch {
    return false;
  }
}

/**
 * Encode a URL for proxying
 */
export function encodeProxyUrl(url: string, proxyBase: string): string {
  return `${proxyBase}/proxy/${encodeURIComponent(url)}`;
}

/**
 * Decode a proxied URL
 */
export function decodeProxyUrl(encodedPath: string): string {
  // Remove /proxy/ prefix and decode
  const encoded = encodedPath.replace(/^\/proxy\//, '');
  return decodeURIComponent(encoded);
}

/**
 * XOR encode for /auth/ paths (like Ultraviolet)
 */
function xorEncode(str: string): string {
  const key = 2;
  try {
    return btoa(str.split('').map((c) => 
      String.fromCharCode(c.charCodeAt(0) ^ key)
    ).join(''));
  } catch {
    return encodeURIComponent(str);
  }
}

/**
 * XOR decode for /auth/ paths (like Ultraviolet)
 */
function xorDecode(encoded: string): string | null {
  const key = 2;
  try {
    const decoded = atob(encoded);
    return decoded.split('').map((c) => 
      String.fromCharCode(c.charCodeAt(0) ^ key)
    ).join('');
  } catch {
    return null;
  }
}

/**
 * Main proxy handler
 */
export async function handleProxy(
  request: Request,
  env: Env
): Promise<Response> {
  const url = new URL(request.url);
  const workerUrl = env.WORKER_URL || url.origin;
  
  let targetUrl: string;
  
  // Check if this is an /auth/ path (XOR encoded) or /proxy/ path (URL encoded)
  if (url.pathname.startsWith('/auth/')) {
    const encoded = url.pathname.replace(/^\/auth\//, '');
    if (!encoded) {
      return new Response('Missing target URL', { status: 400 });
    }
    const decoded = xorDecode(encoded);
    if (!decoded) {
      return new Response('Invalid encoded URL', { status: 400 });
    }
    targetUrl = decoded;
  } else {
    // Standard /proxy/ path
    const targetUrlEncoded = url.pathname.replace(/^\/proxy\//, '');
    if (!targetUrlEncoded) {
      return new Response('Missing target URL', { status: 400 });
    }
    targetUrl = decodeURIComponent(targetUrlEncoded);
  }
  
  // Validate target
  if (!shouldProxy(targetUrl)) {
    return new Response('Target URL not allowed', { status: 403 });
  }
  // Build proxied request
  const targetUrlObj = new URL(targetUrl);
  
  // Copy query params from original request
  url.searchParams.forEach((value, key) => {
    if (key !== '_') { // Skip cache busters
      targetUrlObj.searchParams.set(key, value);
    }
  });

  // Build headers
  const headers = new Headers();
  
  // Copy relevant headers from original request
  const headersToCopy = [
    'accept',
    'accept-language',
    'content-type',
    'content-length',
    'cookie',
  ];
  
  for (const header of headersToCopy) {
    const value = request.headers.get(header);
    if (value) {
      headers.set(header, value);
    }
  }

  // Set proper origin and referer
  headers.set('Origin', targetUrlObj.origin);
  headers.set('Referer', targetUrlObj.origin + '/');
  headers.set('User-Agent', 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36');

  // Make request to target
  const fetchOptions: RequestInit = {
    method: request.method,
    headers,
    redirect: 'manual', // Handle redirects ourselves
  };

  if (request.method !== 'GET' && request.method !== 'HEAD') {
    fetchOptions.body = request.body;
  }

  const response = await fetch(targetUrlObj.toString(), fetchOptions);

  // Determine if we're using XOR encoding (for /auth/ paths)
  const useXor = url.pathname.startsWith('/auth/');
  const prefix = useXor ? '/auth/' : '/proxy/';

  // Handle redirects - rewrite Location header
  if (response.status >= 300 && response.status < 400) {
    const location = response.headers.get('location');
    if (location) {
      const absoluteLocation = new URL(location, targetUrlObj).href;
      let proxiedLocation: string;
      if (useXor) {
        proxiedLocation = workerUrl + '/auth/' + xorEncode(absoluteLocation);
      } else {
        proxiedLocation = encodeProxyUrl(absoluteLocation, workerUrl);
      }
      
      return new Response(null, {
        status: response.status,
        headers: {
          'Location': proxiedLocation,
        },
      });
    }
  }

  // Process response
  const contentType = response.headers.get('content-type') || '';
  let body: BodyInit | null = null;

  if (contentType.includes('text/html')) {
    // Rewrite HTML
    const html = await response.text();
    const proxyBase = workerUrl;
    const rewritten = rewriteHtml(html, targetUrlObj.href, proxyBase, useXor);
    
    // Inject client-side script
    const injectedScript = getInjectedScript(proxyBase, targetUrlObj.origin, useXor);
    body = rewritten.replace(/<head[^>]*>/i, `$&${injectedScript}`);
  } else if (contentType.includes('javascript') || contentType.includes('application/x-javascript')) {
    // Rewrite JavaScript (basic URL rewriting)
    const js = await response.text();
    body = rewriteJsUrls(js, targetUrlObj.href, workerUrl, useXor);
  } else if (contentType.includes('text/css')) {
    // Rewrite CSS URLs
    const css = await response.text();
    body = rewriteCssUrls(css, targetUrlObj.href, workerUrl, useXor);
  } else {
    // Pass through other content types
    body = response.body;
  }

  // Build response headers
  const responseHeaders = new Headers();
  
  // Copy safe headers
  const safeHeaders = [
    'content-type',
    'cache-control',
    'expires',
    'last-modified',
    'etag',
  ];
  
  for (const header of safeHeaders) {
    const value = response.headers.get(header);
    if (value) {
      responseHeaders.set(header, value);
    }
  }

  // Rewrite Set-Cookie headers
  const setCookies = extractCookiesFromResponse(response);
  for (const cookie of setCookies) {
    const rewritten = rewriteSetCookie(cookie, new URL(workerUrl).hostname);
    responseHeaders.append('Set-Cookie', rewritten);
  }

  // Allow cross-origin for API usage
  responseHeaders.set('Access-Control-Allow-Origin', '*');
  responseHeaders.set('Access-Control-Allow-Credentials', 'true');

  return new Response(body, {
    status: response.status,
    headers: responseHeaders,
  });
}

/**
 * Basic JS URL rewriting (for string literals)
 */
function rewriteJsUrls(js: string, baseUrl: string, proxyBase: string, useXor: boolean = false): string {
  // This is a simplified version - full implementation would need a JS parser
  const urlPatterns = [
    // Quoted strings with http/https URLs
    /"(https?:\/\/[^"]+)"/g,
    /'(https?:\/\/[^']+)'/g,
  ];

  let result = js;
  
  for (const pattern of urlPatterns) {
    result = result.replace(pattern, (match, url) => {
      if (shouldProxy(url)) {
        let proxied: string;
        if (useXor) {
          proxied = proxyBase + '/auth/' + xorEncode(url);
        } else {
          proxied = encodeProxyUrl(url, proxyBase);
        }
        return match.replace(url, proxied);
      }
      return match;
    });
  }

  return result;
}

/**
 * CSS URL rewriting
 */
function rewriteCssUrls(css: string, baseUrl: string, proxyBase: string, useXor: boolean = false): string {
  // Rewrite url() references
  return css.replace(/url\s*\(\s*["']?([^"')]+)["']?\s*\)/gi, (match, url) => {
    if (url.startsWith('data:') || url.startsWith('blob:')) {
      return match;
    }
    
    try {
      const absoluteUrl = new URL(url, baseUrl).href;
      if (shouldProxy(absoluteUrl)) {
        let proxied: string;
        if (useXor) {
          proxied = proxyBase + '/auth/' + xorEncode(absoluteUrl);
        } else {
          proxied = encodeProxyUrl(absoluteUrl, proxyBase);
        }
        return `url("${proxied}")`;
      }
    } catch {
      // Invalid URL, leave unchanged
    }
    
    return match;
  });
}
