/**
 * /copilot/v1/* - OpenAI-Compatible Copilot Proxy
 * 
 * Accepts GitHub OAuth token in Authorization header, resolves to Copilot token
 * (cached), injects VS Code emulation headers, and forwards to Copilot API.
 */

import {
	Env,
	COPILOT_TOKEN_LIFETIME_MINUTES,
	COPILOT_API_BASE,
	VSCODE_HEADERS,
	corsHeaders,
	jsonResponse,
	errorResponse,
} from './shared';

interface CopilotTokenCache {
	copilot_token: string;
	expires_at: string;
}

/**
 * Extract GitHub token from Authorization header.
 * Accepts both "Bearer <token>" and raw "<token>".
 */
function extractGitHubToken(request: Request): string | null {
	const auth = request.headers.get('Authorization');
	if (!auth) return null;
	if (auth.toLowerCase().startsWith('bearer ')) {
		return auth.slice(7).trim();
	}
	return auth.trim();
}

/**
 * Generate cache key for Copilot token storage.
 * Uses HMAC if server secret is configured, otherwise simple hash.
 */
async function getCacheKey(githubToken: string, env: Env): Promise<string> {
	const encoder = new TextEncoder();

	if (env.SERVER_SECRET) {
		const key = await crypto.subtle.importKey(
			'raw',
			encoder.encode(env.SERVER_SECRET),
			{ name: 'HMAC', hash: 'SHA-256' },
			false,
			['sign']
		);
		const sig = await crypto.subtle.sign('HMAC', key, encoder.encode(githubToken));
		const hash = Array.from(new Uint8Array(sig)).map(b => b.toString(16).padStart(2, '0')).join('');
		return `copilot_v1:${hash.slice(0, 32)}`;
	}

	const hash = await crypto.subtle.digest('SHA-256', encoder.encode(githubToken));
	const hex = Array.from(new Uint8Array(hash)).map(b => b.toString(16).padStart(2, '0')).join('');
	return `copilot_v1:${hex.slice(0, 32)}`;
}

/**
 * Get Copilot token from GitHub token, using cache when possible.
 */
async function getCopilotToken(githubToken: string, env: Env): Promise<string> {
	const cacheKey = await getCacheKey(githubToken, env);

	const cached = await env.TOKEN_CACHE.get(cacheKey, 'json') as CopilotTokenCache | null;
	if (cached && new Date(cached.expires_at) > new Date()) {
		return cached.copilot_token;
	}

	const response = await fetch('https://api.github.com/copilot_internal/v2/token', {
		method: 'GET',
		headers: {
			'Authorization': `token ${githubToken}`,
			'Accept': 'application/json',
			...VSCODE_HEADERS,
		},
	});

	if (response.status === 401) {
		throw new Error('INVALID_GITHUB_TOKEN');
	}
	if (response.status === 403) {
		throw new Error('NO_COPILOT_ACCESS');
	}
	if (!response.ok) {
		const text = await response.text();
		throw new Error(`Copilot token request failed: ${response.status} - ${text}`);
	}

	const data = await response.json() as { token: string; expires_at?: number };

	const expiresAt = new Date(Date.now() + (COPILOT_TOKEN_LIFETIME_MINUTES - 2) * 60 * 1000);
	await env.TOKEN_CACHE.put(cacheKey, JSON.stringify({
		copilot_token: data.token,
		expires_at: expiresAt.toISOString(),
	}), {
		expirationTtl: (COPILOT_TOKEN_LIFETIME_MINUTES - 2) * 60,
	});

	return data.token;
}

/**
 * Handle requests to /copilot/v1/*
 */
export async function handleCopilotProxy(request: Request, env: Env, path: string): Promise<Response> {
	const githubToken = extractGitHubToken(request);
	if (!githubToken) {
		return errorResponse('Missing Authorization header. Provide GitHub OAuth token.', 401, 'auth_required');
	}

	let copilotToken: string;
	try {
		copilotToken = await getCopilotToken(githubToken, env);
	} catch (error) {
		const msg = String(error);
		if (msg.includes('INVALID_GITHUB_TOKEN')) {
			return errorResponse('Invalid GitHub token', 401, 'invalid_token');
		}
		if (msg.includes('NO_COPILOT_ACCESS')) {
			return errorResponse('GitHub account does not have Copilot access', 403, 'no_access');
		}
		return errorResponse(msg, 500, 'token_error');
	}

	const copilotPath = path.replace('/copilot/v1', '');
	const targetUrl = `${COPILOT_API_BASE}${copilotPath}`;

	const headers: Record<string, string> = {
		...VSCODE_HEADERS,
		'Authorization': `Bearer ${copilotToken}`,
	};

	let body: string | null = null;
	let isStreaming = false;

	if (request.method === 'POST') {
		const bodyData = await request.json() as Record<string, unknown>;
		isStreaming = bodyData.stream === true;
		body = JSON.stringify(bodyData);
	}

	const response = await fetch(targetUrl, {
		method: request.method,
		headers,
		body,
	});

	if (isStreaming && response.body) {
		return new Response(response.body, {
			status: response.status,
			headers: {
				'Content-Type': 'text/event-stream',
				'Cache-Control': 'no-cache',
				...corsHeaders(),
			},
		});
	}

	const data = await response.json();
	return jsonResponse(data, response.status);
}
