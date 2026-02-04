/**
 * API endpoint: /api/comments
 */

import type { Env } from '../index';
import { extractToken, validateToken } from '../auth/login';
import { fetchYouTubePage } from '../extraction/player';
import { extractCommentsContinuation, fetchComments, fetchReplies } from '../extraction/comments';
import { updateToken } from '../storage/kv';
import { throwInvalidParams, throwMissingAuth, throwExtractionFailed } from '../utils/errors';

export async function handleApiComments(
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
  const continuation = url.searchParams.get('continuation');
  const repliesContinuation = url.searchParams.get('replies');
  const sort = (url.searchParams.get('sort') || 'top') as 'top' | 'newest';
  
  // Update last_used
  await updateToken(env, token, { last_used: new Date().toISOString() });
  
  // Handle replies request
  if (repliesContinuation) {
    const result = await fetchReplies(repliesContinuation, tokenData.youtube_cookies);
    return new Response(JSON.stringify(result), {
      headers: { 'Content-Type': 'application/json' },
    });
  }
  
  // Handle continuation (next page of comments)
  if (continuation) {
    const result = await fetchComments(continuation, tokenData.youtube_cookies, sort);
    return new Response(JSON.stringify(result), {
      headers: { 'Content-Type': 'application/json' },
    });
  }
  
  // Initial comments request - need video ID
  if (!videoId) {
    throwInvalidParams('Video ID (v) or continuation token is required');
  }
  
  // Fetch watch page to get initial continuation token
  const watchUrl = `https://www.youtube.com/watch?v=${videoId}`;
  const response = await fetchYouTubePage(watchUrl, tokenData.youtube_cookies);
  const html = await response.text();
  
  const initialContinuation = extractCommentsContinuation(html);
  
  if (!initialContinuation) {
    // Comments might be disabled
    return new Response(JSON.stringify({
      video_id: videoId,
      comment_count: 0,
      comments: [],
      continuation: null,
      note: 'Comments may be disabled for this video',
    }), {
      headers: { 'Content-Type': 'application/json' },
    });
  }
  
  // Fetch first page of comments
  const result = await fetchComments(initialContinuation, tokenData.youtube_cookies, sort);
  
  return new Response(JSON.stringify({
    video_id: videoId,
    ...result,
  }), {
    headers: { 'Content-Type': 'application/json' },
  });
}
