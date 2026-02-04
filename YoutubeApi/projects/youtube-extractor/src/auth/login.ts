/**
 * Authentication and login flow handlers
 */

import type { Env } from '../index';
import { generateToken, storeToken, getToken, deleteToken } from '../storage/kv';
import { encodeProxyUrl } from '../proxy/handler';
import { parseCookies } from '../utils/cookies';
import { throwInvalidToken } from '../utils/errors';

/**
 * Handle login - redirect to proxied Google OAuth
 */
export async function handleLogin(
  request: Request,
  env: Env
): Promise<Response> {
  const url = new URL(request.url);
  const workerUrl = env.WORKER_URL || url.origin;
  
  // YouTube's OAuth URL
  const youtubeUrl = 'https://accounts.google.com/ServiceLogin?service=youtube&uilel=3&passive=true&continue=https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26app%3Ddesktop%26hl%3Den%26next%3Dhttps%253A%252F%252Fwww.youtube.com%252F';
  
  // Redirect to proxied version
  const proxiedUrl = encodeProxyUrl(youtubeUrl, workerUrl);
  
  return Response.redirect(proxiedUrl, 302);
}

/**
 * Handle token page - display or generate token after successful login
 */
export async function handleToken(
  request: Request,
  env: Env
): Promise<Response> {
  const url = new URL(request.url);
  const workerUrl = env.WORKER_URL || url.origin;
  
  // Get cookies from request (set during OAuth flow)
  const cookieHeader = request.headers.get('Cookie') || '';
  const cookies = parseCookies(cookieHeader);
  
  // Check if we have YouTube cookies indicating successful login
  const hasYouTubeCookies = cookies['SAPISID'] || cookies['SID'] || cookies['__Secure-3PAPISID'];
  
  if (!hasYouTubeCookies) {
    // Not logged in yet
    return new Response(generateTokenPageHtml(workerUrl, null, 'Please login first'), {
      headers: { 'Content-Type': 'text/html; charset=utf-8' },
    });
  }
  
  // Generate new token
  const token = generateToken();
  
  // Store token with cookies
  await storeToken(env, token, cookieHeader);
  
  return new Response(generateTokenPageHtml(workerUrl, token, null), {
    headers: { 'Content-Type': 'text/html; charset=utf-8' },
  });
}

/**
 * Handle token revocation
 */
export async function handleTokenRevoke(
  request: Request,
  env: Env
): Promise<Response> {
  try {
    const body = await request.json() as { token?: string };
    const token = body.token;
    
    if (!token) {
      return new Response(JSON.stringify({ error: 'Token required' }), {
        status: 400,
        headers: { 'Content-Type': 'application/json' },
      });
    }
    
    // Verify token exists
    const tokenData = await getToken(env, token);
    if (!tokenData) {
      throwInvalidToken();
    }
    
    // Delete token
    await deleteToken(env, token);
    
    return new Response(JSON.stringify({ success: true }), {
      headers: { 'Content-Type': 'application/json' },
    });
  } catch (error) {
    return new Response(JSON.stringify({ error: 'Invalid request' }), {
      status: 400,
      headers: { 'Content-Type': 'application/json' },
    });
  }
}

/**
 * Extract token from Authorization header
 */
export function extractToken(request: Request): string | null {
  const authHeader = request.headers.get('Authorization');
  if (!authHeader) return null;
  
  const match = authHeader.match(/^Bearer\s+(.+)$/i);
  return match ? match[1] : null;
}

/**
 * Validate token and return token data
 */
export async function validateToken(env: Env, token: string) {
  const tokenData = await getToken(env, token);
  if (!tokenData) {
    throwInvalidToken();
  }
  return tokenData;
}

/**
 * Generate token display page HTML
 */
function generateTokenPageHtml(
  workerUrl: string,
  token: string | null,
  error: string | null
): string {
  return `
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>API Token - YouTube Extractor</title>
  <style>
    * {
      box-sizing: border-box;
      margin: 0;
      padding: 0;
    }
    body {
      font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
      background: linear-gradient(135deg, #1a1a2e 0%, #16213e 100%);
      color: #fff;
      min-height: 100vh;
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 20px;
    }
    .container {
      max-width: 600px;
      width: 100%;
      background: rgba(255,255,255,0.1);
      border-radius: 16px;
      padding: 40px;
      backdrop-filter: blur(10px);
    }
    h1 {
      font-size: 2rem;
      margin-bottom: 20px;
      text-align: center;
    }
    .token-box {
      background: rgba(0,0,0,0.3);
      border-radius: 8px;
      padding: 20px;
      margin: 20px 0;
      word-break: break-all;
      font-family: monospace;
      font-size: 0.9rem;
    }
    .success {
      color: #4ade80;
      text-align: center;
      margin-bottom: 10px;
    }
    .error {
      color: #f87171;
      text-align: center;
      margin-bottom: 10px;
    }
    .btn {
      display: inline-block;
      padding: 12px 24px;
      background: #3b82f6;
      color: white;
      border: none;
      border-radius: 8px;
      font-size: 1rem;
      cursor: pointer;
      text-decoration: none;
      margin: 5px;
    }
    .btn:hover {
      background: #2563eb;
    }
    .btn-secondary {
      background: rgba(255,255,255,0.2);
    }
    .btn-secondary:hover {
      background: rgba(255,255,255,0.3);
    }
    .actions {
      text-align: center;
      margin-top: 20px;
    }
    .info {
      background: rgba(59, 130, 246, 0.2);
      border-radius: 8px;
      padding: 15px;
      margin-top: 20px;
      font-size: 0.9rem;
    }
    .info h3 {
      margin-bottom: 10px;
    }
    .info code {
      background: rgba(0,0,0,0.3);
      padding: 2px 6px;
      border-radius: 4px;
      font-size: 0.85rem;
    }
  </style>
</head>
<body>
  <div class="container">
    <h1>🎬 YouTube Extractor</h1>
    
    ${error ? `<p class="error">${error}</p>` : ''}
    
    ${token ? `
      <p class="success">✓ Login successful! Here's your API token:</p>
      <div class="token-box" id="token">${token}</div>
      
      <div class="actions">
        <button class="btn" onclick="copyToken()">Copy Token</button>
        <a href="/" class="btn btn-secondary">Back to Home</a>
      </div>
      
      <div class="info">
        <h3>How to use:</h3>
        <p>Include this token in your API requests:</p>
        <p style="margin-top: 10px;"><code>Authorization: Bearer ${token.substring(0, 16)}...</code></p>
        <p style="margin-top: 10px;">
          Example: <code>curl -H "Authorization: Bearer YOUR_TOKEN" ${workerUrl}/api/video?v=VIDEO_ID</code>
        </p>
      </div>
      
      <script>
        // Store token in localStorage for web interface
        localStorage.setItem('yt_extractor_token', '${token}');
        
        function copyToken() {
          navigator.clipboard.writeText('${token}');
          alert('Token copied to clipboard!');
        }
      </script>
    ` : `
      <div class="actions">
        <a href="/login" class="btn">Login with Google</a>
        <a href="/" class="btn btn-secondary">Back to Home</a>
      </div>
    `}
  </div>
</body>
</html>
`;
}
