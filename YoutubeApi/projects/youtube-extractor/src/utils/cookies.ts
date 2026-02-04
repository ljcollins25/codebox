/**
 * Cookie handling utilities
 */

export interface ParsedCookies {
  [name: string]: string;
}

/**
 * Parse a Cookie header string into an object
 */
export function parseCookies(cookieHeader: string): ParsedCookies {
  const cookies: ParsedCookies = {};
  
  if (!cookieHeader) return cookies;
  
  cookieHeader.split(';').forEach(cookie => {
    const [name, ...valueParts] = cookie.trim().split('=');
    if (name) {
      cookies[name.trim()] = valueParts.join('=').trim();
    }
  });
  
  return cookies;
}

/**
 * Convert cookies object to Cookie header string
 */
export function serializeCookies(cookies: ParsedCookies): string {
  return Object.entries(cookies)
    .map(([name, value]) => `${name}=${value}`)
    .join('; ');
}

/**
 * Parse Set-Cookie header and extract name/value
 */
export function parseSetCookie(header: string): { name: string; value: string } | null {
  const match = header.match(/^([^=]+)=([^;]*)/);
  if (!match) return null;
  return { name: match[1].trim(), value: match[2].trim() };
}

/**
 * Merge new cookies from Set-Cookie headers into existing cookies
 */
export function mergeCookies(existing: string, setCookieHeaders: string[]): string {
  const cookies = parseCookies(existing);
  
  for (const header of setCookieHeaders) {
    const parsed = parseSetCookie(header);
    if (parsed) {
      cookies[parsed.name] = parsed.value;
    }
  }
  
  return serializeCookies(cookies);
}

/**
 * Rewrite Set-Cookie header for proxy domain
 */
export function rewriteSetCookie(header: string, proxyDomain: string): string {
  return header
    // Remove domain restriction to let it apply to proxy domain
    .replace(/;\s*domain=[^;]+/gi, '')
    // Ensure SameSite=None for cross-site requests
    .replace(/;\s*samesite=[^;]+/gi, '')
    + '; SameSite=None; Secure';
}

/**
 * Extract all cookies from response headers
 */
export function extractCookiesFromResponse(response: Response): string[] {
  const cookies: string[] = [];
  
  // getAll is available in Workers runtime
  const setCookies = response.headers.getAll?.('set-cookie') || [];
  cookies.push(...setCookies);
  
  // Fallback for environments without getAll
  const singleCookie = response.headers.get('set-cookie');
  if (singleCookie && cookies.length === 0) {
    cookies.push(singleCookie);
  }
  
  return cookies;
}
