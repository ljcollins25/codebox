/**
 * GitHub Copilot API Proxy - Cloudflare Worker
 * 
 * This worker proxies requests to the GitHub Copilot API.
 * Authentication must be handled separately - client provides Copilot token.
 * 
 * Usage:
 *   POST /chat/completions - Proxy to Copilot chat API
 *   POST /completions - Proxy to Copilot code completions API
 *   GET /models - Get available models
 */

export interface Env {
	// Optional: Store a default Copilot token in secrets (not recommended for production)
	COPILOT_TOKEN?: string;
	// Optional: Allowed origins for CORS
	ALLOWED_ORIGINS?: string;
}

const COPILOT_API_BASE = 'https://api.githubcopilot.com';
const GITHUB_API_BASE = 'https://api.github.com';

// Headers required by Copilot API
const COPILOT_HEADERS = {
	'X-GitHub-Api-Version': '2025-05-01',
	'OpenAI-Intent': 'chat',
	'Openai-Organization': 'github-copilot',
	'Content-Type': 'application/json',
};

export default {
	async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
		// Handle CORS preflight
		if (request.method === 'OPTIONS') {
			return handleCORS(request, env);
		}

		const url = new URL(request.url);
		const path = url.pathname;

		try {
			// Get Copilot token from Authorization header or env
			const authHeader = request.headers.get('Authorization');
			const copilotToken = authHeader?.replace(/^Bearer\s+/i, '') || env.COPILOT_TOKEN;

			if (!copilotToken) {
				return jsonResponse({ error: 'Missing Copilot token. Provide via Authorization: Bearer <token>' }, 401);
			}

			// Route requests
			if (path === '/chat/completions' && request.method === 'POST') {
				return await proxyChatCompletions(request, copilotToken, env);
			}
			
			if (path === '/completions' && request.method === 'POST') {
				return await proxyCompletions(request, copilotToken, env);
			}
			
			if (path === '/models' && request.method === 'GET') {
				return await proxyModels(copilotToken, env);
			}

			if (path === '/v1/messages' && request.method === 'POST') {
				return await proxyMessages(request, copilotToken, env);
			}

			// Token exchange endpoint (requires GitHub OAuth token)
			if (path === '/token' && request.method === 'POST') {
				return await exchangeToken(request, env);
			}

			// Health check
			if (path === '/health') {
				return jsonResponse({ status: 'ok', timestamp: new Date().toISOString() });
			}

			return jsonResponse({ error: 'Not found', endpoints: ['/chat/completions', '/completions', '/models', '/token', '/health'] }, 404);

		} catch (error) {
			console.error('Worker error:', error);
			return jsonResponse({ error: 'Internal server error', message: String(error) }, 500);
		}
	},
};

/**
 * Proxy chat completions to Copilot API
 */
async function proxyChatCompletions(request: Request, token: string, env: Env): Promise<Response> {
	const body = await request.json() as Record<string, unknown>;
	const requestId = crypto.randomUUID();

	const response = await fetch(`${COPILOT_API_BASE}/chat/completions`, {
		method: 'POST',
		headers: {
			...COPILOT_HEADERS,
			'Authorization': `Bearer ${token}`,
			'X-Request-Id': requestId,
			'X-Interaction-Type': 'chat',
		},
		body: JSON.stringify(body),
	});

	// Handle streaming responses
	if (body.stream && response.body) {
		return new Response(response.body, {
			status: response.status,
			headers: {
				'Content-Type': 'text/event-stream',
				'Cache-Control': 'no-cache',
				'Connection': 'keep-alive',
				...corsHeaders(env),
			},
		});
	}

	const data = await response.json();
	return jsonResponse(data, response.status, env);
}

/**
 * Proxy code completions to Copilot API
 */
async function proxyCompletions(request: Request, token: string, env: Env): Promise<Response> {
	const body = await request.json();
	const requestId = crypto.randomUUID();

	const response = await fetch(`${COPILOT_API_BASE}/completions`, {
		method: 'POST',
		headers: {
			...COPILOT_HEADERS,
			'Authorization': `Bearer ${token}`,
			'X-Request-Id': requestId,
			'X-Interaction-Type': 'completion',
			'OpenAI-Intent': 'completion',
		},
		body: JSON.stringify(body),
	});

	// Handle streaming
	if (response.headers.get('content-type')?.includes('event-stream') && response.body) {
		return new Response(response.body, {
			status: response.status,
			headers: {
				'Content-Type': 'text/event-stream',
				'Cache-Control': 'no-cache',
				...corsHeaders(env),
			},
		});
	}

	const data = await response.json();
	return jsonResponse(data, response.status, env);
}

/**
 * Proxy messages endpoint (Anthropic-style API)
 */
async function proxyMessages(request: Request, token: string, env: Env): Promise<Response> {
	const body = await request.json() as Record<string, unknown>;
	const requestId = crypto.randomUUID();

	const response = await fetch(`${COPILOT_API_BASE}/v1/messages`, {
		method: 'POST',
		headers: {
			...COPILOT_HEADERS,
			'Authorization': `Bearer ${token}`,
			'X-Request-Id': requestId,
		},
		body: JSON.stringify(body),
	});

	if (body.stream && response.body) {
		return new Response(response.body, {
			status: response.status,
			headers: {
				'Content-Type': 'text/event-stream',
				'Cache-Control': 'no-cache',
				...corsHeaders(env),
			},
		});
	}

	const data = await response.json();
	return jsonResponse(data, response.status, env);
}

/**
 * Get available models
 */
async function proxyModels(token: string, env: Env): Promise<Response> {
	const response = await fetch(`${COPILOT_API_BASE}/models`, {
		method: 'GET',
		headers: {
			'Authorization': `Bearer ${token}`,
			'X-GitHub-Api-Version': '2025-05-01',
		},
	});

	const data = await response.json();
	return jsonResponse(data, response.status, env);
}

/**
 * Exchange GitHub OAuth token for Copilot token
 * POST /token with { "github_token": "gho_xxx" }
 */
async function exchangeToken(request: Request, env: Env): Promise<Response> {
	const body = await request.json() as { github_token?: string };
	
	if (!body.github_token) {
		return jsonResponse({ error: 'Missing github_token in request body' }, 400);
	}

	const response = await fetch(`${GITHUB_API_BASE}/copilot_internal/v2/token`, {
		method: 'GET',
		headers: {
			'Authorization': `token ${body.github_token}`,
			'X-GitHub-Api-Version': '2025-04-01',
		},
	});

	if (!response.ok) {
		const error = await response.text();
		return jsonResponse({ 
			error: 'Token exchange failed', 
			status: response.status,
			details: error 
		}, response.status, env);
	}

	const tokenData = await response.json();
	return jsonResponse(tokenData, 200, env);
}

/**
 * CORS headers
 */
function corsHeaders(env?: Env): Record<string, string> {
	return {
		'Access-Control-Allow-Origin': env?.ALLOWED_ORIGINS || '*',
		'Access-Control-Allow-Methods': 'GET, POST, OPTIONS',
		'Access-Control-Allow-Headers': 'Content-Type, Authorization, X-Request-Id',
		'Access-Control-Max-Age': '86400',
	};
}

function handleCORS(request: Request, env: Env): Response {
	return new Response(null, {
		status: 204,
		headers: corsHeaders(env),
	});
}

function jsonResponse(data: unknown, status = 200, env?: Env): Response {
	return new Response(JSON.stringify(data, null, 2), {
		status,
		headers: {
			'Content-Type': 'application/json',
			...corsHeaders(env),
		},
	});
}
