/**
 * API endpoint: /api/subtitles
 */

import type { Env } from '../index';
import { extractToken, validateToken } from '../auth/login';
import { extractVideoData } from '../extraction/player';
import { updateToken } from '../storage/kv';
import { throwInvalidParams, throwMissingAuth, throwUnauthorized } from '../utils/errors';

interface SubtitleFormat {
  vtt: string;
  srt: string;
  json3: string;
}

const FORMAT_PARAMS: Record<keyof SubtitleFormat, string> = {
  vtt: 'vtt',
  srt: 'srv1',
  json3: 'json3',
};

interface OAuthData {
  type: 'oauth';
  access_token: string;
  refresh_token?: string;
  token_expires_at: number;
}

function isOAuthToken(cookies: string): OAuthData | null {
  try {
    const parsed = JSON.parse(cookies);
    if (parsed.type === 'oauth' && parsed.access_token) {
      return parsed as OAuthData;
    }
  } catch {}
  return null;
}

/**
 * Fetch captions using YouTube Data API with OAuth token
 */
async function fetchCaptionsViaOAuth(
  videoId: string,
  accessToken: string,
  lang?: string | null,
  format?: keyof SubtitleFormat
): Promise<any> {
  // First, get list of caption tracks via YouTube Data API
  const listUrl = `https://www.googleapis.com/youtube/v3/captions?videoId=${videoId}&part=snippet`;
  
  const listResponse = await fetch(listUrl, {
    headers: {
      'Authorization': `Bearer ${accessToken}`,
      'Accept': 'application/json',
    },
  });
  
  if (!listResponse.ok) {
    const error = await listResponse.text();
    console.error('Captions list error:', listResponse.status, error);
    if (listResponse.status === 403) {
      // Try fallback to innertube API
      return fetchCaptionsViaInnertube(videoId, accessToken, lang, format);
    }
    throw new Error(`Failed to fetch captions: ${listResponse.status}`);
  }
  
  const listData = await listResponse.json() as {
    items?: Array<{
      id: string;
      snippet: {
        videoId: string;
        language: string;
        name: string;
        trackKind: string;
        isAutoSynced?: boolean;
      };
    }>;
  };
  
  const available = (listData.items || []).map(item => ({
    id: item.id,
    code: item.snippet.language,
    name: item.snippet.name || item.snippet.language,
    auto: item.snippet.trackKind === 'ASR' || item.snippet.isAutoSynced,
  }));
  
  const response: any = {
    video_id: videoId,
    available,
  };
  
  // If language requested, download that track
  if (lang && available.length > 0) {
    const track = available.find(
      t => t.code === lang || t.code.startsWith(lang + '-')
    );
    
    if (!track) {
      response.subtitles = null;
      response.error = `Language '${lang}' not available`;
    } else {
      // Download the caption track
      // Note: The captions.download endpoint requires the caption ID
      const downloadUrl = `https://www.googleapis.com/youtube/v3/captions/${track.id}?tfmt=${format === 'srt' ? 'srt' : 'vtt'}`;
      
      const downloadResponse = await fetch(downloadUrl, {
        headers: {
          'Authorization': `Bearer ${accessToken}`,
        },
      });
      
      if (downloadResponse.ok) {
        const content = await downloadResponse.text();
        response.subtitles = {
          lang,
          format: format || 'vtt',
          content,
        };
      } else {
        response.subtitles = null;
        response.error = `Failed to download subtitles: ${downloadResponse.status}`;
      }
    }
  }
  
  return response;
}

/**
 * Fallback: Use InnerTube API with OAuth (for videos where Data API fails)
 */
