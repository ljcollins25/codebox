/**
 * Authentication and login flow handlers
 * 
 * Uses manual cookie paste approach since Google blocks proxied OAuth.
 * Users extract cookies from a logged-in YouTube session.
 */

import type { Env } from '../index';
import { generateToken, storeToken, getToken, deleteToken } from '../storage/kv';
import { parseCookies } from '../utils/cookies';
import { throwInvalidToken } from '../utils/errors';

/**
 * Required YouTube cookies for authentication
 */
const REQUIRED_COOKIES = ['SID', 'HSID', 'SSID', 'APISID', 'SAPISID'];
const OPTIONAL_COOKIES = ['LOGIN_INFO', '__Secure-1PSID', '__Secure-3PSID', '__Secure-1PAPISID', '__Secure-3PAPISID'];

/**
 * Handle login page - show manual cookie paste interface
 */
export async function handleLogin(
  request: Request,
  env: Env
): Promise<Response> {
  const url = new URL(request.url);
  const workerUrl = env.WORKER_URL || url.origin;
  
  if (request.method === 'POST') {
    return handleLoginSubmit(request, env);
  }
  
  return new Response(generateLoginPageHtml(workerUrl), {
    headers: { 'Content-Type': 'text/html; charset=utf-8' },
  });
}

/**
 * Handle login form submission with pasted cookies
 */
async function handleLoginSubmit(
  request: Request,
  env: Env
): Promise<Response> {
  const url = new URL(request.url);
  const workerUrl = env.WORKER_URL || url.origin;
  
  try {
    const formData = await request.formData();
    const cookieString = formData.get('cookies') as string;
    
    if (!cookieString || !cookieString.trim()) {
      return new Response(generateLoginPageHtml(workerUrl, 'Please paste your YouTube cookies'), {
        status: 400,
        headers: { 'Content-Type': 'text/html; charset=utf-8' },
      });
    }
    
    // Parse and validate cookies
    const cookies = parseCookies(cookieString.trim());
    
    // Check for required cookies
    const missingCookies = REQUIRED_COOKIES.filter(name => !cookies[name]);
    
    if (missingCookies.length > 0) {
      return new Response(
        generateLoginPageHtml(workerUrl, `Missing required cookies: ${missingCookies.join(', ')}`),
        {
          status: 400,
          headers: { 'Content-Type': 'text/html; charset=utf-8' },
        }
      );
    }
    
    // Verify cookies work by making a test request to YouTube
    const verified = await verifyYouTubeCookies(cookieString);
    
    if (!verified) {
      return new Response(
        generateLoginPageHtml(workerUrl, 'Invalid or expired cookies. Please extract fresh cookies from YouTube.'),
        {
          status: 400,
          headers: { 'Content-Type': 'text/html; charset=utf-8' },
        }
      );
    }
    
    // Generate token and store with cookies
    const token = generateToken();
    await storeToken(env, token, cookieString.trim());
    
    // Redirect to token page
    return Response.redirect(`${workerUrl}/token?new=${token}`, 302);
    
  } catch (error) {
    return new Response(
      generateLoginPageHtml(workerUrl, 'Error processing cookies. Please try again.'),
      {
        status: 500,
        headers: { 'Content-Type': 'text/html; charset=utf-8' },
      }
    );
  }
}

/**
 * Verify YouTube cookies by making a test request
 */
async function verifyYouTubeCookies(cookieString: string): Promise<boolean> {
  try {
    const response = await fetch('https://www.youtube.com/account', {
      headers: {
        'Cookie': cookieString,
        'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
      },
      redirect: 'manual',
    });
    
    // If we get redirected to login, cookies are invalid
    const location = response.headers.get('location') || '';
    if (location.includes('accounts.google.com') || location.includes('ServiceLogin')) {
      return false;
    }
    
    // Check response for logged-in indicators
    if (response.status === 200) {
      const text = await response.text();
      // Look for signs of being logged in
      return text.includes('LOGGED_IN') || text.includes('"LOGGED_IN":true') || !text.includes('Sign in');
    }
    
    return response.status !== 302 && response.status !== 401;
  } catch {
    // If request fails, assume cookies might be valid (network issue)
    return true;
  }
}

/**
 * Generate login page HTML
 */
