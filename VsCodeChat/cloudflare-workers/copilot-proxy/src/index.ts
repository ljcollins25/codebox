/**
 * Copilot Proxy - Cloudflare Worker
 * 
 * A layered proxy that:
 * - /login          - Initiates GitHub OAuth device flow (stateless)
 * - /copilot/v1/*   - OpenAI-compatible Copilot proxy (GitHub token -> Copilot token)
 * - /poe/server     - Poe Server Bot <-> OpenAI translation
 * - /               - Static UI served by Cloudflare static assets
 * 
 * See: copilot-proxy-spec.md for full specification
 */

import { Env, handleCORS, jsonResponse, errorResponse } from './shared';
import { handleLogin } from './login';
import { handleCopilotProxy } from './copilot';
import { handlePoeServer, handlePoeSettings } from './poe-server';

export { Env };

// =============================================================================
// Main Handler
// =============================================================================

export default {
	async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
		if (request.method === 'OPTIONS') {
			return handleCORS();
		}

		const url = new URL(request.url);
		const path = url.pathname;

		try {
			// /login - Device flow initiation (stateless)
			if (path === '/login' && (request.method === 'POST' || request.method === 'GET')) {
				return await handleLogin(request, env);
			}

			// /copilot/v1/* - OpenAI-compatible Copilot proxy
			if (path.startsWith('/copilot/v1/')) {
				return await handleCopilotProxy(request, env, path);
			}

			// /poe/server - Poe Server Bot translation
			if (path === '/poe/server' && request.method === 'POST') {
				return await handlePoeServer(request, env, url);
			}

			// /poe/settings - Poe parameter controls
			if (path === '/poe/settings' && request.method === 'POST') {
				return await handlePoeSettings(request, env, url);
			}

			// /health - Health check
			if (path === '/health' && request.method === 'GET') {
				return jsonResponse({ status: 'ok', timestamp: new Date().toISOString() });
			}

			// Static assets handle / automatically
			return jsonResponse({
				error: 'Not found',
				endpoints: [
					'GET|POST /login',
					'POST /copilot/v1/chat/completions',
					'POST /copilot/v1/completions',
					'GET  /copilot/v1/models',
					'POST /poe/server',
					'POST /poe/settings',
					'GET  /health',
				]
			}, 404);

		} catch (error) {
			console.error('Worker error:', error);
			return errorResponse(String(error), 500, 'internal_error');
		}
	},
};