async function fetchCaptionsViaInnertube(
  videoId: string,
  accessToken: string,
  lang?: string | null,
  format?: keyof SubtitleFormat
): Promise<any> {
  // Use the player endpoint to get caption tracks
  const playerUrl = 'https://www.youtube.com/youtubei/v1/player';
  
  const playerResponse = await fetch(playerUrl, {
    method: 'POST',
    headers: {
      'Authorization': `Bearer ${accessToken}`,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({
      videoId,
      context: {
        client: {
          clientName: 'TVHTML5',
          clientVersion: '7.20240724.13.00',
        },
      },
    }),
  });
  
  if (!playerResponse.ok) {
    throw new Error(`InnerTube player failed: ${playerResponse.status}`);
  }
  
  const data = await playerResponse.json() as any;
  
  const captions = data.captions?.playerCaptionsTracklistRenderer?.captionTracks || [];
  
  const available = captions.map((track: any) => ({
    code: track.languageCode,
    name: track.name?.simpleText || track.languageCode,
    auto: track.kind === 'asr',
    baseUrl: track.baseUrl,
  }));
  
  const response: any = {
    video_id: videoId,
    title: data.videoDetails?.title,
    available: available.map((t: any) => ({
      code: t.code,
      name: t.name,
      auto: t.auto,
    })),
  };
  
  if (lang && available.length > 0) {
    const track = available.find(
      (t: any) => t.code === lang || t.code.startsWith(lang + '-')
    );
    
    if (!track) {
      response.subtitles = null;
      response.error = `Language '${lang}' not available`;
    } else if (track.baseUrl) {
      const subtitleUrl = new URL(track.baseUrl);
      subtitleUrl.searchParams.set('fmt', FORMAT_PARAMS[format || 'vtt']);
      
      const subtitleResponse = await fetch(subtitleUrl.toString(), {
        headers: {
          'Authorization': `Bearer ${accessToken}`,
        },
      });
      
      if (subtitleResponse.ok) {
        response.subtitles = {
          lang,
          format: format || 'vtt',
          content: await subtitleResponse.text(),
        };
      } else {
        response.subtitles = null;
        response.error = `Failed to download subtitles`;
      }
    }
  }
  
  return response;
}

export async function handleApiSubtitles(
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
  const videoId = url.searchParams.get('v');
  const lang = url.searchParams.get('lang');
  const format = (url.searchParams.get('format') || 'vtt') as keyof SubtitleFormat;
  
  if (!videoId) {
    throwInvalidParams('Video ID (v) is required');
  }
  
  if (format && !FORMAT_PARAMS[format]) {
    throwInvalidParams('Invalid format. Use: vtt, srt, or json3');
  }
  
  // Update last_used
  await updateToken(env, token, { last_used: new Date().toISOString() });
  
  // Check if this is an OAuth token
  const oauthData = isOAuthToken(tokenData.youtube_cookies);
  
  if (oauthData) {
    // Use OAuth-based caption fetching (YouTube Data API or InnerTube)
    try {
      const response = await fetchCaptionsViaInnertube(
        videoId!,
        oauthData.access_token,
        lang,
        format
      );
      return new Response(JSON.stringify(response), {
        headers: { 'Content-Type': 'application/json' },
      });
    } catch (error: any) {
      return new Response(JSON.stringify({
        video_id: videoId,
        error: error.message || 'Failed to fetch captions',
      }), {
        status: 500,
        headers: { 'Content-Type': 'application/json' },
      });
    }
  }
  
  // Cookie-based extraction (original method)
  const { playerResponse } = await extractVideoData(videoId!, tokenData.youtube_cookies);
  
  // Build response
  const response: any = {
    video_id: videoId,
    title: playerResponse.videoDetails.title,
    available: playerResponse.captionTracks.map(track => ({
      code: track.languageCode,
      name: track.name,
      auto: track.isAutoGenerated,
    })),
  };
  
  // If language requested, fetch subtitle content
  if (lang) {
    const track = playerResponse.captionTracks.find(
      t => t.languageCode === lang || t.languageCode.startsWith(lang + '-')
    );
    
    if (!track) {
      return new Response(JSON.stringify({
        ...response,
        subtitles: null,
        error: `Language '${lang}' not available`,
      }), {
        headers: { 'Content-Type': 'application/json' },
      });
    }
    
    // Fetch subtitle content
    const subtitleUrl = new URL(track.baseUrl);
    subtitleUrl.searchParams.set('fmt', FORMAT_PARAMS[format]);
    
    const subtitleResponse = await fetch(subtitleUrl.toString(), {
      headers: {
        'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
        'Cookie': tokenData.youtube_cookies,
      },
    });
    
    const content = await subtitleResponse.text();
    
    response.subtitles = {
      lang,
      format,
      content,
    };
  }
  
  return new Response(JSON.stringify(response), {
    headers: { 'Content-Type': 'application/json' },
  });
}