function generateLoginPageHtml(workerUrl: string, error?: string): string {
  return `
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>Login - YouTube Extractor</title>
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
      max-width: 700px;
      width: 100%;
      background: rgba(255,255,255,0.1);
      border-radius: 16px;
      padding: 40px;
      backdrop-filter: blur(10px);
    }
    h1 {
      font-size: 2rem;
      margin-bottom: 10px;
      text-align: center;
    }
    .subtitle {
      text-align: center;
      color: rgba(255,255,255,0.7);
      margin-bottom: 30px;
    }
    .error {
      background: rgba(239, 68, 68, 0.2);
      border: 1px solid #ef4444;
      color: #fca5a5;
      padding: 15px;
      border-radius: 8px;
      margin-bottom: 20px;
    }
    .form-group {
      margin-bottom: 20px;
    }
    label {
      display: block;
      margin-bottom: 8px;
      font-weight: 500;
    }
    textarea {
      width: 100%;
      height: 120px;
      padding: 12px;
      border: 1px solid rgba(255,255,255,0.2);
      border-radius: 8px;
      background: rgba(0,0,0,0.3);
      color: #fff;
      font-family: monospace;
      font-size: 0.85rem;
      resize: vertical;
    }
    textarea:focus {
      outline: none;
      border-color: #3b82f6;
    }
    textarea::placeholder {
      color: rgba(255,255,255,0.4);
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
      width: 100%;
    }
    .btn:hover {
      background: #2563eb;
    }
    .instructions {
      background: rgba(59, 130, 246, 0.15);
      border-radius: 8px;
      padding: 20px;
      margin-bottom: 25px;
    }
    .instructions h3 {
      margin-bottom: 15px;
      font-size: 1.1rem;
    }
    .instructions ol {
      margin-left: 20px;
    }
    .instructions li {
      margin-bottom: 10px;
      line-height: 1.6;
    }
    .instructions code {
      background: rgba(0,0,0,0.3);
      padding: 2px 6px;
      border-radius: 4px;
      font-size: 0.9rem;
    }
    .note {
      background: rgba(245, 158, 11, 0.15);
      border-left: 3px solid #f59e0b;
      padding: 15px;
      margin-top: 20px;
      font-size: 0.9rem;
    }
    .back-link {
      display: block;
      text-align: center;
      margin-top: 20px;
      color: rgba(255,255,255,0.7);
      text-decoration: none;
    }
    .back-link:hover {
      color: #fff;
    }
  </style>
</head>
<body>
  <div class="container">
    <h1>🔐 Login to YouTube</h1>
    <p class="subtitle">Authenticate to access premium features</p>
    
    ${error ? `<div class="error">⚠️ ${error}</div>` : ''}
    
    <div class="instructions">
      <h3>How to get your cookies:</h3>
      <ol>
        <li>Open <a href="https://www.youtube.com" target="_blank" style="color: #60a5fa;">youtube.com</a> and sign in with your Google account</li>
        <li>Press <code>F12</code> to open Developer Tools</li>
        <li>Go to the <strong>Application</strong> tab (or <strong>Storage</strong> in Firefox)</li>
        <li>In the left sidebar, expand <strong>Cookies</strong> and click on <code>https://www.youtube.com</code></li>
        <li>Find and copy all cookies (use a <a href="https://chrome.google.com/webstore/detail/editthiscookie/fngmhnnpilhplaeedifhccceomclgfbg" target="_blank" style="color: #60a5fa;">cookie extension</a> to export all)</li>
        <li>Paste the cookies below in the format: <code>NAME=VALUE; NAME2=VALUE2</code></li>
      </ol>
    </div>
    
    <form method="POST" action="/login">
      <div class="form-group">
        <label for="cookies">Your YouTube Cookies:</label>
        <textarea 
          name="cookies" 
          id="cookies" 
          placeholder="SID=xxx; HSID=xxx; SSID=xxx; APISID=xxx; SAPISID=xxx; ..."
          required
        ></textarea>
      </div>
      
      <button type="submit" class="btn">Generate API Token</button>
    </form>
    
    <div class="note">
      <strong>🔒 Security Note:</strong> Your cookies are stored securely and used only to authenticate YouTube API requests. 
      Tokens can be revoked at any time. Never share your cookies with untrusted services.
    </div>
    
    <a href="/" class="back-link">← Back to Home</a>
  </div>
</body>
</html>
`;
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
  
  // Check for newly created token from login redirect
  const newToken = url.searchParams.get('new');
  
  if (newToken) {
    // Verify this token exists
    const tokenData = await getToken(env, newToken);
    if (tokenData) {
      return new Response(generateTokenPageHtml(workerUrl, newToken, null), {
        headers: { 'Content-Type': 'text/html; charset=utf-8' },
      });
    }
  }
  
  // No valid token - redirect to login
  return new Response(generateTokenPageHtml(workerUrl, null, 'Please login first'), {
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
