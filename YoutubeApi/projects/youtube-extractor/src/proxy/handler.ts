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
 * Main proxy handler
 */
export async function handleProxy(
  request: Request,
  env: Env
): Promise<Response> {
  const url = new URL(request.url);
  const workerUrl = env.WORKER_URL || url.origin;
  
  // Extract target URL from path
  const targetUrlEncoded = url.pathname.replace(/^\/proxy\//, '');
  
  if (!targetUrlEncoded) {
    return new Response('Missing target URL', { status: 400 });
  }

  const targetUrl = decodeURIComponent(targetUrlEncoded);
  
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

  // Handle redirects - rewrite Location header
  if (response.status >= 300 && response.status < 400) {
    const location = response.headers.get('location');
    if (location) {
      const absoluteLocation = new URL(location, targetUrlObj).href;
      const proxiedLocation = encodeProxyUrl(absoluteLocation, workerUrl);
      
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
    const rewritten = rewriteHtml(html, targetUrlObj.href, proxyBase);
    
    // Inject client-side script
    const injectedScript = getInjectedScript(proxyBase, targetUrlObj.origin);
    body = rewritten.replace('<head>', `<head>${injectedScript}`);
  } else if (contentType.includes('javascript') || contentType.includes('application/x-javascript')) {
    // Rewrite JavaScript (basic URL rewriting)
    const js = await response.text();
    body = rewriteJsUrls(js, targetUrlObj.href, workerUrl);
  } else if (contentType.includes('text/css')) {
    // Rewrite CSS URLs
    const css = await response.text();
    body = rewriteCssUrls(css, targetUrlObj.href, workerUrl);
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
function rewriteJsUrls(js: string, baseUrl: string, proxyBase: string): string {
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
        const proxied = encodeProxyUrl(url, proxyBase);
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
function rewriteCssUrls(css: string, baseUrl: string, proxyBase: string): string {
  // Rewrite url() references
  return css.replace(/url\s*\(\s*["']?([^"')]+)["']?\s*\)/gi, (match, url) => {
    if (url.startsWith('data:') || url.startsWith('blob:')) {
      return match;
    }
    
    try {
      const absoluteUrl = new URL(url, baseUrl).href;
      if (shouldProxy(absoluteUrl)) {
        const proxied = encodeProxyUrl(absoluteUrl, proxyBase);
        return `url("${proxied}")`;
      }
    } catch {
      // Invalid URL, leave unchanged
    }
    
    return match;
  });
}
