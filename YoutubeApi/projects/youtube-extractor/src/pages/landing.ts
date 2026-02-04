/**
 * Landing page handler
 */

import type { Env } from '../index';

export async function handleLandingPage(
  request: Request,
  env: Env
): Promise<Response> {
  const url = new URL(request.url);
  const workerUrl = env.WORKER_URL || url.origin;
  
  const html = `
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>YouTube Extractor</title>
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
      padding: 40px 20px;
    }
    .container {
      max-width: 800px;
      margin: 0 auto;
    }
    header {
      text-align: center;
      margin-bottom: 40px;
    }
    h1 {
      font-size: 3rem;
      margin-bottom: 10px;
    }
    .subtitle {
      color: rgba(255,255,255,0.7);
      font-size: 1.2rem;
    }
    .search-box {
      background: rgba(255,255,255,0.1);
      border-radius: 16px;
      padding: 30px;
      margin-bottom: 30px;
    }
    .input-group {
      display: flex;
      gap: 10px;
    }
    input[type="text"] {
      flex: 1;
      padding: 15px 20px;
      font-size: 1rem;
      border: none;
      border-radius: 8px;
      background: rgba(255,255,255,0.9);
      color: #333;
    }
    input[type="text"]:focus {
      outline: 2px solid #3b82f6;
    }
    .btn {
      padding: 15px 30px;
      font-size: 1rem;
      border: none;
      border-radius: 8px;
      cursor: pointer;
      background: #3b82f6;
      color: white;
      font-weight: 600;
      transition: background 0.2s;
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
    .auth-status {
      text-align: center;
      margin-bottom: 30px;
      padding: 15px;
      background: rgba(255,255,255,0.05);
      border-radius: 8px;
    }
    .auth-status.logged-in {
      background: rgba(74, 222, 128, 0.1);
      border: 1px solid rgba(74, 222, 128, 0.3);
    }
    .features {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
      gap: 20px;
      margin-top: 40px;
    }
    .feature {
      background: rgba(255,255,255,0.05);
      border-radius: 12px;
      padding: 25px;
      text-align: center;
    }
    .feature-icon {
      font-size: 2rem;
      margin-bottom: 10px;
    }
    .feature h3 {
      margin-bottom: 10px;
    }
    .feature p {
      color: rgba(255,255,255,0.7);
      font-size: 0.9rem;
    }
    .api-docs {
      margin-top: 40px;
      background: rgba(255,255,255,0.05);
      border-radius: 12px;
      padding: 30px;
    }
    .api-docs h2 {
      margin-bottom: 20px;
    }
    .endpoint {
      background: rgba(0,0,0,0.2);
      border-radius: 8px;
      padding: 15px;
      margin-bottom: 15px;
    }
    .endpoint code {
      color: #4ade80;
    }
    .endpoint p {
      color: rgba(255,255,255,0.7);
      font-size: 0.9rem;
      margin-top: 5px;
    }
    footer {
      text-align: center;
      margin-top: 60px;
      color: rgba(255,255,255,0.5);
    }
  </style>
</head>
<body>
  <div class="container">
    <header>
      <h1>🎬 YouTube Extractor</h1>
      <p class="subtitle">Extract subtitles, videos, and metadata from YouTube</p>
    </header>
    
    <div id="auth-status" class="auth-status">
      <span id="auth-text">Checking login status...</span>
      <a href="/login" id="login-btn" class="btn btn-secondary" style="display: none; margin-left: 10px;">Login</a>
      <a href="/token" id="token-btn" class="btn btn-secondary" style="display: none; margin-left: 10px;">View Token</a>
    </div>
    
    <div class="search-box">
      <form id="search-form">
        <div class="input-group">
          <input 
            type="text" 
            id="url-input" 
            placeholder="Paste YouTube URL (video or playlist)..."
            autocomplete="off"
          >
          <button type="submit" class="btn">Go</button>
        </div>
      </form>
    </div>
    
    <div class="features">
      <div class="feature">
        <div class="feature-icon">📝</div>
        <h3>Subtitles</h3>
        <p>Download subtitles in VTT, SRT, or JSON formats</p>
      </div>
      <div class="feature">
        <div class="feature-icon">🎥</div>
        <h3>Video Info</h3>
        <p>Get video metadata and download URLs</p>
      </div>
      <div class="feature">
        <div class="feature-icon">📋</div>
        <h3>Playlists</h3>
        <p>Extract all videos from a playlist</p>
      </div>
      <div class="feature">
        <div class="feature-icon">💬</div>
        <h3>Comments</h3>
        <p>Retrieve video comments and replies</p>
      </div>
    </div>
    
    <div class="api-docs">
      <h2>API Endpoints</h2>
      
      <div class="endpoint">
        <code>GET /api/video?v={VIDEO_ID}</code>
        <p>Get video metadata and download URLs</p>
      </div>
      
      <div class="endpoint">
        <code>GET /api/subtitles?v={VIDEO_ID}&lang={LANG}&format={FORMAT}</code>
        <p>Get subtitles (formats: vtt, srt, json3)</p>
      </div>
      
      <div class="endpoint">
        <code>GET /api/playlist?list={PLAYLIST_ID}</code>
        <p>Get all videos in a playlist</p>
      </div>
      
      <div class="endpoint">
        <code>GET /api/comments?v={VIDEO_ID}</code>
        <p>Get video comments</p>
      </div>
      
      <div class="endpoint">
        <code>GET /api/thumbnail?v={VIDEO_ID}&quality={QUALITY}</code>
        <p>Get thumbnail URL (quality: default, medium, high, max)</p>
      </div>
      
      <p style="margin-top: 20px; color: rgba(255,255,255,0.7);">
        All API endpoints require: <code style="color: #4ade80;">Authorization: Bearer {TOKEN}</code>
      </p>
    </div>
    
    <footer>
      <p>YouTube Extractor Service</p>
    </footer>
  </div>
  
  <script>
    const token = localStorage.getItem('yt_extractor_token');
    const authStatus = document.getElementById('auth-status');
    const authText = document.getElementById('auth-text');
    const loginBtn = document.getElementById('login-btn');
    const tokenBtn = document.getElementById('token-btn');
    
    if (token) {
      // Verify token is valid
      fetch('/api/status', {
        headers: { 'Authorization': 'Bearer ' + token }
      })
      .then(r => r.json())
      .then(data => {
        if (data.valid) {
          authStatus.classList.add('logged-in');
          authText.textContent = '✓ Logged in' + (data.cookies_valid ? '' : ' (cookies expired)');
          tokenBtn.style.display = 'inline-block';
          if (!data.cookies_valid) {
            loginBtn.style.display = 'inline-block';
            loginBtn.textContent = 'Re-login';
          }
        } else {
          showNotLoggedIn();
        }
      })
      .catch(() => showNotLoggedIn());
    } else {
      showNotLoggedIn();
    }
    
    function showNotLoggedIn() {
      authText.textContent = 'Not logged in';
      loginBtn.style.display = 'inline-block';
    }
    
    // URL parsing and redirect
    document.getElementById('search-form').addEventListener('submit', function(e) {
      e.preventDefault();
      const input = document.getElementById('url-input').value.trim();
      
      // Parse YouTube URL
      let videoId = null;
      let playlistId = null;
      
      // youtu.be format
      const shortMatch = input.match(/youtu\\.be\\/([a-zA-Z0-9_-]+)/);
      if (shortMatch) videoId = shortMatch[1];
      
      // youtube.com/watch format
      const watchMatch = input.match(/[?&]v=([a-zA-Z0-9_-]+)/);
      if (watchMatch) videoId = watchMatch[1];
      
      // Playlist ID
      const listMatch = input.match(/[?&]list=([a-zA-Z0-9_-]+)/);
      if (listMatch) playlistId = listMatch[1];
      
      // Direct video ID
      if (!videoId && !playlistId && /^[a-zA-Z0-9_-]{11}$/.test(input)) {
        videoId = input;
      }
      
      // Redirect
      if (playlistId) {
        window.location.href = '/playlist/' + playlistId;
      } else if (videoId) {
        window.location.href = '/video/' + videoId;
      } else {
        alert('Invalid YouTube URL or ID');
      }
    });
  </script>
</body>
</html>
`;
  
  return new Response(html, {
    headers: { 'Content-Type': 'text/html; charset=utf-8' },
  });
}
