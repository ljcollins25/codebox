/**
 * GitHub Copilot Token Service - Cloudflare Worker
 * 
 * Uses GitHub Device Flow for authentication (no client secret required).
 * Provides a stable API endpoint that handles Copilot token refresh automatically.
 * 
 * Flow:
 *   1. User visits / → sees login page
 *   2. User clicks login → gets a device code and goes to github.com/login/device
 *   3. User enters code on GitHub, worker polls for completion
 *   4. Worker gets GitHub token, stores in KV with session ID
 *   5. User gets an API key (sk-copilot-xxx) that works permanently
 *   6. /v1/* endpoints proxy to Copilot, auto-refreshing tokens as needed
 */

export interface Env {
	// KV namespace for storing sessions (required for persistent auth)
	SESSIONS: KVNamespace;
	// KV namespace for storing pending device flow states
	DEVICE_FLOWS: KVNamespace;
}

interface StoredSession {
	github_token: string;
	github_user: string;
	copilot_token?: string;
	copilot_expires_at?: number;
	created_at: number;
	last_used: number;
}

interface DeviceFlowState {
	device_code: string;
	user_code: string;
	verification_uri: string;
	expires_at: number;
	interval: number;
}

// VS Code's GitHub OAuth App client ID (public, no secret needed for device flow)
const GITHUB_CLIENT_ID = '01ab8ac9400c4e429b23';

const GITHUB_DEVICE_CODE_URL = 'https://github.com/login/device/code';
const GITHUB_TOKEN_URL = 'https://github.com/login/oauth/access_token';
const GITHUB_API_URL = 'https://api.github.com';
const COPILOT_API_URL = 'https://api.githubcopilot.com';
const COPILOT_TOKEN_URL = 'https://api.github.com/copilot_internal/v2/token';

// Scopes needed for Copilot
const OAUTH_SCOPES = 'read:user';

// Token refresh buffer (refresh 5 minutes before expiry)
const TOKEN_REFRESH_BUFFER = 5 * 60;

export default {
	async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
		const url = new URL(request.url);
		const path = url.pathname;

		// Handle CORS preflight
		if (request.method === 'OPTIONS') {
			return new Response(null, { status: 204, headers: corsHeaders() });
		}

		try {
			// Public routes
			if (path === '/') return renderHomePage();
			if (path === '/login') return startDeviceFlow(env);
			if (path === '/callback') return renderDeviceFlowPage(url, env);
			if (path === '/poll') return pollDeviceFlow(request, env);
			if (path === '/health') return jsonResponse({ status: 'ok' });

			// Authenticated routes - check for session/API key
			const sessionId = getSessionId(request);
			
			// Dashboard (requires session cookie)
			if (path === '/dashboard') {
				if (!sessionId) return Response.redirect(`${url.origin}/`, 302);
				return renderDashboard(sessionId, env);
			}

			// Logout
			if (path === '/logout') {
				if (sessionId) await env.SESSIONS.delete(sessionId);
				return new Response(null, {
					status: 302,
					headers: {
						'Location': '/',
						'Set-Cookie': 'session=; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=0',
					},
				});
			}

			// API proxy routes - /v1/* proxies to Copilot with auto-refresh
			if (path.startsWith('/v1/')) {
				return handleApiProxy(request, path, env);
			}

			// Legacy direct token endpoint
			if (path === '/api/token') {
				return apiGetToken(request, env);
			}

			return jsonResponse({ error: 'Not found' }, 404);

		} catch (error) {
			console.error('Error:', error);
			return renderErrorPage(String(error));
		}
	},
};

/**
 * Start the GitHub Device Flow
 */
