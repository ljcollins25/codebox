/**
 * API endpoint: /api/status
 */

import type { Env } from '../index';
import { extractToken, validateToken } from '../auth/login';
import { fetchYouTubePage } from '../extraction/player';
import { throwMissingAuth } from '../utils/errors';

export async function handleApiStatus(
  request: Request,
  env: Env
): Promise<Response> {
  // Extract and validate token
  const token = extractToken(request);
  if (!token) {
    throwMissingAuth();
  }
  
  const tokenData = await validateToken(env, token);
  
  // Test if cookies are still valid by making a request to YouTube
  let cookiesValid = true;
  
  try {
    const response = await fetchYouTubePage(
      'https://www.youtube.com/feed/subscriptions',
      tokenData.youtube_cookies
    );
    const html = await response.text();
    
    // Check if we're logged in by looking for sign-in prompt
    if (html.includes('Sign in') && html.includes('to see updates')) {
      cookiesValid = false;
    }
  } catch {
    cookiesValid = false;
  }
  
  return new Response(JSON.stringify({
    valid: true,
    created_at: tokenData.created_at,
    last_used: tokenData.last_used,
    cookies_valid: cookiesValid,
    label: tokenData.label || null,
  }), {
    headers: { 'Content-Type': 'application/json' },
  });
}
