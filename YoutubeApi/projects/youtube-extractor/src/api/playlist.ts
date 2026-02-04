/**
 * API endpoint: /api/playlist
 */

import type { Env } from '../index';
import { extractToken, validateToken } from '../auth/login';
import { fetchYouTubePage } from '../extraction/player';
import { updateToken } from '../storage/kv';
import { throwInvalidParams, throwMissingAuth, throwPlaylistNotFound, throwExtractionFailed } from '../utils/errors';

interface PlaylistVideo {
  video_id: string;
  title: string;
  duration_seconds: number;
  thumbnail: string;
  index: number;
}

interface PlaylistData {
  playlist_id: string;
  title: string;
  author: string;
  video_count: number;
  videos: PlaylistVideo[];
}

export async function handleApiPlaylist(
  request: Request,
  env: Env
): Promise<Response> {
  // Extract and validate token
  const token = extractToken(request);
  if (!token) {
    throwMissingAuth();
  }
  
  const tokenData = await validateToken(env, token);
  
  // Parse query params
  const url = new URL(request.url);
  const playlistId = url.searchParams.get('list');
  
  if (!playlistId) {
    throwInvalidParams('Playlist ID (list) is required');
  }
  
  // Fetch playlist page
  const playlistUrl = `https://www.youtube.com/playlist?list=${playlistId}`;
  const response = await fetchYouTubePage(playlistUrl, tokenData.youtube_cookies);
  const html = await response.text();
  
  // Update last_used
  await updateToken(env, token, { last_used: new Date().toISOString() });
  
  // Extract playlist data from ytInitialData
  const playlistData = extractPlaylistData(html, playlistId);
  
  return new Response(JSON.stringify(playlistData), {
    headers: { 'Content-Type': 'application/json' },
  });
}

function extractPlaylistData(html: string, playlistId: string): PlaylistData {
  // Find ytInitialData
  const patterns = [
    /var\s+ytInitialData\s*=\s*(\{.+?\});/s,
    /ytInitialData\s*=\s*(\{.+?\})\s*;/s,
    /window\["ytInitialData"\]\s*=\s*(\{.+?\});/s,
  ];

  let initialData: any = null;

  for (const pattern of patterns) {
    const match = html.match(pattern);
    if (match) {
      try {
        initialData = JSON.parse(extractBalancedJson(match[1]));
        break;
      } catch {
        continue;
      }
    }
  }

  if (!initialData) {
    throwExtractionFailed('Could not find playlist data');
  }

  // Navigate to playlist content
  const contents = initialData.contents?.twoColumnBrowseResultsRenderer?.tabs?.[0]
    ?.tabRenderer?.content?.sectionListRenderer?.contents?.[0]
    ?.itemSectionRenderer?.contents?.[0]?.playlistVideoListRenderer?.contents;

  if (!contents) {
    // Check if playlist is private or doesn't exist
    const alerts = initialData.alerts || [];
    for (const alert of alerts) {
      const text = alert.alertRenderer?.text?.simpleText || 
                   alert.alertRenderer?.text?.runs?.[0]?.text;
      if (text) {
        throwPlaylistNotFound();
      }
    }
    throwPlaylistNotFound();
  }

  // Extract playlist metadata
  const metadata = initialData.metadata?.playlistMetadataRenderer || {};
  const sidebar = initialData.sidebar?.playlistSidebarRenderer?.items?.[0]
    ?.playlistSidebarPrimaryInfoRenderer;
  
  const title = metadata.title || 'Unknown Playlist';
  const author = initialData.sidebar?.playlistSidebarRenderer?.items?.[1]
    ?.playlistSidebarSecondaryInfoRenderer?.videoOwner?.videoOwnerRenderer
    ?.title?.runs?.[0]?.text || 'Unknown';

  // Extract videos
  const videos: PlaylistVideo[] = [];
  let index = 1;

  for (const item of contents) {
    if (item.playlistVideoRenderer) {
      const video = item.playlistVideoRenderer;
      
      // Skip unavailable videos
      if (!video.videoId) continue;

      const duration = parseDuration(
        video.lengthText?.simpleText || 
        video.lengthText?.runs?.[0]?.text || 
        '0:00'
      );

      videos.push({
        video_id: video.videoId,
        title: video.title?.runs?.[0]?.text || 'Unknown Title',
        duration_seconds: duration,
        thumbnail: video.thumbnail?.thumbnails?.[0]?.url || '',
        index: index++,
      });
    }

    // Handle continuation (load more)
    if (item.continuationItemRenderer) {
      // For now, we just return what we have
      // Full implementation would fetch continuation
    }
  }

  return {
    playlist_id: playlistId,
    title,
    author,
    video_count: videos.length,
    videos,
  };
}

function parseDuration(durationStr: string): number {
  const parts = durationStr.split(':').map(Number);
  
  if (parts.length === 3) {
    // HH:MM:SS
    return parts[0] * 3600 + parts[1] * 60 + parts[2];
  } else if (parts.length === 2) {
    // MM:SS
    return parts[0] * 60 + parts[1];
  }
  
  return 0;
}

function extractBalancedJson(str: string): string {
  let depth = 0;
  let inString = false;
  let escape = false;

  for (let i = 0; i < str.length; i++) {
    const char = str[i];

    if (escape) {
      escape = false;
      continue;
    }

    if (char === '\\' && inString) {
      escape = true;
      continue;
    }

    if (char === '"' && !escape) {
      inString = !inString;
      continue;
    }

    if (!inString) {
      if (char === '{') depth++;
      if (char === '}') {
        depth--;
        if (depth === 0) {
          return str.substring(0, i + 1);
        }
      }
    }
  }

  return str;
}