async function startDeviceFlow(env: Env): Promise<Response> {
	// Request device code from GitHub
	const response = await fetch(GITHUB_DEVICE_CODE_URL, {
		method: 'POST',
		headers: {
			'Accept': 'application/json',
			'Content-Type': 'application/json',
		},
		body: JSON.stringify({
			client_id: GITHUB_CLIENT_ID,
			scope: OAUTH_SCOPES,
		}),
	});

	const data = await response.json() as {
		device_code: string;
		user_code: string;
		verification_uri: string;
		expires_in: number;
		interval: number;
		error?: string;
	};

	if (data.error) {
		return renderErrorPage(`GitHub error: ${data.error}`);
	}

	// Store the device flow state
	const flowId = generateFlowId();
	const state: DeviceFlowState = {
		device_code: data.device_code,
		user_code: data.user_code,
		verification_uri: data.verification_uri,
		expires_at: Date.now() + (data.expires_in * 1000),
		interval: data.interval,
	};

	await env.DEVICE_FLOWS.put(flowId, JSON.stringify(state), {
		expirationTtl: data.expires_in,
	});

	// Redirect to callback page with flow ID
	return Response.redirect(`/callback?flow=${flowId}`, 302);
}

/**
 * Render the device flow page where user enters the code
 */
async function renderDeviceFlowPage(url: URL, env: Env): Promise<Response> {
	const flowId = url.searchParams.get('flow');
	if (!flowId) {
		return renderErrorPage('Missing flow ID');
	}

	const stateData = await env.DEVICE_FLOWS.get(flowId);
	if (!stateData) {
		return renderErrorPage('Device flow expired or not found. Please try again.');
	}

	const state: DeviceFlowState = JSON.parse(stateData);

	const html = `<!DOCTYPE html>
<html lang="en">
<head>
	<meta charset="UTF-8">
	<meta name="viewport" content="width=device-width, initial-scale=1.0">
	<title>Sign in - Copilot API</title>
	<style>
		* { box-sizing: border-box; }
		body {
			font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
			max-width: 500px;
			margin: 0 auto;
			padding: 2rem;
			background: #0d1117;
			color: #c9d1d9;
			min-height: 100vh;
			display: flex;
			flex-direction: column;
			justify-content: center;
		}
		h1 { color: #58a6ff; text-align: center; }
		.card {
			background: #161b22;
			border: 1px solid #30363d;
			border-radius: 12px;
			padding: 2rem;
			text-align: center;
		}
		.code {
			font-family: monospace;
			font-size: 2.5rem;
			font-weight: bold;
			letter-spacing: 0.3em;
			color: #58a6ff;
			background: #0d1117;
			padding: 1rem 2rem;
			border-radius: 8px;
			margin: 1.5rem 0;
			border: 2px dashed #30363d;
		}
		.btn {
			display: inline-block;
			background: #238636;
			color: white;
			padding: 14px 28px;
			border-radius: 6px;
			text-decoration: none;
			font-weight: 600;
			font-size: 1.1rem;
			margin-top: 1rem;
			border: none;
			cursor: pointer;
		}
		.btn:hover { background: #2ea043; }
		.status {
			margin-top: 2rem;
			padding: 1rem;
			border-radius: 6px;
			display: none;
		}
		.status.waiting {
			display: block;
			background: #1f2937;
			color: #9ca3af;
		}
		.status.success {
			display: block;
			background: #064e3b;
			color: #6ee7b7;
		}
		.status.error {
			display: block;
			background: #3d1a1a;
			color: #f87171;
		}
		.spinner {
			display: inline-block;
			width: 16px;
			height: 16px;
			border: 2px solid #9ca3af;
			border-radius: 50%;
			border-top-color: transparent;
			animation: spin 1s linear infinite;
			margin-right: 8px;
			vertical-align: middle;
		}
		@keyframes spin {
			to { transform: rotate(360deg); }
		}
		.step {
			color: #8b949e;
			margin: 0.5rem 0;
		}
		.step strong { color: #c9d1d9; }
	</style>
</head>
<body>
	<div class="card">
		<h1>🔐 Sign in to GitHub</h1>
		
		<p class="step"><strong>Step 1:</strong> Copy this code:</p>
		<div class="code" id="user-code">${state.user_code}</div>
		
		<p class="step"><strong>Step 2:</strong> Click below and paste the code:</p>
		<a href="${state.verification_uri}" target="_blank" class="btn" id="open-github">
			Open GitHub →
		</a>
		
		<div class="status waiting" id="status">
			<span class="spinner"></span>
			Waiting for you to authorize on GitHub...
		</div>
	</div>

	<script>
		const flowId = '${flowId}';
		const interval = ${state.interval * 1000};
		let polling = false;

		document.getElementById('open-github').addEventListener('click', () => {
			document.getElementById('status').classList.add('waiting');
			document.getElementById('status').style.display = 'block';
			if (!polling) startPolling();
		});

		async function startPolling() {
			polling = true;
			while (polling) {
				await new Promise(r => setTimeout(r, interval));
				try {
					const res = await fetch('/poll', {
						method: 'POST',
						headers: { 'Content-Type': 'application/json' },
						body: JSON.stringify({ flow_id: flowId })
					});
					const data = await res.json();
					
					if (data.status === 'complete') {
						document.getElementById('status').className = 'status success';
						document.getElementById('status').innerHTML = '✅ Success! Redirecting...';
						window.location.href = '/dashboard';
						return;
					} else if (data.status === 'error') {
						document.getElementById('status').className = 'status error';
						document.getElementById('status').textContent = '❌ ' + data.error;
						polling = false;
						return;
					}
					// authorization_pending - keep polling
				} catch (e) {
					console.error('Poll error:', e);
				}
			}
		}

		// Start polling immediately
		startPolling();
	</script>
</body>
</html>`;

	return new Response(html, { headers: { 'Content-Type': 'text/html' } });
}

