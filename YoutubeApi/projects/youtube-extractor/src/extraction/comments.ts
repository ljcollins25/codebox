/**
 * Comments extraction from YouTube
 */

import { throwExtractionFailed } from '../utils/errors';

export interface Comment {
  id: string;
  author: string;
  authorChannelId: string;
  text: string;
  likes: number;
  published: string;
  replyCount: number;
  repliesContinuation?: string;
}

export interface CommentsResponse {
  videoId: string;
  commentCount: number;
  comments: Comment[];
  continuation?: string;
}

/**
 * Innertube API context
 */
const INNERTUBE_CONTEXT = {
  client: {
    clientName: 'WEB',
    clientVersion: '2.20240101.00.00',
    hl: 'en',
    gl: 'US',
  },
};

/**
 * Extract initial comments continuation token from watch page
 */
export function extractCommentsContinuation(html: string): string | null {
  // Look for the continuation token in the page
  const patterns = [
    /"continuationCommand":\s*\{\s*"token":\s*"([^"]+)"[^}]*"request":\s*"CONTINUATION_REQUEST_TYPE_WATCH_NEXT"/,
    /"continuation":\s*"([^"]+)"[^}]*"clickTrackingParams"/,
  ];

  for (const pattern of patterns) {
    const match = html.match(pattern);
    if (match) {
      return match[1];
    }
  }

  return null;
}

/**
 * Fetch comments using continuation token
 */
export async function fetchComments(
  continuation: string,
  cookies?: string,
  sort: 'top' | 'newest' = 'top'
): Promise<CommentsResponse> {
  const url = 'https://www.youtube.com/youtubei/v1/next?prettyPrint=false';

  const headers: HeadersInit = {
    'Content-Type': 'application/json',
    'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
    'Origin': 'https://www.youtube.com',
    'Referer': 'https://www.youtube.com/',
  };

  if (cookies) {
    headers['Cookie'] = cookies;
  }

  const body = {
    context: INNERTUBE_CONTEXT,
    continuation,
  };

  const response = await fetch(url, {
    method: 'POST',
    headers,
    body: JSON.stringify(body),
  });

  if (!response.ok) {
    throwExtractionFailed(`Failed to fetch comments: ${response.status}`);
  }

  const data = await response.json();
  return parseCommentsResponse(data);
}

/**
 * Parse comments response from Innertube API
 */
function parseCommentsResponse(data: any): CommentsResponse {
  const comments: Comment[] = [];
  let continuation: string | undefined;
  let commentCount = 0;
  let videoId = '';

  // Navigate the response structure
  const endpoints = data.onResponseReceivedEndpoints || [];
  
  for (const endpoint of endpoints) {
    const continuationItems = 
      endpoint.reloadContinuationItemsCommand?.continuationItems ||
      endpoint.appendContinuationItemsAction?.continuationItems ||
      [];

    for (const item of continuationItems) {
      // Comment thread renderer
      if (item.commentThreadRenderer) {
        const comment = parseCommentThread(item.commentThreadRenderer);
        if (comment) {
          comments.push(comment);
        }
      }

      // Continuation token for next page
      if (item.continuationItemRenderer) {
        const continuationEndpoint = item.continuationItemRenderer.continuationEndpoint;
        if (continuationEndpoint?.continuationCommand?.token) {
          continuation = continuationEndpoint.continuationCommand.token;
        }
      }

      // Comment count header
      if (item.commentsHeaderRenderer) {
        const countText = item.commentsHeaderRenderer.countText?.runs?.[0]?.text;
        if (countText) {
          const match = countText.match(/[\d,]+/);
          if (match) {
            commentCount = parseInt(match[0].replace(/,/g, ''), 10);
          }
        }
      }
    }
  }

  return {
    videoId,
    commentCount,
    comments,
    continuation,
  };
}

/**
 * Parse a single comment thread
 */
function parseCommentThread(thread: any): Comment | null {
  const commentRenderer = thread.comment?.commentRenderer;
  if (!commentRenderer) return null;

  const contentText = commentRenderer.contentText?.runs
    ?.map((r: any) => r.text)
    .join('') || '';

  return {
    id: commentRenderer.commentId,
    author: commentRenderer.authorText?.simpleText || '',
    authorChannelId: commentRenderer.authorEndpoint?.browseEndpoint?.browseId || '',
    text: contentText,
    likes: commentRenderer.voteCount?.simpleText 
      ? parseInt(commentRenderer.voteCount.simpleText.replace(/[^0-9]/g, ''), 10) || 0
      : 0,
    published: commentRenderer.publishedTimeText?.runs?.[0]?.text || '',
    replyCount: commentRenderer.replyCount || 0,
    repliesContinuation: thread.replies?.commentRepliesRenderer?.contents?.[0]
      ?.continuationItemRenderer?.continuationEndpoint?.continuationCommand?.token,
  };
}

/**
 * Fetch replies to a comment
 */
export async function fetchReplies(
  continuation: string,
  cookies?: string
): Promise<{ replies: Comment[]; continuation?: string }> {
  const response = await fetchComments(continuation, cookies);
  
  return {
    replies: response.comments,
    continuation: response.continuation,
  };
}
