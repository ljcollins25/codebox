/**
 * Authentication and login flow handlers
 * 
 * Supports two authentication methods:
 * 1. Automatic OAuth via service worker proxy (like Ultraviolet)
 * 2. Manual cookie paste as fallback
 */

import type { Env } from '../index';
import { generateToken, storeToken, getToken, deleteToken } from '../storage/kv';
import { parseCookies } from '../utils/cookies';
import { throwInvalidToken } from '../utils/errors';
import { encodeProxyUrl } from '../proxy/handler';

/**
 * Required YouTube cookies for authentication
 */
const REQUIRED_COOKIES = ['SID', 'HSID', 'SSID', 'APISID', 'SAPISID'];
const OPTIONAL_COOKIES = ['LOGIN_INFO', '__Secure-1PSID', '__Secure-3PSID', '__Secure-1PAPISID', '__Secure-3PAPISID'];

/**
 * Handle login page - show login options
 */
export async function handleLogin(
  request: Request,
  env: Env
): Promise<Response> {
  const url = new URL(request.url);
  const workerUrl = env.WORKER_URL || url.origin;
  const method = url.searchParams.get('method');
  
  if (request.method === 'POST') {
    return handleLoginSubmit(request, env);
  }
  
  // Show different page based on method
  if (method === 'manual') {
    return new Response(generateManualLoginPageHtml(workerUrl), {
      headers: { 'Content-Type': 'text/html; charset=utf-8' },
    });
  }
  
  // Default: show login options page
  return new Response(generateLoginOptionsPageHtml(workerUrl), {
    headers: { 'Content-Type': 'text/html; charset=utf-8' },
  });
}

/**
 * Handle OAuth start - redirect to the auth frame page
 */
export async function handleOAuthStart(
  request: Request,
  env: Env
): Promise<Response> {
  const url = new URL(request.url);
  const workerUrl = env.WORKER_URL || url.origin;
  
  return new Response(generateOAuthFramePageHtml(workerUrl), {
    headers: { 'Content-Type': 'text/html; charset=utf-8' },
  });
}

/**
 * Handle OAuth callback - receive cookies from service worker
 */
