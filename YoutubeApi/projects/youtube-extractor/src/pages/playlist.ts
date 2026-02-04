/**
 * Playlist page handler
 */

import type { Env } from '../index';

export async function handlePlaylistPage(
  request: Request,
  env: Env,
  playlistId: string
): Promise<Response> {
  const url = new URL(request.url);
  const workerUrl = env.WORKER_URL || url.origin;
  
  const html = `
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>Playlist - YouTube Extractor</title>
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
      padding: 20px;
    }
    .container {
      max-width: 1000px;
      margin: 0 auto;
    }
    header {
      display: flex;
      align-items: center;
      margin-bottom: 30px;
    }
    header a {
      color: #3b82f6;
      text-decoration: none;
      margin-right: 20px;
    }
    .loading {
      text-align: center;
      padding: 60px;
      color: rgba(255,255,255,0.7);
    }
    .error {
      background: rgba(239, 68, 68, 0.2);
      border: 1px solid rgba(239, 68, 68, 0.5);
      border-radius: 8px;
      padding: 20px;
      margin: 20px 0;
    }
    .playlist-header {
      background: rgba(255,255,255,0.05);
      border-radius: 12px;
      padding: 25px;
      margin-bottom: 20px;
    }
    .playlist-title {
      font-size: 1.8rem;
      margin-bottom: 10px;
    }
    .playlist-meta {
      color: rgba(255,255,255,0.7);
    }
    .actions {
      margin-top: 15px;
    }
    .btn {
      display: inline-block;
      padding: 10px 20px;
      font-size: 0.9rem;
      border: none;
      border-radius: 6px;
      cursor: pointer;
      background: #3b82f6;
      color: white;
      text-decoration: none;
      margin: 5px 5px 5px 0;
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
    .video-list {
      display: flex;
      flex-direction: column;
      gap: 15px;
    }
    .video-item {
      display: flex;
      gap: 15px;
      background: rgba(255,255,255,0.05);
      border-radius: 10px;
      padding: 15px;
      transition: background 0.2s;
    }
    .video-item:hover {
      background: rgba(255,255,255,0.1);
    }
    .video-index {
      color: rgba(255,255,255,0.5);
      font-size: 1.2rem;
      min-width: 30px;
      display: flex;
      align-items: center;
      justify-content: center;
    }
    .video-thumbnail {
      width: 160px;
      height: 90px;
      border-radius: 6px;
      object-fit: cover;
      background: rgba(0,0,0,0.3);
    }
    .video-details {
      flex: 1;
      min-width: 0;
    }
    .video-title {
      font-size: 1rem;
      margin-bottom: 5px;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .video-title a {
      color: inherit;
      text-decoration: none;
    }
    .video-title a:hover {
      color: #3b82f6;
    }
    .video-duration {
      color: rgba(255,255,255,0.5);
      font-size: 0.85rem;
    }
    .video-actions {
      display: flex;
      align-items: center;
    }
    .btn-small {
      padding: 6px 12px;
      font-size: 0.8rem;
    }
  </style>
</head>
<body>
  <div class="container">
    <header>
      <a href="/">← Back</a>
      <h1>Playlist</h1>
    </header>
    
    <div id="content">
      <div class="loading">Loading playlist information...</div>
    </div>
  </div>
  
  <script>
    const PLAYLIST_ID = '${playlistId}';
    const token = localStorage.getItem('yt_extractor_token');
    
    if (!token) {
      document.getElementById('content').innerHTML = \`
        <div class="error">
          <p>Please <a href="/login" style="color: #3b82f6;">login</a> to access playlist information.</p>
        </div>
      \`;
    } else {
      loadPlaylist();
    }
    
    async function loadPlaylist() {
      try {
        const response = await fetch('/api/playlist?list=' + PLAYLIST_ID, {
          headers: { 'Authorization': 'Bearer ' + token }
        });
        
        if (!response.ok) {
          const error = await response.json();
          throw new Error(error.error?.message || 'Failed to load playlist');
        }
        
        const data = await response.json();
        renderPlaylist(data);
      } catch (error) {
        document.getElementById('content').innerHTML = \`
          <div class="error">
            <p>Error: \${error.message}</p>
          </div>
        \`;
      }
    }
    
    function renderPlaylist(data) {
      const totalDuration = data.videos.reduce((sum, v) => sum + v.duration_seconds, 0);
      
      document.getElementById('content').innerHTML = \`
        <div class="playlist-header">
          <h2 class="playlist-title">\${escapeHtml(data.title)}</h2>
          <p class="playlist-meta">
            \${data.author} • \${data.video_count} videos • \${formatDuration(totalDuration)} total
          </p>
          <div class="actions">
            <button class="btn" onclick="downloadAllSubtitles()">
              📝 Download All Subtitles
            </button>
            <a class="btn btn-secondary" href="https://www.youtube.com/playlist?list=\${PLAYLIST_ID}" target="_blank">
              View on YouTube
            </a>
          </div>
        </div>
        
        <div class="video-list">
          \${data.videos.map(video => \`
            <div class="video-item">
              <div class="video-index">\${video.index}</div>
              <img class="video-thumbnail" src="\${video.thumbnail}" alt="" loading="lazy">
              <div class="video-details">
                <div class="video-title">
                  <a href="/video/\${video.video_id}">\${escapeHtml(video.title)}</a>
                </div>
                <div class="video-duration">\${formatDuration(video.duration_seconds)}</div>
              </div>
              <div class="video-actions">
                <a class="btn btn-small btn-secondary" href="/video/\${video.video_id}">
                  Details
                </a>
              </div>
            </div>
          \`).join('')}
        </div>
      \`;
      
      // Store videos for batch operations
      window.playlistVideos = data.videos;
    }
    
    async function downloadAllSubtitles() {
      const videos = window.playlistVideos;
      if (!videos || videos.length === 0) return;
      
      const format = prompt('Subtitle format (vtt, srt, json3):', 'vtt');
      if (!format) return;
      
      const lang = prompt('Language code (e.g., en, es, auto for first available):', 'en');
      if (!lang) return;
      
      alert('Starting download of ' + videos.length + ' subtitles. Please wait...');
      
      const results = [];
      
      for (const video of videos) {
        try {
          const response = await fetch(\`/api/subtitles?v=\${video.video_id}&lang=\${lang}&format=\${format}\`, {
            headers: { 'Authorization': 'Bearer ' + token }
          });
          
          const data = await response.json();
          
          if (data.subtitles && data.subtitles.content) {
            results.push({
              filename: \`\${video.index.toString().padStart(2, '0')}_\${video.video_id}_\${lang}.\${format}\`,
              content: data.subtitles.content
            });
          }
        } catch (error) {
          console.error('Failed to fetch subtitles for', video.video_id);
        }
      }
      
      if (results.length === 0) {
        alert('No subtitles found for the specified language.');
        return;
      }
      
      // Download as individual files or create a simple "zip" (actually just download them)
      for (const result of results) {
        const blob = new Blob([result.content], { type: 'text/plain' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = result.filename;
        a.click();
        URL.revokeObjectURL(url);
        
        // Small delay to prevent browser blocking
        await new Promise(r => setTimeout(r, 100));
      }
      
      alert('Downloaded ' + results.length + ' subtitle files.');
    }
    
    function formatDuration(seconds) {
      const h = Math.floor(seconds / 3600);
      const m = Math.floor((seconds % 3600) / 60);
      const s = seconds % 60;
      
      if (h > 0) {
        return \`\${h}:\${m.toString().padStart(2, '0')}:\${s.toString().padStart(2, '0')}\`;
      }
      return \`\${m}:\${s.toString().padStart(2, '0')}\`;
    }
    
    function escapeHtml(str) {
      return str.replace(/&/g, '&amp;')
                .replace(/</g, '&lt;')
                .replace(/>/g, '&gt;')
                .replace(/"/g, '&quot;');
    }
  </script>
</body>
</html>
`;
  
  return new Response(html, {
    headers: { 'Content-Type': 'text/html; charset=utf-8' },
  });
}
