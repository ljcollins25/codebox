/**
 * Error handling utilities
 */

export class AppError extends Error {
  constructor(
    public code: string,
    message: string,
    public status: number = 500
  ) {
    super(message);
    this.name = 'AppError';
  }
}

export const ErrorCodes = {
  MISSING_AUTH: 'missing_auth',
  INVALID_TOKEN: 'invalid_token',
  COOKIES_EXPIRED: 'cookies_expired',
  VIDEO_NOT_FOUND: 'video_not_found',
  VIDEO_PRIVATE: 'video_private',
  PLAYLIST_NOT_FOUND: 'playlist_not_found',
  RATE_LIMITED: 'rate_limited',
  EXTRACTION_FAILED: 'extraction_failed',
  INVALID_PARAMS: 'invalid_params',
  INTERNAL_ERROR: 'internal_error',
} as const;

export function createErrorResponse(
  code: string,
  message: string,
  status: number
): Response {
  return new Response(
    JSON.stringify({
      error: {
        code,
        message,
      },
    }),
    {
      status,
      headers: {
        'Content-Type': 'application/json',
      },
    }
  );
}

export function throwMissingAuth(): never {
  throw new AppError(ErrorCodes.MISSING_AUTH, 'Authorization header required', 401);
}

export function throwInvalidToken(): never {
  throw new AppError(ErrorCodes.INVALID_TOKEN, 'Invalid or expired token', 401);
}

export function throwCookiesExpired(): never {
  throw new AppError(ErrorCodes.COOKIES_EXPIRED, 'YouTube cookies have expired, please re-login', 403);
}

export function throwVideoNotFound(): never {
  throw new AppError(ErrorCodes.VIDEO_NOT_FOUND, 'Video not found', 404);
}

export function throwVideoPrivate(): never {
  throw new AppError(ErrorCodes.VIDEO_PRIVATE, 'Video is private or requires authentication', 403);
}

export function throwPlaylistNotFound(): never {
  throw new AppError(ErrorCodes.PLAYLIST_NOT_FOUND, 'Playlist not found', 404);
}

export function throwRateLimited(): never {
  throw new AppError(ErrorCodes.RATE_LIMITED, 'Rate limited by YouTube', 429);
}

export function throwExtractionFailed(details?: string): never {
  throw new AppError(
    ErrorCodes.EXTRACTION_FAILED,
    details ? `Extraction failed: ${details}` : 'Failed to extract video data',
    500
  );
}

export function throwInvalidParams(message: string): never {
  throw new AppError(ErrorCodes.INVALID_PARAMS, message, 400);
}
