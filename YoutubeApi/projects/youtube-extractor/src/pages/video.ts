/**
 * Video detail page handler
 */

import type { Env } from '../index';

export async function handleVideoPage(
  request: Request,
  env: Env,
  videoId: string
): Promise<Response> {
  const url = new URL(request.url);
  const workerUrl = env.WORKER_URL || url.origin;
  
  const html = `
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>Video - YouTube Extractor</title>
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
    .video-info {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 30px;
    }
    @media (max-width: 768px) {
      .video-info {
        grid-template-columns: 1fr;
      }
    }
    .thumbnail {
      width: 100%;
      border-radius: 12px;
    }
    .title {
      font-size: 1.5rem;
      margin-bottom: 10px;
    }
    .meta {
      color: rgba(255,255,255,0.7);
      margin-bottom: 20px;
    }
    .section {
      background: rgba(255,255,255,0.05);
      border-radius: 12px;
      padding: 20px;
      margin-top: 20px;
    }
    .section h3 {
      margin-bottom: 15px;
      border-bottom: 1px solid rgba(255,255,255,0.1);
      padding-bottom: 10px;
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
    .btn-small {
      padding: 6px 12px;
      font-size: 0.8rem;
    }
    table {
      width: 100%;
      border-collapse: collapse;
    }
    th, td {
      text-align: left;
      padding: 10px;
      border-bottom: 1px solid rgba(255,255,255,0.1);
    }
    th {
      color: rgba(255,255,255,0.7);
      font-weight: 500;
    }
    .subtitle-list {
      display: flex;
      flex-wrap: wrap;
      gap: 10px;
    }
    select {
      padding: 8px 12px;
      border-radius: 6px;
      border: none;
      background: rgba(255,255,255,0.9);
      color: #333;
    }
    .player-container {
      margin-top: 20px;
    }
    video {
      width: 100%;
      border-radius: 8px;
      background: #000;
    }
    .settings {
      margin-top: 15px;
      padding: 10px;
      background: rgba(0,0,0,0.2);
      border-radius: 8px;
      font-size: 0.9rem;
    }
    .settings label {
      margin-right: 15px;
    }
  </style>
</head>
<body>
  <div class="container">
    <header>
      <a href="/">← Back</a>
      <h1>Video Details</h1>
    </header>
    
    <div id="content">
      <div class="loading">Loading video information...</div>
    </div>
  </div>
  
  <script>
    const VIDEO_ID = '${videoId}';
    const token = localStorage.getItem('yt_extractor_token');
    
    if (!token) {
      document.getElementById('content').innerHTML = \`
        <div class="error">
          <p>Please <a href="/login" style="color: #3b82f6;">login</a> to access video information.</p>
        </div>
      \`;
    } else {
      loadVideoInfo();
    }
    
    async function loadVideoInfo() {
      try {
        const response = await fetch('/api/video?v=' + VIDEO_ID, {
          headers: { 'Authorization': 'Bearer ' + token }
        });
        
        if (!response.ok) {
          const error = await response.json();
          throw new Error(error.error?.message || 'Failed to load video');
        }
        
        const data = await response.json();
        renderVideo(data);
      } catch (error) {
        document.getElementById('content').innerHTML = \`
          <div class="error">
            <p>Error: \${error.message}</p>
          </div>
        \`;
      }
    }
    
    function renderVideo(data) {
      const videoFormats = data.formats.filter(f => f.has_video && f.has_audio);
      const adaptiveVideo = data.formats.filter(f => f.has_video && !f.has_audio);
      const adaptiveAudio = data.formats.filter(f => f.has_audio && !f.has_video);
      
      document.getElementById('content').innerHTML = \`
        <div class="video-info">
          <div>
            <img class="thumbnail" src="\${data.thumbnail}" alt="Thumbnail">
            
            <div class="player-container">
              <video id="player" controls poster="\${data.thumbnail}">
                <source src="" type="video/mp4">
              </video>
              <div class="settings">
                <label>
                  <select id="quality-select" onchange="updatePlayer()">
                    <option value="">Select quality to play</option>
                    \${videoFormats.map(f => \`
                      <option value="\${f.url}">\${f.quality} (\${f.mime_type.split(';')[0]})</option>
                    \`).join('')}
                  </select>
                </label>
              </div>
            </div>
          </div>
          
          <div>
            <h2 class="title">\${escapeHtml(data.title)}</h2>
            <p class="meta">
              \${data.author} • \${formatDuration(data.duration_seconds)} • \${formatViews(data.view_count)} views
            </p>
            
            <div class="section">
              <h3>📝 Subtitles</h3>
              \${data.subtitles.length > 0 ? \`
                <div class="subtitle-list">
                  \${data.subtitles.map(s => \`
                    <button class="btn btn-small btn-secondary" onclick="downloadSubtitle('\${s.code}')">
                      \${s.name} \${s.auto ? '(auto)' : ''}
                    </button>
                  \`).join('')}
                </div>
                <div style="margin-top: 15px;">
                  <label>Format: 
                    <select id="subtitle-format">
                      <option value="vtt">WebVTT</option>
                      <option value="srt">SRT</option>
                      <option value="json3">JSON</option>
                    </select>
                  </label>
                </div>
              \` : '<p style="color: rgba(255,255,255,0.5);">No subtitles available</p>'}
            </div>
            
            <div class="section">
              <h3>⬇️ Download</h3>
              <p style="margin-bottom: 10px; color: rgba(255,255,255,0.7);">
                Recommended: \${data.recommended.needs_muxing ? 
                  'Video + Audio (requires muxing)' : 
                  'Combined format available'}
              </p>
              
              \${videoFormats.length > 0 ? \`
                <div>
                  <strong>Combined (Video + Audio):</strong>
                  \${videoFormats.slice(0, 4).map(f => \`
                    <a class="btn btn-small" href="\${f.url}" target="_blank">
                      \${f.quality}
                    </a>
                  \`).join('')}
                </div>
              \` : ''}
              
              \${adaptiveVideo.length > 0 ? \`
                <div style="margin-top: 10px;">
                  <strong>Video Only:</strong>
                  \${adaptiveVideo.slice(0, 4).map(f => \`
                    <a class="btn btn-small btn-secondary" href="\${f.url}" target="_blank">
                      \${f.quality}
                    </a>
                  \`).join('')}
                </div>
              \` : ''}
              
              \${adaptiveAudio.length > 0 ? \`
                <div style="margin-top: 10px;">
                  <strong>Audio Only:</strong>
                  \${adaptiveAudio.slice(0, 3).map(f => \`
                    <a class="btn btn-small btn-secondary" href="\${f.url}" target="_blank">
                      \${f.audio_quality || 'Audio'}
                    </a>
                  \`).join('')}
                </div>
              \` : ''}
            </div>
          </div>
        </div>
        
        <div class="section">
          <h3>📊 All Formats</h3>
          <table>
            <thead>
              <tr>
                <th>Quality</th>
                <th>Type</th>
                <th>Resolution</th>
                <th>Bitrate</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              \${data.formats.map(f => \`
                <tr>
                  <td>\${f.quality}</td>
                  <td>\${f.mime_type.split(';')[0]}</td>
                  <td>\${f.width ? f.width + 'x' + f.height : '-'}</td>
                  <td>\${formatBitrate(f.bitrate)}</td>
                  <td><a class="btn btn-small" href="\${f.url}" target="_blank">Download</a></td>
                </tr>
              \`).join('')}
            </tbody>
          </table>
        </div>
      \`;
    }
    
    function updatePlayer() {
      const select = document.getElementById('quality-select');
      const player = document.getElementById('player');
      const source = player.querySelector('source');
      
      if (select.value) {
        source.src = select.value;
        player.load();
      }
    }
    
    async function downloadSubtitle(lang) {
      const format = document.getElementById('subtitle-format').value;
      
      try {
        const response = await fetch(\`/api/subtitles?v=\${VIDEO_ID}&lang=\${lang}&format=\${format}\`, {
          headers: { 'Authorization': 'Bearer ' + token }
        });
        
        const data = await response.json();
        
        if (data.subtitles && data.subtitles.content) {
          // Download as file
          const blob = new Blob([data.subtitles.content], { type: 'text/plain' });
          const url = URL.createObjectURL(blob);
          const a = document.createElement('a');
          a.href = url;
          a.download = \`\${VIDEO_ID}_\${lang}.\${format}\`;
          a.click();
          URL.revokeObjectURL(url);
        } else {
          alert('Failed to download subtitles');
        }
      } catch (error) {
        alert('Error: ' + error.message);
      }
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
    
    function formatViews(views) {
      if (views >= 1000000) return (views / 1000000).toFixed(1) + 'M';
      if (views >= 1000) return (views / 1000).toFixed(1) + 'K';
      return views.toString();
    }
    
    function formatBitrate(bps) {
      if (bps >= 1000000) return (bps / 1000000).toFixed(1) + ' Mbps';
      if (bps >= 1000) return (bps / 1000).toFixed(0) + ' kbps';
      return bps + ' bps';
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
