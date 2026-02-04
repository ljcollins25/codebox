/**
 * YouTube Extractor Client-Side JavaScript
 * 
 * Handles:
 * - URL parsing
 * - API communication
 * - Media muxing (WebCodecs/ffmpeg.wasm)
 * - File downloads
 */

// Storage keys
const STORAGE_KEYS = {
  TOKEN: 'yt_extractor_token',
  MUXER: 'yt_extractor_muxer',
};

// Muxer preference
const MuxerPreference = {
  AUTO: 'auto',
  WEBCODECS: 'webcodecs',
  FFMPEG: 'ffmpeg',
};

/**
 * Get stored token
 */
function getToken() {
  return localStorage.getItem(STORAGE_KEYS.TOKEN);
}

/**
 * Store token
 */
function setToken(token) {
  localStorage.setItem(STORAGE_KEYS.TOKEN, token);
}

/**
 * Clear token
 */
function clearToken() {
  localStorage.removeItem(STORAGE_KEYS.TOKEN);
}

/**
 * Get muxer preference
 */
function getMuxerPreference() {
  return localStorage.getItem(STORAGE_KEYS.MUXER) || MuxerPreference.AUTO;
}

/**
 * Set muxer preference
 */
function setMuxerPreference(pref) {
  localStorage.setItem(STORAGE_KEYS.MUXER, pref);
}

/**
 * Check WebCodecs support
 */
function hasWebCodecs() {
  return 'VideoDecoder' in window && 'AudioDecoder' in window;
}

/**
 * Determine which muxer to use
 */
function getMuxer() {
  const pref = getMuxerPreference();
  
  if (pref === MuxerPreference.FFMPEG) {
    return MuxerPreference.FFMPEG;
  }
  
  if (pref === MuxerPreference.WEBCODECS && hasWebCodecs()) {
    return MuxerPreference.WEBCODECS;
  }
  
  // Auto: prefer WebCodecs
  if (hasWebCodecs()) {
    return MuxerPreference.WEBCODECS;
  }
  
  return MuxerPreference.FFMPEG;
}

/**
 * Parse YouTube URL
 */
function parseYouTubeUrl(input) {
  const result = {
    videoId: null,
    playlistId: null,
  };
  
  if (!input) return result;
  
  input = input.trim();
  
  // Direct video ID (11 characters)
  if (/^[a-zA-Z0-9_-]{11}$/.test(input)) {
    result.videoId = input;
    return result;
  }
  
  // Direct playlist ID
  if (/^PL[a-zA-Z0-9_-]+$/.test(input)) {
    result.playlistId = input;
    return result;
  }
  
  try {
    const url = new URL(input);
    
    // youtu.be format
    if (url.hostname === 'youtu.be') {
      result.videoId = url.pathname.slice(1);
    }
    
    // youtube.com formats
    if (url.hostname.includes('youtube.com')) {
      // Video ID from ?v=
      const v = url.searchParams.get('v');
      if (v) result.videoId = v;
      
      // Playlist ID from ?list=
      const list = url.searchParams.get('list');
      if (list) result.playlistId = list;
      
      // /shorts/ format
      const shortsMatch = url.pathname.match(/\/shorts\/([a-zA-Z0-9_-]+)/);
      if (shortsMatch) result.videoId = shortsMatch[1];
      
      // /embed/ format
      const embedMatch = url.pathname.match(/\/embed\/([a-zA-Z0-9_-]+)/);
      if (embedMatch) result.videoId = embedMatch[1];
    }
  } catch {
    // Not a valid URL
  }
  
  return result;
}

/**
 * API request helper
 */
async function apiRequest(endpoint, params = {}) {
  const token = getToken();
  if (!token) {
    throw new Error('Not authenticated');
  }
  
  const url = new URL(endpoint, window.location.origin);
  Object.entries(params).forEach(([key, value]) => {
    if (value != null) url.searchParams.set(key, value);
  });
  
  const response = await fetch(url.toString(), {
    headers: {
      'Authorization': `Bearer ${token}`,
    },
  });
  
  const data = await response.json();
  
  if (!response.ok) {
    throw new Error(data.error?.message || 'API request failed');
  }
  
  return data;
}

/**
 * Download file from URL
 */
function downloadFile(url, filename) {
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
}

/**
 * Download text content as file
 */
function downloadText(content, filename, mimeType = 'text/plain') {
  const blob = new Blob([content], { type: mimeType });
  const url = URL.createObjectURL(blob);
  downloadFile(url, filename);
  URL.revokeObjectURL(url);
}

/**
 * Format duration (seconds to HH:MM:SS or MM:SS)
 */
function formatDuration(seconds) {
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = seconds % 60;
  
  if (h > 0) {
    return `${h}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
  }
  return `${m}:${s.toString().padStart(2, '0')}`;
}

/**
 * Format view count
 */
function formatViews(count) {
  if (count >= 1000000) return (count / 1000000).toFixed(1) + 'M';
  if (count >= 1000) return (count / 1000).toFixed(1) + 'K';
  return count.toString();
}

/**
 * Format bitrate
 */
function formatBitrate(bps) {
  if (bps >= 1000000) return (bps / 1000000).toFixed(1) + ' Mbps';
  if (bps >= 1000) return Math.round(bps / 1000) + ' kbps';
  return bps + ' bps';
}

/**
 * Escape HTML
 */
function escapeHtml(str) {
  const div = document.createElement('div');
  div.textContent = str;
  return div.innerHTML;
}

// Export for use in pages
window.YTExtractor = {
  getToken,
  setToken,
  clearToken,
  getMuxerPreference,
  setMuxerPreference,
  getMuxer,
  hasWebCodecs,
  parseYouTubeUrl,
  apiRequest,
  downloadFile,
  downloadText,
  formatDuration,
  formatViews,
  formatBitrate,
  escapeHtml,
  MuxerPreference,
};
