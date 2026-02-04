/**
 * API endpoint: /api/thumbnail
 */

import type { Env } from '../index';
import { extractToken, validateToken } from '../auth/login';
import { throwInvalidParams, throwMissingAuth } from '../utils/errors';

type ThumbnailQuality = 'default' | 'medium' | 'high' | 'max';

const THUMBNAIL_URLS: Record<ThumbnailQuality, string> = {
  default: 'https://i.ytimg.com/vi/{id}/default.jpg',
  medium: 'https://i.ytimg.com/vi/{id}/mqdefault.jpg',
  high: 'https://i.ytimg.com/vi/{id}/hqdefault.jpg',
  max: 'https://i.ytimg.com/vi/{id}/maxresdefault.jpg',
};

export async function handleApiThumbnail(
  request: Request,
  env: Env
): Promise<Response> {
  // Extract and validate token (optional for thumbnails)
  const token = extractToken(request);
  
  if (token) {
    await validateToken(env, token);
  }
  
  // Parse query params
  const url = new URL(request.url);
  const videoId = url.searchParams.get('v');
  const quality = (url.searchParams.get('quality') || 'high') as ThumbnailQuality;
  
  if (!videoId) {
    throwInvalidParams('Video ID (v) is required');
  }
  
  if (!THUMBNAIL_URLS[quality]) {
    throwInvalidParams('Invalid quality. Use: default, medium, high, or max');
  }
  
  const thumbnailUrl = THUMBNAIL_URLS[quality].replace('{id}', videoId);
  
  // Check if max quality exists (falls back to high if not)
  if (quality === 'max') {
    const response = await fetch(thumbnailUrl, { method: 'HEAD' });
    if (!response.ok) {
      return new Response(JSON.stringify({
        video_id: videoId,
        quality: 'high',
        url: THUMBNAIL_URLS.high.replace('{id}', videoId),
        note: 'Max resolution not available, using high',
      }), {
        headers: { 'Content-Type': 'application/json' },
      });
    }
  }
  
  return new Response(JSON.stringify({
    video_id: videoId,
    quality,
    url: thumbnailUrl,
  }), {
    headers: { 'Content-Type': 'application/json' },
  });
}
