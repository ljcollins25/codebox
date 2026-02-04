/**
 * Custom YT-Player Web Component
 * 
 * Usage: <yt-player video-id="dQw4w9WgXcQ"></yt-player>
 */

class YTPlayer extends HTMLElement {
  static get observedAttributes() {
    return ['video-id'];
  }

  constructor() {
    super();
    this.attachShadow({ mode: 'open' });
    this._videoData = null;
    this._currentFormat = null;
  }

  connectedCallback() {
    this.render();
    this.loadVideo();
  }

  attributeChangedCallback(name, oldValue, newValue) {
    if (name === 'video-id' && oldValue !== newValue) {
      this.loadVideo();
    }
  }

  get videoId() {
    return this.getAttribute('video-id');
  }

  set videoId(value) {
    this.setAttribute('video-id', value);
  }

  render() {
    this.shadowRoot.innerHTML = `
      <style>
        :host {
          display: block;
          background: #000;
          border-radius: 8px;
          overflow: hidden;
        }
        .container {
          position: relative;
          width: 100%;
          padding-bottom: 56.25%; /* 16:9 */
        }
        .content {
          position: absolute;
          top: 0;
          left: 0;
          width: 100%;
          height: 100%;
          display: flex;
          align-items: center;
          justify-content: center;
          flex-direction: column;
          color: white;
          font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
        }
        video {
          position: absolute;
          top: 0;
          left: 0;
          width: 100%;
          height: 100%;
        }
        .login-prompt {
          text-align: center;
          padding: 20px;
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
          margin-top: 10px;
        }
        .btn:hover {
          background: #2563eb;
        }
        .error {
          color: #f87171;
          text-align: center;
          padding: 20px;
        }
        .loading {
          color: rgba(255,255,255,0.7);
        }
        .quality-selector {
          position: absolute;
          bottom: 60px;
          right: 10px;
          background: rgba(0,0,0,0.8);
          border-radius: 4px;
          padding: 5px;
        }
        .quality-selector select {
          background: transparent;
          color: white;
          border: none;
          font-size: 14px;
          cursor: pointer;
        }
        .quality-selector select option {
          background: #333;
        }
      </style>
      <div class="container">
        <div class="content" id="content">
          <div class="loading">Loading...</div>
        </div>
      </div>
    `;
  }

  async loadVideo() {
    const content = this.shadowRoot.getElementById('content');
    const token = localStorage.getItem('yt_extractor_token');

    if (!token) {
      this.renderLoginPrompt(content);
      return;
    }

    if (!this.videoId) {
      content.innerHTML = '<div class="error">No video ID specified</div>';
      return;
    }

    try {
      content.innerHTML = '<div class="loading">Loading video...</div>';
      
      const response = await fetch(`/api/video?v=${this.videoId}`, {
        headers: { 'Authorization': `Bearer ${token}` }
      });

      const data = await response.json();

      if (data.error) {
        if (data.error.code === 'cookies_expired') {
          this.renderLoginPrompt(content, 'Session expired');
          return;
        }
        throw new Error(data.error.message);
      }

      this._videoData = data;
      this.renderPlayer(content, data);
    } catch (error) {
      content.innerHTML = `<div class="error">Error: ${error.message}</div>`;
    }
  }

  renderLoginPrompt(content, message = 'Login required') {
    content.innerHTML = `
      <div class="login-prompt">
        <p>${message}</p>
        <a href="/login" class="btn">Login to Watch</a>
      </div>
    `;
  }

  renderPlayer(content, data) {
    // Find best combined format (has both video and audio)
    const combinedFormats = data.formats.filter(f => f.has_video && f.has_audio);
    const defaultFormat = combinedFormats.find(f => f.quality === '720p') 
                         || combinedFormats[0];

    if (!defaultFormat) {
      content.innerHTML = '<div class="error">No playable format found</div>';
      return;
    }

    this._currentFormat = defaultFormat;

    content.innerHTML = `
      <video id="video" controls poster="${data.thumbnail}">
        <source src="${defaultFormat.url}" type="${defaultFormat.mime_type.split(';')[0]}">
      </video>
      <div class="quality-selector">
        <select id="quality">
          ${combinedFormats.map(f => `
            <option value="${f.url}" ${f === defaultFormat ? 'selected' : ''}>
              ${f.quality}
            </option>
          `).join('')}
        </select>
      </div>
    `;

    // Quality selector event
    const qualitySelect = this.shadowRoot.getElementById('quality');
    const video = this.shadowRoot.getElementById('video');
    
    qualitySelect.addEventListener('change', () => {
      const currentTime = video.currentTime;
      const wasPlaying = !video.paused;
      
      video.src = qualitySelect.value;
      video.currentTime = currentTime;
      if (wasPlaying) video.play();
    });
  }
}

// Register custom element
customElements.define('yt-player', YTPlayer);
