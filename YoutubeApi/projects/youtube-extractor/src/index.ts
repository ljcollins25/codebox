/**
 * YouTube Extraction Service
 * Main entry point for Cloudflare Worker
 */

import { Router } from './router';
import { handleApiSubtitles } from './api/subtitles';
import { handleApiVideo } from './api/video';
import { handleApiPlaylist } from './api/playlist';
import { handleApiThumbnail } from './api/thumbnail';
import { handleApiComments } from './api/comments';
import { handleApiStatus } from './api/status';
import { handleProxy } from './proxy/handler';
import { handleLogin, handleToken, handleTokenRevoke, handleOAuthStart, handleOAuthCallback, handleOAuthComplete, handleDeviceStart, handleDevicePoll, handleDeviceStatus } from './auth/login';
import { handleLandingPage } from './pages/landing';
import { handleVideoPage } from './pages/video';
import { handlePlaylistPage } from './pages/playlist';
import { createErrorResponse, AppError } from './utils/errors';

export interface Env {
  TOKENS: KVNamespace;
  CACHE: KVNamespace;
  WORKER_URL: string;
  ASSETS: Fetcher;
}

export default {
  async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    const url = new URL(request.url);
    
    // Serve static assets directly (before router)
    if (url.pathname.startsWith('/static/')) {
      // Remove /static/ prefix for asset lookup
      const assetPath = url.pathname.replace('/static/', '/');
      const assetUrl = new URL(assetPath, request.url);
      const assetRequest = new Request(assetUrl, request);
      
      try {
        const response = await env.ASSETS.fetch(assetRequest);
        if (response.status !== 404) {
          // Add proper headers for service worker
          const headers = new Headers(response.headers);
          if (url.pathname.endsWith('.js')) {
            headers.set('Content-Type', 'application/javascript');
            headers.set('Service-Worker-Allowed', '/');
          }
          return new Response(response.body, {
            status: response.status,
            headers
          });
        }
      } catch (e) {
        // Fall through to 404
      }
      return new Response('Not found', { status: 404 });
    }
    
    const router = new Router();

    // Landing page
    router.get('/', (req) => handleLandingPage(req, env));

    // Auth routes
    router.get('/login', (req) => handleLogin(req, env));
    router.post('/login', (req) => handleLogin(req, env));
    router.get('/token', (req) => handleToken(req, env));
    router.post('/token/revoke', (req) => handleTokenRevoke(req, env));
    
    // OAuth service worker flow
    router.get('/oauth/start', (req) => handleOAuthStart(req, env));
    router.post('/oauth/callback', (req) => handleOAuthCallback(req, env));
    router.get('/oauth/complete', (req) => handleOAuthComplete(req, env));
    
    // Device flow OAuth
    router.post('/device/start', (req) => handleDeviceStart(req, env));
    router.get('/device/poll', (req) => handleDevicePoll(req, env));
    router.get('/device/status', (req) => handleDeviceStatus(req, env));

    // Proxy routes (for OAuth flow)
    router.get('/proxy/*', (req) => handleProxy(req, env));
    router.post('/proxy/*', (req) => handleProxy(req, env));
    
    // Auth proxy routes (for service worker)
    router.get('/auth/*', (req) => handleProxy(req, env));
    router.post('/auth/*', (req) => handleProxy(req, env));

    // API routes (token-based)
    router.get('/api/subtitles', (req) => handleApiSubtitles(req, env));
    router.get('/api/video', (req) => handleApiVideo(req, env));
    router.get('/api/playlist', (req) => handleApiPlaylist(req, env));
    router.get('/api/thumbnail', (req) => handleApiThumbnail(req, env));
    router.get('/api/comments', (req) => handleApiComments(req, env));
    router.get('/api/status', (req) => handleApiStatus(req, env));

    // Web pages
    router.get('/video/:id', (req, params) => handleVideoPage(req, env, params.id));
    router.get('/playlist/:id', (req, params) => handlePlaylistPage(req, env, params.id));

    // Static assets are handled by Cloudflare Sites

    try {
      const response = await router.handle(request);
      return response;
    } catch (error) {
      if (error instanceof AppError) {
        return createErrorResponse(error.code, error.message, error.status);
      }
      console.error('Unhandled error:', error);
      return createErrorResponse('internal_error', 'An unexpected error occurred', 500);
    }
  },
};