export async function handleOAuthCallback(
  request: Request,
  env: Env
): Promise<Response> {
  const url = new URL(request.url);
  const workerUrl = env.WORKER_URL || url.origin;
  
  if (request.method === 'POST') {
    try {
      const body = await request.json() as { cookies?: string };
      const cookieString = body.cookies;
      
      if (!cookieString) {
        return new Response(JSON.stringify({ error: 'No cookies provided' }), {
          status: 400,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      
      // Parse and validate cookies
      const cookies = parseCookies(cookieString);
      const hasRequired = REQUIRED_COOKIES.some(name => cookies[name]);
      
      if (!hasRequired) {
        return new Response(JSON.stringify({ error: 'Missing required YouTube cookies' }), {
          status: 400,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      
      // Generate token
      const token = generateToken();
      await storeToken(env, token, cookieString);
      
      return new Response(JSON.stringify({ success: true, token }), {
        headers: { 'Content-Type': 'application/json' },
      });
      
    } catch (error) {
      return new Response(JSON.stringify({ error: 'Invalid request' }), {
        status: 400,
        headers: { 'Content-Type': 'application/json' },
      });
    }
  }
  
  return new Response('Method not allowed', { status: 405 });
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
/**
 * Generate login options page HTML
 */
function generateLoginOptionsPageHtml(workerUrl: string): string {
  return `
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>Login - YouTube Extractor</title>
  <style>
    * { box-sizing: border-box; margin: 0; padding: 0; }
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
    h1 { font-size: 2rem; margin-bottom: 10px; text-align: center; }
    .subtitle { text-align: center; color: rgba(255,255,255,0.7); margin-bottom: 30px; }
    .login-option {
      background: rgba(0,0,0,0.2);
      border-radius: 12px;
      padding: 25px;
      margin-bottom: 20px;
      border: 1px solid rgba(255,255,255,0.1);
      transition: all 0.2s;
    }
    .login-option:hover {
      border-color: rgba(59, 130, 246, 0.5);
      background: rgba(0,0,0,0.3);
    }
    .login-option h3 { margin-bottom: 10px; display: flex; align-items: center; gap: 10px; }
    .login-option p { color: rgba(255,255,255,0.7); font-size: 0.9rem; margin-bottom: 15px; }
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
    }
    .btn:hover { background: #2563eb; }
    .btn-secondary { background: rgba(255,255,255,0.2); }
    .btn-secondary:hover { background: rgba(255,255,255,0.3); }
    .badge {
      font-size: 0.7rem;
      padding: 3px 8px;
      background: #22c55e;
      border-radius: 4px;
      text-transform: uppercase;
      font-weight: 600;
    }
    .badge.experimental { background: #f59e0b; }
    .back-link {
      display: block;
      text-align: center;
      margin-top: 20px;
      color: rgba(255,255,255,0.7);
      text-decoration: none;
    }
    .back-link:hover { color: #fff; }
  </style>
</head>
<body>
  <div class="container">
    <h1>🔐 Login to YouTube</h1>
    <p class="subtitle">Choose how you want to authenticate</p>
    
    <div class="login-option">
      <h3>🌐 Sign in with Google <span class="badge experimental">Experimental</span></h3>
      <p>Sign in directly through Google's login page. Uses a secure proxy to capture authentication cookies.</p>
      <a href="/oauth/start" class="btn">Sign in with Google</a>
    </div>
    
    <div class="login-option">
      <h3>📋 Paste Cookies <span class="badge">Reliable</span></h3>
      <p>Manually extract and paste cookies from an existing YouTube session. Works consistently but requires browser developer tools.</p>
      <a href="/login?method=manual" class="btn btn-secondary">Use Cookie Method</a>
    </div>
    
    <a href="/" class="back-link">← Back to Home</a>
  </div>
</body>
</html>
`;
}

/**
 * Generate manual cookie paste page HTML
 */
function generateManualLoginPageHtml(workerUrl: string, error?: string): string {
  return `
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>Manual Login - YouTube Extractor</title>
  <style>
    * { box-sizing: border-box; margin: 0; padding: 0; }
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
    h1 { font-size: 2rem; margin-bottom: 10px; text-align: center; }
    .subtitle { text-align: center; color: rgba(255,255,255,0.7); margin-bottom: 30px; }
    .error {
      background: rgba(239, 68, 68, 0.2);
      border: 1px solid #ef4444;
      color: #fca5a5;
      padding: 15px;
      border-radius: 8px;
      margin-bottom: 20px;
    }
    .form-group { margin-bottom: 20px; }
    label { display: block; margin-bottom: 8px; font-weight: 500; }
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
    textarea:focus { outline: none; border-color: #3b82f6; }
    textarea::placeholder { color: rgba(255,255,255,0.4); }
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
    .btn:hover { background: #2563eb; }
    .instructions {
      background: rgba(59, 130, 246, 0.15);
      border-radius: 8px;
      padding: 20px;
      margin-bottom: 25px;
    }
    .instructions h3 { margin-bottom: 15px; font-size: 1.1rem; }
    .instructions ol { margin-left: 20px; }
    .instructions li { margin-bottom: 10px; line-height: 1.6; }
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
    .back-link:hover { color: #fff; }
  </style>
</head>
<body>
  <div class="container">
    <h1>📋 Manual Cookie Login</h1>
    <p class="subtitle">Paste your YouTube cookies to authenticate</p>
    
    ${error ? `<div class="error">⚠️ ${error}</div>` : ''}
    
    <div class="instructions">
      <h3>How to get your cookies:</h3>
      <ol>
        <li>Open <a href="https://www.youtube.com" target="_blank" style="color: #60a5fa;">youtube.com</a> and sign in with your Google account</li>
        <li>Press <code>F12</code> to open Developer Tools</li>
        <li>Go to the <strong>Application</strong> tab (or <strong>Storage</strong> in Firefox)</li>
        <li>In the left sidebar, expand <strong>Cookies</strong> → <code>https://www.youtube.com</code></li>
        <li>Copy all cookies (use <a href="https://chrome.google.com/webstore/detail/editthiscookie/fngmhnnpilhplaeedifhccceomclgfbg" target="_blank" style="color: #60a5fa;">EditThisCookie</a> extension to export)</li>
        <li>Paste below in format: <code>NAME=VALUE; NAME2=VALUE2</code></li>
      </ol>
    </div>
    
    <form method="POST" action="/login?method=manual">
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
      <strong>🔒 Security Note:</strong> Your cookies are stored securely and used only for YouTube API requests. 
      Never share your cookies with untrusted services.
    </div>
    
    <a href="/login" class="back-link">← Back to Login Options</a>
  </div>
</body>
</html>
`;
}

/**
 * Generate OAuth frame page HTML (with service worker registration)
 */
function generateOAuthFramePageHtml(workerUrl: string): string {
  return `
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>Signing in... - YouTube Extractor</title>
  <style>
    * { box-sizing: border-box; margin: 0; padding: 0; }
    body {
      font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
      background: linear-gradient(135deg, #1a1a2e 0%, #16213e 100%);
      color: #fff;
      min-height: 100vh;
      display: flex;
      flex-direction: column;
    }
    .header {
      padding: 15px 20px;
      background: rgba(0,0,0,0.2);
      display: flex;
      align-items: center;
      justify-content: space-between;
    }
    .header h1 { font-size: 1.2rem; }
    .status {
      display: flex;
      align-items: center;
      gap: 10px;
      font-size: 0.9rem;
    }
    .status-dot {
      width: 10px;
      height: 10px;
      border-radius: 50%;
      background: #f59e0b;
      animation: pulse 1.5s infinite;
    }
    .status-dot.ready { background: #22c55e; animation: none; }
    .status-dot.error { background: #ef4444; animation: none; }
    @keyframes pulse {
      0%, 100% { opacity: 1; }
      50% { opacity: 0.5; }
    }
    .frame-container {
      flex: 1;
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 20px;
    }
    .frame-wrapper {
      width: 100%;
      max-width: 500px;
      height: 600px;
      background: #fff;
      border-radius: 12px;
      overflow: hidden;
      box-shadow: 0 20px 60px rgba(0,0,0,0.3);
    }
    iframe {
      width: 100%;
      height: 100%;
      border: none;
    }
    .loading {
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      height: 100%;
      color: #333;
    }
    .loading h2 { margin-bottom: 20px; }
    .spinner {
      width: 40px;
      height: 40px;
      border: 3px solid #e5e7eb;
      border-top-color: #3b82f6;
      border-radius: 50%;
      animation: spin 1s linear infinite;
    }
    @keyframes spin { to { transform: rotate(360deg); } }
    .error-msg {
      text-align: center;
      padding: 40px;
    }
    .error-msg h2 { color: #ef4444; margin-bottom: 15px; }
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
      margin-top: 15px;
    }
    .btn:hover { background: #2563eb; }
    .instructions {
      max-width: 500px;
      margin: 0 auto 20px;
      padding: 15px;
      background: rgba(59, 130, 246, 0.15);
      border-radius: 8px;
      font-size: 0.9rem;
      text-align: center;
    }
    .hidden { display: none !important; }
  </style>
</head>
<body>
  <div class="header">
    <h1>🔐 YouTube Sign In</h1>
    <div class="status">
      <div class="status-dot" id="statusDot"></div>
      <span id="statusText">Initializing...</span>
    </div>
  </div>
  
  <div class="frame-container">
    <div>
      <div class="instructions" id="instructions">
        Sign in with your Google account below. Once complete, your API token will be generated automatically.
      </div>
      
      <div class="frame-wrapper" id="frameWrapper">
        <div class="loading" id="loadingState">
          <div class="spinner"></div>
          <h2>Preparing secure login...</h2>
          <p>Setting up proxy service worker</p>
        </div>
        
        <div class="error-msg hidden" id="errorState">
          <h2>⚠️ Could not initialize</h2>
          <p id="errorText">Service workers are not supported or blocked.</p>
          <a href="/login?method=manual" class="btn">Use Manual Method</a>
        </div>
        
        <iframe id="authFrame" class="hidden" sandbox="allow-forms allow-scripts allow-same-origin allow-popups"></iframe>
      </div>
    </div>
  </div>
  
  <script>
    const WORKER_URL = ${JSON.stringify(workerUrl)};
    const GOOGLE_AUTH_URL = 'https://accounts.google.com/ServiceLogin?service=youtube&uilel=3&passive=true&continue=https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26app%3Ddesktop%26hl%3Den%26next%3Dhttps%253A%252F%252Fwww.youtube.com%252F';
    
    const statusDot = document.getElementById('statusDot');
    const statusText = document.getElementById('statusText');
    const loadingState = document.getElementById('loadingState');
    const errorState = document.getElementById('errorState');
    const errorText = document.getElementById('errorText');
    const authFrame = document.getElementById('authFrame');
    const instructions = document.getElementById('instructions');
    
    function setStatus(status, text) {
      statusDot.className = 'status-dot ' + status;
      statusText.textContent = text;
    }
    
    function showError(msg) {
      loadingState.classList.add('hidden');
      errorState.classList.remove('hidden');
      errorText.textContent = msg;
      setStatus('error', 'Error');
    }
    
    function showFrame() {
      loadingState.classList.add('hidden');
      errorState.classList.add('hidden');
      authFrame.classList.remove('hidden');
      setStatus('ready', 'Ready - Please sign in');
    }
    
    async function init() {
      // Check for service worker support
      if (!('serviceWorker' in navigator)) {
        showError('Your browser does not support service workers.');
        return;
      }
      
      try {
        setStatus('', 'Registering service worker...');
        
        // Register the service worker
        const registration = await navigator.serviceWorker.register('/static/uv-sw.js', {
          scope: '/auth/'
        });
        
        // Wait for it to be active
        let sw = registration.active || registration.installing || registration.waiting;
        
        if (!registration.active) {
          await new Promise((resolve, reject) => {
            sw.addEventListener('statechange', () => {
              if (sw.state === 'activated') resolve();
              if (sw.state === 'redundant') reject(new Error('SW failed'));
            });
            setTimeout(() => reject(new Error('Timeout')), 10000);
          });
        }
        
        setStatus('', 'Service worker ready');
        
        // Load the Google login page through our proxy
        // The SW will intercept /auth/* requests
        const encodedUrl = '/auth/' + btoa(GOOGLE_AUTH_URL.split('').map((c,i) => 
          String.fromCharCode(c.charCodeAt(0) ^ 2)
        ).join(''));
        
        authFrame.src = encodedUrl;
        showFrame();
        
        // Listen for messages from the frame
        window.addEventListener('message', async (event) => {
          if (event.data.type === 'AUTH_COMPLETE') {
            setStatus('', 'Processing login...');
            instructions.textContent = 'Login detected! Generating your API token...';
            
            // Request cookies from the service worker
            if (navigator.serviceWorker.controller) {
              navigator.serviceWorker.controller.postMessage({ type: 'GET_COOKIES' });
            }
          }
        });
        
        // Listen for cookie response from SW
        navigator.serviceWorker.addEventListener('message', async (event) => {
          if (event.data.type === 'COOKIES') {
            const cookies = event.data.cookies;
            if (cookies && cookies.includes('SID=')) {
              // Submit cookies to get token
              const resp = await fetch('/oauth/callback', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ cookies })
              });
              
              const result = await resp.json();
              if (result.token) {
                // Success! Redirect to token page
                window.location.href = '/token?new=' + result.token;
              } else {
                showError(result.error || 'Failed to create token');
              }
            }
          }
        });
        
        // Also check periodically if we've logged in
        // (in case message passing doesn't work)
        setInterval(async () => {
          if (navigator.serviceWorker.controller) {
            navigator.serviceWorker.controller.postMessage({ type: 'GET_COOKIES' });
          }
        }, 3000);
        
      } catch (error) {
        console.error('Init error:', error);
        showError('Failed to initialize: ' + error.message);
      }
    }
    
    init();
  </script>
</body>
</html>
`;
}