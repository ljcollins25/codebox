/**
 * Media muxing utilities
 * 
 * Supports WebCodecs (preferred) and ffmpeg.wasm (fallback)
 */

// Muxer state
let ffmpegLoaded = false;
let ffmpegInstance = null;

/**
 * Check if WebCodecs is available
 */
function hasWebCodecs() {
  return 'VideoDecoder' in window && 
         'AudioDecoder' in window &&
         'VideoEncoder' in window &&
         'AudioEncoder' in window;
}

/**
 * Load ffmpeg.wasm (lazy loaded)
 */
async function loadFFmpeg() {
  if (ffmpegLoaded) return ffmpegInstance;
  
  // Dynamic import ffmpeg.wasm
  const { FFmpeg } = await import('https://unpkg.com/@ffmpeg/ffmpeg@0.12.6/dist/esm/index.js');
  const { fetchFile } = await import('https://unpkg.com/@ffmpeg/util@0.12.1/dist/esm/index.js');
  
  ffmpegInstance = new FFmpeg();
  
  // Load ffmpeg core
  await ffmpegInstance.load({
    coreURL: 'https://unpkg.com/@ffmpeg/core@0.12.4/dist/esm/ffmpeg-core.js',
    wasmURL: 'https://unpkg.com/@ffmpeg/core@0.12.4/dist/esm/ffmpeg-core.wasm',
  });
  
  ffmpegLoaded = true;
  
  // Store fetchFile for later use
  ffmpegInstance.fetchFile = fetchFile;
  
  return ffmpegInstance;
}

/**
 * Mux video and audio streams using ffmpeg.wasm
 * 
 * @param {string} videoUrl - URL to video stream
 * @param {string} audioUrl - URL to audio stream
 * @param {string} outputFilename - Desired output filename
 * @param {function} onProgress - Progress callback (0-100)
 * @returns {Blob} Muxed video blob
 */
async function muxWithFFmpeg(videoUrl, audioUrl, outputFilename, onProgress) {
  const ffmpeg = await loadFFmpeg();
  
  onProgress?.(5);
  
  // Fetch video and audio files
  const [videoData, audioData] = await Promise.all([
    fetch(videoUrl).then(r => r.arrayBuffer()),
    fetch(audioUrl).then(r => r.arrayBuffer()),
  ]);
  
  onProgress?.(30);
  
  // Write input files
  await ffmpeg.writeFile('video.mp4', new Uint8Array(videoData));
  await ffmpeg.writeFile('audio.m4a', new Uint8Array(audioData));
  
  onProgress?.(40);
  
  // Set up progress handler
  ffmpeg.on('progress', ({ progress }) => {
    onProgress?.(40 + progress * 50);
  });
  
  // Mux video and audio
  await ffmpeg.exec([
    '-i', 'video.mp4',
    '-i', 'audio.m4a',
    '-c', 'copy',
    '-movflags', '+faststart',
    'output.mp4'
  ]);
  
  onProgress?.(95);
  
  // Read output
  const outputData = await ffmpeg.readFile('output.mp4');
  
  // Cleanup
  await ffmpeg.deleteFile('video.mp4');
  await ffmpeg.deleteFile('audio.m4a');
  await ffmpeg.deleteFile('output.mp4');
  
  onProgress?.(100);
  
  return new Blob([outputData], { type: 'video/mp4' });
}

/**
 * Download and mux video
 * 
 * @param {object} options
 * @param {string} options.videoUrl - Video stream URL
 * @param {string} options.audioUrl - Audio stream URL (optional for combined formats)
 * @param {string} options.filename - Output filename
 * @param {function} options.onProgress - Progress callback
 * @param {string} options.preferredMuxer - 'auto', 'webcodecs', or 'ffmpeg'
 */
async function downloadAndMux({
  videoUrl,
  audioUrl,
  filename,
  onProgress,
  preferredMuxer = 'auto'
}) {
  // If no audio URL needed, just download video directly
  if (!audioUrl) {
    const response = await fetch(videoUrl);
    const blob = await response.blob();
    triggerDownload(blob, filename);
    return;
  }
  
  // Need to mux
  const useMuxer = preferredMuxer === 'auto' 
    ? 'ffmpeg'  // Default to ffmpeg for reliability
    : preferredMuxer;
  
  let blob;
  
  if (useMuxer === 'ffmpeg') {
    blob = await muxWithFFmpeg(videoUrl, audioUrl, filename, onProgress);
  } else {
    // WebCodecs muxing would go here
    // For now, fall back to ffmpeg
    blob = await muxWithFFmpeg(videoUrl, audioUrl, filename, onProgress);
  }
  
  triggerDownload(blob, filename);
}

/**
 * Trigger file download
 */
function triggerDownload(blob, filename) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}

/**
 * Check muxing support
 */
function getMuxingSupport() {
  return {
    webcodecs: hasWebCodecs(),
    ffmpeg: true, // Always available via CDN
  };
}

// Export
window.YTMuxer = {
  hasWebCodecs,
  loadFFmpeg,
  muxWithFFmpeg,
  downloadAndMux,
  getMuxingSupport,
};