/**
 * Poll for device flow completion
 */
async function pollDeviceFlow(request: Request, env: Env): Promise<Response> {
	const body = await request.json() as { flow_id: string };
	const flowId = body.flow_id;

	if (!flowId) {
		return jsonResponse({ status: 'error', error: 'Missing flow_id' }, 400);
	}

	const stateData = await env.DEVICE_FLOWS.get(flowId);
	if (!stateData) {
		return jsonResponse({ status: 'error', error: 'Flow expired' }, 400);
	}

	const state: DeviceFlowState = JSON.parse(stateData);

	// Poll GitHub for the token
	const response = await fetch(GITHUB_TOKEN_URL, {
		method: 'POST',
		headers: {
			'Accept': 'application/json',
			'Content-Type': 'application/json',
		},
		body: JSON.stringify({
			client_id: GITHUB_CLIENT_ID,
			device_code: state.device_code,
			grant_type: 'urn:ietf:params:oauth:grant-type:device_code',
		}),
	});

	const tokenData = await response.json() as {
		access_token?: string;
		token_type?: string;
		scope?: string;
		error?: string;
		error_description?: string;
	};

	if (tokenData.error === 'authorization_pending') {
		return jsonResponse({ status: 'pending' });
	}

	if (tokenData.error === 'slow_down') {
		return jsonResponse({ status: 'pending', slow_down: true });
	}

	if (tokenData.error === 'expired_token') {
		await env.DEVICE_FLOWS.delete(flowId);
		return jsonResponse({ status: 'error', error: 'Code expired. Please try again.' });
	}

	if (tokenData.error || !tokenData.access_token) {
		return jsonResponse({ status: 'error', error: tokenData.error_description || tokenData.error || 'Unknown error' });
	}

	// Success! We have the GitHub token
	const githubToken = tokenData.access_token;

	// Get user info
	const userResponse = await fetch(`${GITHUB_API_URL}/user`, {
		headers: {
			'Authorization': `Bearer ${githubToken}`,
			'Accept': 'application/json',
			'User-Agent': 'Copilot-Token-App',
		},
	});
	const userData = await userResponse.json() as { login?: string };

	// Get initial Copilot token to verify access
	const copilotData = await fetchCopilotToken(githubToken);

	if (!copilotData.token) {
		return jsonResponse({ 
			status: 'error', 
			error: 'No Copilot access. Make sure you have an active Copilot subscription.' 
		});
	}

	// Create session
	const sessionId = generateSessionId();
	const session: StoredSession = {
		github_token: githubToken,
		github_user: userData.login || 'unknown',
		copilot_token: copilotData.token,
		copilot_expires_at: copilotData.expires_at,
		created_at: Date.now(),
		last_used: Date.now(),
	};

	// Store session in KV (expires in 30 days)
	await env.SESSIONS.put(sessionId, JSON.stringify(session), {
		expirationTtl: 30 * 24 * 60 * 60,
	});

	// Clean up the device flow
	await env.DEVICE_FLOWS.delete(flowId);

	// Return success with session cookie
	return new Response(JSON.stringify({ status: 'complete' }), {
		headers: {
			'Content-Type': 'application/json',
			'Set-Cookie': `session=${sessionId}; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=${30 * 24 * 60 * 60}`,
		},
	});
}

