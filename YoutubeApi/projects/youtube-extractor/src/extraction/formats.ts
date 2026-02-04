/**
 * Format handling - build final video URLs
 */

import type { Env } from '../index';
import type { Format, PlayerResponse } from './player';
import { extractDecipherFunction, createDecipherExecutor, decipherSignatureCipher } from './decipher';
import { extractNTransformFunction, createNTransformExecutor, applyNTransform } from './ntransform';
import { getCachedPlayerFunctions, cachePlayerFunctions } from '../storage/kv';
import { throwExtractionFailed } from '../utils/errors';

export interface ProcessedFormat extends Format {
  url: string;
  processed: true;
}

/**
 * Get or extract player functions (decipher, n-transform)
 */
async function getPlayerFunctions(
  env: Env,
  playerJsUrl: string
): Promise<{
  decipherFn: (sig: string) => string;
  nTransformFn: (n: string) => string;
}> {
  // Extract player version from URL
  const versionMatch = playerJsUrl.match(/\/player\/([a-zA-Z0-9]+)\//);
  const playerVersion = versionMatch ? versionMatch[1] : 'unknown';

  // Check cache first
  const cached = await getCachedPlayerFunctions(env, playerVersion);
  if (cached) {
    return {
      decipherFn: createDecipherExecutor(cached.decipherFunction),
      nTransformFn: createNTransformExecutor(cached.nTransformFunction),
    };
  }

  // Fetch base.js
  const response = await fetch(playerJsUrl, {
    headers: {
      'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
    },
  });

  if (!response.ok) {
    throwExtractionFailed(`Failed to fetch player JS: ${response.status}`);
  }

  const baseJs = await response.text();

  // Extract functions
  const decipherCode = extractDecipherFunction(baseJs);
  const nTransformCode = extractNTransformFunction(baseJs);

  // Cache for later
  await cachePlayerFunctions(env, playerVersion, decipherCode, nTransformCode);

  return {
    decipherFn: createDecipherExecutor(decipherCode),
    nTransformFn: createNTransformExecutor(nTransformCode),
  };
}

/**
 * Process a single format to get final URL
 */
function processFormat(
  format: Format,
  decipherFn: (sig: string) => string,
  nTransformFn: (n: string) => string
): ProcessedFormat {
  let url: string;

  if (format.url) {
    // Direct URL, just apply n-transform
    url = format.url;
  } else if (format.signatureCipher) {
    // Need to decipher
    url = decipherSignatureCipher(format.signatureCipher, decipherFn);
  } else {
    throwExtractionFailed(`Format ${format.itag} has no URL or cipher`);
  }

  // Apply n-transform to prevent throttling
  url = applyNTransform(url, nTransformFn);

  return {
    ...format,
    url,
    processed: true,
  };
}

/**
 * Process all formats in a player response
 */
export async function processFormats(
  env: Env,
  playerResponse: PlayerResponse
): Promise<ProcessedFormat[]> {
  const { decipherFn, nTransformFn } = await getPlayerFunctions(
    env,
    playerResponse.playerJsUrl
  );

  const allFormats = [...playerResponse.formats, ...playerResponse.adaptiveFormats];
  const processedFormats: ProcessedFormat[] = [];

  for (const format of allFormats) {
    try {
      processedFormats.push(processFormat(format, decipherFn, nTransformFn));
    } catch (e) {
      // Skip formats that fail to process
      console.warn(`Failed to process format ${format.itag}:`, e);
    }
  }

  return processedFormats;
}

/**
 * Get recommended format combination
 */
export function getRecommendedFormats(formats: ProcessedFormat[]): {
  video: ProcessedFormat | null;
  audio: ProcessedFormat | null;
  combined: ProcessedFormat | null;
  needsMuxing: boolean;
} {
  // Find best combined format (has both video and audio)
  const combined = formats
    .filter(f => f.hasVideo && f.hasAudio)
    .sort((a, b) => (b.height || 0) - (a.height || 0))[0] || null;

  // Find best video-only format
  const video = formats
    .filter(f => f.hasVideo && !f.hasAudio)
    .sort((a, b) => (b.height || 0) - (a.height || 0))[0] || null;

  // Find best audio-only format
  const audio = formats
    .filter(f => f.hasAudio && !f.hasVideo)
    .sort((a, b) => b.bitrate - a.bitrate)[0] || null;

  // Determine if muxing is needed for best quality
  const needsMuxing = !combined || (video && video.height && combined.height && video.height > combined.height);

  return {
    video,
    audio,
    combined,
    needsMuxing,
  };
}

/**
 * Quality presets
 */
export const QualityPresets = {
  best: (formats: ProcessedFormat[]) => {
    const { video, audio, combined, needsMuxing } = getRecommendedFormats(formats);
    if (needsMuxing && video && audio) {
      return { video, audio, needsMuxing: true };
    }
    return { video: combined, audio: null, needsMuxing: false };
  },
  
  '1080p': (formats: ProcessedFormat[]) => findByQuality(formats, 1080),
  '720p': (formats: ProcessedFormat[]) => findByQuality(formats, 720),
  '480p': (formats: ProcessedFormat[]) => findByQuality(formats, 480),
  '360p': (formats: ProcessedFormat[]) => findByQuality(formats, 360),
  
  audioOnly: (formats: ProcessedFormat[]) => {
    const audio = formats
      .filter(f => f.hasAudio && !f.hasVideo)
      .sort((a, b) => b.bitrate - a.bitrate)[0];
    return { video: null, audio, needsMuxing: false };
  },
};

function findByQuality(formats: ProcessedFormat[], targetHeight: number) {
  // Try combined format first
  const combined = formats
    .filter(f => f.hasVideo && f.hasAudio && f.height === targetHeight)
    .sort((a, b) => b.bitrate - a.bitrate)[0];
  
  if (combined) {
    return { video: combined, audio: null, needsMuxing: false };
  }

  // Fall back to separate streams
  const video = formats
    .filter(f => f.hasVideo && !f.hasAudio && f.height === targetHeight)
    .sort((a, b) => b.bitrate - a.bitrate)[0];
  
  const audio = formats
    .filter(f => f.hasAudio && !f.hasVideo)
    .sort((a, b) => b.bitrate - a.bitrate)[0];

  if (video && audio) {
    return { video, audio, needsMuxing: true };
  }

  return { video: null, audio: null, needsMuxing: false };
}
