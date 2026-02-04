/**
 * HTML rewriting for proxy
 */

import { encodeProxyUrl } from './handler';

/**
 * Attributes that contain URLs by tag
 */
const URL_ATTRIBUTES: Record<string, string[]> = {
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
  'input': ['formaction'],
  'area': ['href'],
  'track': ['src'],
};

/**
 * Rewrite HTML document for proxying
 */
export function rewriteHtml(html: string, baseUrl: string, proxyBase: string): string {
  let result = html;

  // Rewrite URL attributes
  for (const [tag, attrs] of Object.entries(URL_ATTRIBUTES)) {
    for (const attr of attrs) {
      result = rewriteAttribute(result, tag, attr, baseUrl, proxyBase);
    }
  }

  // Rewrite srcset attributes (special format)
  result = rewriteSrcset(result, baseUrl, proxyBase);

  // Rewrite meta refresh redirects
  result = rewriteMetaRefresh(result, baseUrl, proxyBase);

  // Rewrite inline styles with url()
  result = rewriteInlineStyles(result, baseUrl, proxyBase);

  // Rewrite data-* attributes that might contain URLs
  result = rewriteDataAttributes(result, baseUrl, proxyBase);

  return result;
}

/**
 * Rewrite a specific attribute on a specific tag
 */
function rewriteAttribute(
  html: string,
  tag: string,
  attr: string,
  baseUrl: string,
  proxyBase: string
): string {
  // Match tag with the specified attribute
  const pattern = new RegExp(
    `(<${tag}[^>]*\\s+${attr}=["'])([^"']+)(["'])`,
    'gi'
  );

  return html.replace(pattern, (match, pre, url, post) => {
    const rewritten = rewriteUrl(url, baseUrl, proxyBase);
    return `${pre}${rewritten}${post}`;
  });
}

/**
 * Rewrite srcset attributes (comma-separated URL + descriptor)
 */
function rewriteSrcset(html: string, baseUrl: string, proxyBase: string): string {
  const pattern = /srcset=["']([^"']+)["']/gi;

  return html.replace(pattern, (match, srcset) => {
    const parts = srcset.split(',').map((part: string) => {
      const trimmed = part.trim();
      const [url, ...descriptors] = trimmed.split(/\s+/);
      const rewritten = rewriteUrl(url, baseUrl, proxyBase);
      return [rewritten, ...descriptors].join(' ');
    });
    return `srcset="${parts.join(', ')}"`;
  });
}

/**
 * Rewrite meta refresh redirects
 */
function rewriteMetaRefresh(html: string, baseUrl: string, proxyBase: string): string {
  const pattern = /(<meta[^>]*http-equiv=["']refresh["'][^>]*content=["'])(\d+;\s*url=)([^"']+)(["'])/gi;

  return html.replace(pattern, (match, pre, delay, url, post) => {
    const rewritten = rewriteUrl(url, baseUrl, proxyBase);
    return `${pre}${delay}${rewritten}${post}`;
  });
}

/**
 * Rewrite url() in inline styles
 */
function rewriteInlineStyles(html: string, baseUrl: string, proxyBase: string): string {
  const pattern = /(style=["'][^"']*)url\s*\(\s*["']?([^"')]+)["']?\s*\)/gi;

  return html.replace(pattern, (match, pre, url) => {
    const rewritten = rewriteUrl(url, baseUrl, proxyBase);
    return `${pre}url("${rewritten}")`;
  });
}

/**
 * Rewrite data-* attributes that look like URLs
 */
function rewriteDataAttributes(html: string, baseUrl: string, proxyBase: string): string {
  // Match data- attributes that contain http(s) URLs
  const pattern = /(data-[a-z-]+=["'])(https?:\/\/[^"']+)(["'])/gi;

  return html.replace(pattern, (match, pre, url, post) => {
    const rewritten = rewriteUrl(url, baseUrl, proxyBase);
    return `${pre}${rewritten}${post}`;
  });
}

/**
 * Rewrite a single URL
 */
function rewriteUrl(url: string, baseUrl: string, proxyBase: string): string {
  // Skip special URLs
  if (!url || 
      url.startsWith('data:') || 
      url.startsWith('blob:') || 
      url.startsWith('javascript:') ||
      url.startsWith('#') ||
      url.startsWith('mailto:') ||
      url.startsWith('tel:')) {
    return url;
  }

  try {
    // Make URL absolute
    const absoluteUrl = new URL(url, baseUrl).href;
    
    // Encode for proxy
    return encodeProxyUrl(absoluteUrl, proxyBase);
  } catch {
    // Invalid URL, return unchanged
    return url;
  }
}