/**
 * Get session ID from cookie or Authorization header
 */
function getSessionId(request: Request): string | null {
	// Check Authorization header first (for API usage)
	const authHeader = request.headers.get('Authorization');
	if (authHeader?.startsWith('Bearer sk-copilot-')) {
		return authHeader.replace('Bearer ', '');
	}

	// Check cookie
	const cookies = request.headers.get('Cookie') || '';
	const match = cookies.match(/session=([^;]+)/);
	return match ? match[1] : null;
}

/**
 * Generate a flow ID for device flow state
 */
function generateFlowId(): string {
	const bytes = new Uint8Array(16);
	crypto.getRandomValues(bytes);
	return Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
}

/**
 * Generate a session ID that looks like an API key
 */
function generateSessionId(): string {
	const bytes = new Uint8Array(24);
	crypto.getRandomValues(bytes);
	const base64 = btoa(String.fromCharCode(...bytes))
		.replace(/\+/g, '-')
		.replace(/\//g, '_')
		.replace(/=/g, '');
	return `sk-copilot-${base64}`;
}

/**
 * Fetch Copilot token using GitHub token
 */
async function fetchCopilotToken(githubToken: string): Promise<CopilotTokenResponse> {
	const response = await fetch(COPILOT_TOKEN_URL, {
		headers: {
			'Authorization': `token ${githubToken}`,
			'X-GitHub-Api-Version': '2025-04-01',
		},
	});

	if (!response.ok) {
		return { error: `HTTP ${response.status}` };
	}

	return await response.json() as CopilotTokenResponse;
}

/**
 * Get valid Copilot token, refreshing if needed
 */
async function getValidCopilotToken(session: StoredSession, sessionId: string, env: Env): Promise<string | null> {
	const now = Math.floor(Date.now() / 1000);

	// Check if current token is still valid
	if (session.copilot_token && session.copilot_expires_at && session.copilot_expires_at > now + TOKEN_REFRESH_BUFFER) {
		return session.copilot_token;
	}

	// Refresh token
	const copilotData = await fetchCopilotToken(session.github_token);
	
	if (!copilotData.token) {
		return null;
	}

	// Update session
	session.copilot_token = copilotData.token;
	session.copilot_expires_at = copilotData.expires_at;
	session.last_used = Date.now();

	// Save updated session
	await env.SESSIONS.put(sessionId, JSON.stringify(session), {
		expirationTtl: 30 * 24 * 60 * 60,
	});

	return copilotData.token;
}

/**
 * Handle API proxy requests - /v1/* → Copilot API with auto-refresh
 */
async function handleApiProxy(request: Request, path: string, env: Env): Promise<Response> {
	const sessionId = getSessionId(request);
	
	if (!sessionId) {
		return jsonResponse({ error: 'Missing API key. Use Authorization: Bearer sk-copilot-xxx' }, 401);
	}

	// Get session
	const sessionData = await env.SESSIONS.get(sessionId);
	if (!sessionData) {
		return jsonResponse({ error: 'Invalid or expired API key. Please re-authenticate.' }, 401);
	}

	const session: StoredSession = JSON.parse(sessionData);

	// Get valid Copilot token (auto-refresh if needed)
	const copilotToken = await getValidCopilotToken(session, sessionId, env);
	if (!copilotToken) {
		return jsonResponse({ error: 'Failed to refresh Copilot token. GitHub token may have been revoked.' }, 401);
	}

	// Map path: /v1/chat/completions → /chat/completions
	const copilotPath = path.replace(/^\/v1/, '');
	const copilotUrl = `${COPILOT_API_URL}${copilotPath}`;

	// Proxy the request
	const headers = new Headers(request.headers);
	headers.set('Authorization', `Bearer ${copilotToken}`);
	headers.set('X-GitHub-Api-Version', '2025-05-01');
	headers.delete('Host');

	const proxyRequest = new Request(copilotUrl, {
		method: request.method,
		headers,
		body: request.body,
	});

	const response = await fetch(proxyRequest);

	// Return response with CORS headers
	const responseHeaders = new Headers(response.headers);
	Object.entries(corsHeaders()).forEach(([k, v]) => responseHeaders.set(k, v));

	return new Response(response.body, {
		status: response.status,
		headers: responseHeaders,
	});
}

/**
 * API endpoint to get token programmatically
 */
async function apiGetToken(request: Request, env: Env): Promise<Response> {
	const sessionId = getSessionId(request);
	
	if (!sessionId) {
		return jsonResponse({ error: 'Provide API key via Authorization: Bearer sk-copilot-xxx' }, 401);
	}

	const sessionData = await env.SESSIONS.get(sessionId);
	if (!sessionData) {
		return jsonResponse({ error: 'Invalid or expired API key' }, 401);
	}

	const session: StoredSession = JSON.parse(sessionData);
	const copilotToken = await getValidCopilotToken(session, sessionId, env);

	if (!copilotToken) {
		return jsonResponse({ error: 'Failed to get Copilot token' }, 500);
	}

	return jsonResponse({
		token: copilotToken,
		expires_at: session.copilot_expires_at,
		user: session.github_user,
	});
}

interface CopilotTokenResponse {
	token?: string;
	expires_at?: number;
	refresh_in?: number;
	sku?: string;
	chat_enabled?: boolean;
	error?: string;
}

function corsHeaders(): Record<string, string> {
	return {
		'Access-Control-Allow-Origin': '*',
		'Access-Control-Allow-Methods': 'GET, POST, PUT, DELETE, OPTIONS',
		'Access-Control-Allow-Headers': 'Content-Type, Authorization',
	};
}

function jsonResponse(data: unknown, status = 200): Response {
	return new Response(JSON.stringify(data, null, 2), {
		status,
		headers: { 
			'Content-Type': 'application/json',
			...corsHeaders(),
		},
	});
}

// ============ HTML Pages ============

function renderHomePage(): Response {
	const html = `<!DOCTYPE html>
<html lang="en">
<head>
	<meta charset="UTF-8">
	<meta name="viewport" content="width=device-width, initial-scale=1.0">
	<title>Copilot API Gateway</title>
	<style>
		* { box-sizing: border-box; }
		body {
			font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
			max-width: 700px;
			margin: 0 auto;
			padding: 2rem;
			background: #0d1117;
			color: #c9d1d9;
			min-height: 100vh;
		}
		h1 { color: #58a6ff; margin-bottom: 0.5rem; }
		.subtitle { color: #8b949e; margin-bottom: 2rem; }
		.btn {
			display: inline-block;
			background: #238636;
			color: white;
			padding: 14px 28px;
			border-radius: 6px;
			text-decoration: none;
			font-weight: 600;
			font-size: 1.1rem;
			margin-top: 1rem;
		}
		.btn:hover { background: #2ea043; }
		.card {
			background: #161b22;
			border: 1px solid #30363d;
			border-radius: 8px;
			padding: 1.5rem;
			margin: 1.5rem 0;
		}
		.card h3 { color: #58a6ff; margin-top: 0; }
		code {
			background: #1f2428;
			padding: 2px 6px;
			border-radius: 3px;
			font-size: 0.9em;
		}
		.feature-grid {
			display: grid;
			grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
			gap: 1rem;
			margin: 1.5rem 0;
		}
		.feature {
			background: #161b22;
			border: 1px solid #30363d;
			border-radius: 6px;
			padding: 1rem;
		}
		.feature h4 { color: #58a6ff; margin: 0 0 0.5rem 0; }
		.feature p { margin: 0; color: #8b949e; font-size: 0.9rem; }
		.badge {
			display: inline-block;
			background: #238636;
			color: white;
			padding: 4px 8px;
			border-radius: 4px;
			font-size: 0.75rem;
			margin-left: 0.5rem;
		}
	</style>
</head>
<body>
	<h1>🤖 Copilot API Gateway</h1>
	<p class="subtitle">OpenAI-compatible API powered by GitHub Copilot <span class="badge">No OAuth App needed</span></p>

	<a href="/login" class="btn">Sign in with GitHub →</a>

	<div class="card">
		<h3>How it works</h3>
		<ol>
			<li><strong>Sign in</strong> with your GitHub account (via device code)</li>
			<li><strong>Get an API key</strong> (looks like <code>sk-copilot-xxx</code>)</li>
			<li><strong>Use it</strong> with any OpenAI-compatible client</li>
		</ol>
		<p style="color: #8b949e; margin-top: 1rem;">
			Your API key never expires. Token refresh is handled automatically.
		</p>
	</div>

	<div class="feature-grid">
		<div class="feature">
			<h4>🔄 Auto-Refresh</h4>
			<p>Copilot tokens refresh automatically. Your API key stays valid.</p>
		</div>
		<div class="feature">
			<h4>🔌 OpenAI Compatible</h4>
			<p>Works with any OpenAI SDK, LangChain, etc.</p>
		</div>
		<div class="feature">
			<h4>🚀 GPT-4, Claude & more</h4>
			<p>Access all models available in Copilot.</p>
		</div>
		<div class="feature">
			<h4>🔒 No App Required</h4>
			<p>Uses GitHub device flow. No OAuth app needed.</p>
		</div>
	</div>

	<div class="card">
		<h3>Requirements</h3>
		<p>You need an active <a href="https://github.com/features/copilot" style="color: #58a6ff;">GitHub Copilot</a> subscription.</p>
	</div>
</body>
</html>`;

	return new Response(html, { headers: { 'Content-Type': 'text/html' } });
}

async function renderDashboard(sessionId: string, env: Env): Promise<Response> {
	const sessionData = await env.SESSIONS.get(sessionId);
	if (!sessionData) {
		return Response.redirect('/login', 302);
	}

	const session: StoredSession = JSON.parse(sessionData);

	const html = `<!DOCTYPE html>
<html lang="en">
<head>
	<meta charset="UTF-8">
	<meta name="viewport" content="width=device-width, initial-scale=1.0">
	<title>Dashboard - Copilot API</title>
	<style>
		* { box-sizing: border-box; }
		body {
			font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
			max-width: 900px;
			margin: 0 auto;
			padding: 2rem;
			background: #0d1117;
			color: #c9d1d9;
			min-height: 100vh;
		}
		h1 { color: #58a6ff; }
		h2 { color: #c9d1d9; font-size: 1.1rem; margin-top: 2rem; border-bottom: 1px solid #30363d; padding-bottom: 0.5rem; }
		.header {
			display: flex;
			justify-content: space-between;
			align-items: center;
			margin-bottom: 2rem;
		}
		.user-info {
			display: flex;
			align-items: center;
			gap: 1rem;
		}
		.user-info span { color: #8b949e; }
		.btn {
			background: #238636;
			color: white;
			padding: 8px 16px;
			border: none;
			border-radius: 6px;
			cursor: pointer;
			font-weight: 600;
			text-decoration: none;
		}
		.btn:hover { background: #2ea043; }
		.btn-secondary {
			background: #21262d;
			border: 1px solid #30363d;
		}
		.btn-secondary:hover { background: #30363d; }
		.api-key-box {
			background: #161b22;
			border: 1px solid #30363d;
			border-radius: 8px;
			padding: 1.5rem;
			margin: 1rem 0;
		}
		.api-key {
			font-family: monospace;
			font-size: 1.1rem;
			background: #0d1117;
			padding: 1rem;
			border-radius: 6px;
			word-break: break-all;
			margin: 1rem 0;
			border: 1px solid #30363d;
		}
		.success { color: #3fb950; }
		.endpoint-box {
			background: #161b22;
			border: 1px solid #30363d;
			border-radius: 6px;
			padding: 1rem;
			margin: 0.5rem 0;
		}
		.endpoint-url {
			font-family: monospace;
			color: #58a6ff;
		}
		pre {
			background: #161b22;
			border: 1px solid #30363d;
			border-radius: 6px;
			padding: 1rem;
			overflow-x: auto;
			font-size: 0.85rem;
			margin: 0.5rem 0;
		}
		code {
			background: #1f2428;
			padding: 2px 6px;
			border-radius: 3px;
			font-size: 0.9em;
		}
		.tabs {
			display: flex;
			gap: 0.5rem;
			margin-bottom: 1rem;
		}
		.tab {
			padding: 8px 16px;
			background: #21262d;
			border: 1px solid #30363d;
			border-radius: 6px;
			cursor: pointer;
			color: #c9d1d9;
		}
		.tab.active {
			background: #238636;
			border-color: #238636;
		}
		.tab-content { display: none; }
		.tab-content.active { display: block; }
		.copy-btn {
			background: #21262d;
			border: 1px solid #30363d;
			color: #c9d1d9;
			padding: 4px 12px;
			border-radius: 4px;
			cursor: pointer;
			font-size: 0.85rem;
		}
		.copy-btn:hover { background: #30363d; }
	</style>
</head>
<body>
	<div class="header">
		<h1>🤖 Copilot API</h1>
		<div class="user-info">
			<span>Signed in as <strong>${session.github_user}</strong></span>
			<a href="/logout" class="btn btn-secondary">Sign out</a>
		</div>
	</div>

	<p class="success">✅ Your API is ready to use!</p>

	<h2>🔑 Your API Key</h2>
	<div class="api-key-box">
		<p>Use this key with any OpenAI-compatible client. It never expires.</p>
		<div class="api-key" id="api-key">${sessionId}</div>
		<button class="btn" onclick="copyToClipboard('api-key')">📋 Copy API Key</button>
	</div>

	<h2>🌐 API Endpoints</h2>
	<div class="endpoint-box">
		<strong>Base URL:</strong>
		<span class="endpoint-url" id="base-url"></span>
		<button class="copy-btn" onclick="copyToClipboard('base-url')">Copy</button>
	</div>
	<p style="color: #8b949e;">Available endpoints:</p>
	<ul>
		<li><code>/v1/chat/completions</code> - Chat completions</li>
		<li><code>/v1/models</code> - List models</li>
		<li><code>/v1/completions</code> - Code completions</li>
		<li><code>/v1/embeddings</code> - Embeddings</li>
	</ul>

	<h2>📖 Usage Examples</h2>
	
	<div class="tabs">
		<div class="tab active" onclick="showTab('python')">Python</div>
		<div class="tab" onclick="showTab('node')">Node.js</div>
		<div class="tab" onclick="showTab('curl')">curl</div>
	</div>

	<div id="python" class="tab-content active">
		<pre>from openai import OpenAI

client = OpenAI(
    api_key="${sessionId}",
    base_url="<span class="base-url-placeholder"></span>/v1"
)

response = client.chat.completions.create(
    model="gpt-4o",  # or "claude-3.5-sonnet"
    messages=[{"role": "user", "content": "Hello!"}]
)
print(response.choices[0].message.content)</pre>
	</div>

	<div id="node" class="tab-content">
		<pre>import OpenAI from 'openai';

const client = new OpenAI({
    apiKey: '${sessionId}',
    baseURL: '<span class="base-url-placeholder"></span>/v1'
});

const response = await client.chat.completions.create({
    model: 'gpt-4o',
    messages: [{ role: 'user', content: 'Hello!' }]
});
console.log(response.choices[0].message.content);</pre>
	</div>

	<div id="curl" class="tab-content">
		<pre>curl <span class="base-url-placeholder"></span>/v1/chat/completions \\
  -H "Authorization: Bearer ${sessionId}" \\
  -H "Content-Type: application/json" \\
  -d '{
    "model": "gpt-4o",
    "messages": [{"role": "user", "content": "Hello!"}]
  }'</pre>
	</div>

	<h2>📋 Available Models</h2>
	<pre>curl <span class="base-url-placeholder"></span>/v1/models \\
  -H "Authorization: Bearer ${sessionId}"</pre>

	<script>
		const baseUrl = window.location.origin;
		document.getElementById('base-url').textContent = baseUrl;
		document.querySelectorAll('.base-url-placeholder').forEach(el => {
			el.textContent = baseUrl;
		});

		function copyToClipboard(id) {
			const text = document.getElementById(id).textContent;
			navigator.clipboard.writeText(text).then(() => {
				alert('Copied to clipboard!');
			});
		}

		function showTab(name) {
			document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
			document.querySelectorAll('.tab-content').forEach(t => t.classList.remove('active'));
			document.querySelector(\`.tab-content#\${name}\`).classList.add('active');
			event.target.classList.add('active');
		}
	</script>
</body>
</html>`;

	return new Response(html, { headers: { 'Content-Type': 'text/html' } });
}

function renderErrorPage(message: string): Response {
	const html = `<!DOCTYPE html>
<html lang="en">
<head>
	<meta charset="UTF-8">
	<meta name="viewport" content="width=device-width, initial-scale=1.0">
	<title>Error</title>
	<style>
		body {
			font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
			max-width: 600px;
			margin: 0 auto;
			padding: 2rem;
			background: #0d1117;
			color: #c9d1d9;
		}
		h1 { color: #f85149; }
		.error {
			background: #3d1a1a;
			border: 1px solid #f85149;
			border-radius: 6px;
			padding: 1rem;
			word-break: break-word;
		}
		.btn {
			display: inline-block;
			background: #21262d;
			color: white;
			padding: 12px 24px;
			border-radius: 6px;
			text-decoration: none;
			margin-top: 1rem;
		}
		.btn:hover { background: #30363d; }
	</style>
</head>
<body>
	<h1>❌ Error</h1>
	<div class="error">${escapeHtml(message)}</div>
	<a href="/" class="btn">← Back to Home</a>
</body>
</html>`;

	return new Response(html, {
		status: 400,
		headers: { 'Content-Type': 'text/html' },
	});
}

function escapeHtml(text: string): string {
	return text
		.replace(/&/g, '&amp;')
		.replace(/</g, '&lt;')
		.replace(/>/g, '&gt;')
		.replace(/"/g, '&quot;');
}
