/**
 * GitHub Copilot Proxy - Cloudflare Worker
 * 
 * A comprehensive Copilot API proxy that:
 * 1. Handles GitHub Device Flow authentication with streaming progress
 * 2. Caches GitHub and Copilot tokens in Workers KV
 * 3. Proxies chat requests to GitHub Copilot API
 * 4. Provides a web UI for testing (served as static asset)
 * 
 * Endpoints:
 *   GET  /              - Web UI for testing device flow and chat (static)
 *   POST /chat/completions - OpenAI-compatible chat proxy (auto device flow if no token)
 *   POST /completions   - Code completions proxy
 *   GET  /models        - List available models
 *   POST /start-auth    - Start device flow authentication
 *   POST /poll-auth     - Poll device flow status
 *   POST /chat          - Web UI chat endpoint (uses session)
 *   POST /token         - Exchange GitHub token for Copilot token
 *   GET  /health        - Health check
 */

export interface Env {
	// KV namespace for token caching
	TOKEN_CACHE: KVNamespace;
	// Optional: Store a default Copilot token
	COPILOT_TOKEN?: string;
}

// =============================================================================
// Constants
// =============================================================================

const GITHUB_CLIENT_ID = "01ab8ac9400c4e429b23";
const GITHUB_SCOPES = "read:user";
const GITHUB_TOKEN_LIFETIME_DAYS = 30;
const COPILOT_TOKEN_LIFETIME_MINUTES = 25;
const DATA_VERSION = "v1";

const COPILOT_API_BASE = 'https://api.githubcopilot.com';

// Headers required by Copilot API
const COPILOT_HEADERS: Record<string, string> = {
	'User-Agent': 'GitHubCopilotChat/1.0.0',
	'Editor-Version': 'vscode/1.104.1',
	'Editor-Plugin-Version': 'copilot-chat/0.36.0',
	'Openai-Organization': 'github-copilot',
	'Copilot-Integration-Id': 'vscode-chat',
	'Content-Type': 'application/json',
};

// =============================================================================
// CORS Headers
// =============================================================================

function corsHeaders(): Record<string, string> {
	return {
		'Access-Control-Allow-Origin': '*',
		'Access-Control-Allow-Methods': 'GET, POST, PUT, DELETE, OPTIONS',
		'Access-Control-Allow-Headers': 'Content-Type, Authorization, X-Request-Id',
		'Access-Control-Max-Age': '86400',
	};
}

function handleCORS(): Response {
	return new Response(null, { status: 204, headers: corsHeaders() });
}

function jsonResponse(data: unknown, status = 200): Response {
	return new Response(JSON.stringify(data, null, 2), {
		status,
		headers: { 'Content-Type': 'application/json', ...corsHeaders() },
	});
}

// =============================================================================
// Main Handler
// =============================================================================

export default {
	async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
		// Handle CORS preflight
		if (request.method === 'OPTIONS') {
			return handleCORS();
		}

		const url = new URL(request.url);
		const path = url.pathname;

		try {
			// GET requests (static assets serve / and /index.html automatically)
			if (request.method === 'GET') {
				if (path === '/health') {
					return jsonResponse({ status: 'ok', timestamp: new Date().toISOString() });
				}
				if (path === '/models') {
					return await handleModelsRequest(request, env);
				}
			}

			// POST endpoints for device flow (Web UI)
			if (request.method === 'POST' && path === '/start-auth') {
				return await handleStartAuth(request, env);
			}
			if (request.method === 'POST' && path === '/poll-auth') {
				return await handlePollAuth(request, env);
			}
			if (request.method === 'POST' && path === '/chat') {
				return await handleWebChat(request, env);
			}

			// Direct proxy endpoints (require Copilot token in Authorization header)
			if (request.method === 'POST' && path === '/chat/completions') {
				return await proxyChatCompletions(request, env);
			}
			if (request.method === 'POST' && path === '/completions') {
				return await proxyCompletions(request, env);
			}
			if (request.method === 'POST' && path === '/v1/messages') {
				return await proxyMessages(request, env);
			}
			if (request.method === 'POST' && path === '/token') {
				return await exchangeToken(request, env);
			}

			return jsonResponse({ 
				error: 'Not found', 
				endpoints: [
					'GET  /',
					'POST /chat/completions',
					'POST /completions',
					'GET  /models',
					'POST /start-auth',
					'POST /poll-auth',
					'POST /chat',
					'POST /token',
					'GET  /health'
				] 
			}, 404);

		} catch (error) {
			console.error('Worker error:', error);
			return jsonResponse({ error: 'Internal server error', message: String(error) }, 500);
		}
	},
};

// =============================================================================
// Types
// =============================================================================

interface DeviceFlowState {
	device_code: string;
	user_code: string;
	verification_uri: string;
	expires_at: string;
	interval: number;
}

interface Session {
	github_token: string;
	github_expires_at: string;
	copilot_token?: string;
	copilot_expires_at?: string;
}

interface DeviceFlowResponse {
	device_code: string;
	user_code: string;
	verification_uri: string;
	expires_in: number;
	interval: number;
}

interface PollResponse {
	access_token?: string;
	error?: string;
}

interface CopilotTokenResponse {
	token: string;
}

// =============================================================================
// Device Flow Handlers (Web UI)
// =============================================================================

async function handleStartAuth(request: Request, env: Env): Promise<Response> {
	try {
		const deviceFlow = await startDeviceFlow();
		
		// Generate a session ID for the web UI
		const sessionId = crypto.randomUUID();
		const pendingFlowKey = `webui_pending_${DATA_VERSION}_${sessionId}`;
		
		const pendingState: DeviceFlowState = {
			device_code: deviceFlow.device_code,
			user_code: deviceFlow.user_code,
			verification_uri: deviceFlow.verification_uri,
			expires_at: new Date(Date.now() + deviceFlow.expires_in * 1000).toISOString(),
			interval: deviceFlow.interval || 5,
		};
		
		await env.TOKEN_CACHE.put(pendingFlowKey, JSON.stringify(pendingState), {
			expirationTtl: deviceFlow.expires_in,
		});
		
		return jsonResponse({
			session_id: sessionId,
			user_code: deviceFlow.user_code,
			verification_uri: deviceFlow.verification_uri,
			expires_in: deviceFlow.expires_in,
			interval: deviceFlow.interval,
		});
		
	} catch (error) {
		return jsonResponse({ error: String(error) }, 500);
	}
}

async function handlePollAuth(request: Request, env: Env): Promise<Response> {
	const body = await request.json() as { session_id?: string };
	const sessionId = body.session_id;
	
	if (!sessionId) {
		return jsonResponse({ error: 'Missing session_id' }, 400);
	}
	
	const pendingFlowKey = `webui_pending_${DATA_VERSION}_${sessionId}`;
	const pendingFlow = await env.TOKEN_CACHE.get(pendingFlowKey, 'json') as DeviceFlowState | null;
	
	if (!pendingFlow) {
		return jsonResponse({ status: 'expired' });
	}
	
	const pollResult = await pollDeviceFlowOnce(pendingFlow.device_code);
	
	if (pollResult.access_token) {
		// Get Copilot token
		try {
			const copilotData = await getCopilotToken(pollResult.access_token);
			
			// Store session for web UI
			const sessionKey = `webui_session_${DATA_VERSION}_${sessionId}`;
			const session: Session = {
				github_token: pollResult.access_token,
				github_expires_at: new Date(Date.now() + GITHUB_TOKEN_LIFETIME_DAYS * 24 * 60 * 60 * 1000).toISOString(),
				copilot_token: copilotData.token,
				copilot_expires_at: new Date(Date.now() + COPILOT_TOKEN_LIFETIME_MINUTES * 60 * 1000).toISOString(),
			};
			
			await env.TOKEN_CACHE.put(sessionKey, JSON.stringify(session), {
				expirationTtl: GITHUB_TOKEN_LIFETIME_DAYS * 24 * 60 * 60,
			});
			await env.TOKEN_CACHE.delete(pendingFlowKey);
			
			return jsonResponse({ 
				status: 'complete',
				session_id: sessionId,
			});
			
		} catch (error) {
			const errorMsg = String(error);
			if (errorMsg.includes('NO_COPILOT_ACCESS')) {
				return jsonResponse({ status: 'error', error: 'Your GitHub account does not have Copilot access.' });
			}
			return jsonResponse({ status: 'error', error: errorMsg });
		}
	}
	
	if (pollResult.error === 'expired_token') {
		await env.TOKEN_CACHE.delete(pendingFlowKey);
		return jsonResponse({ status: 'expired' });
	}
	
	if (pollResult.error === 'authorization_pending' || pollResult.error === 'slow_down') {
		return jsonResponse({ status: 'pending' });
	}
	
	return jsonResponse({ status: 'error', error: pollResult.error });
}

async function handleWebChat(request: Request, env: Env): Promise<Response> {
	const body = await request.json() as { 
		session_id?: string; 
		messages?: Array<{ role: string; content: string }>; 
		model?: string;
	};
	const { session_id, messages, model = 'gpt-4o' } = body;
	
	if (!session_id) {
		return jsonResponse({ error: 'Missing session_id' }, 400);
	}
	
	if (!messages || messages.length === 0) {
		return jsonResponse({ error: 'Missing messages' }, 400);
	}
	
	const sessionKey = `webui_session_${DATA_VERSION}_${session_id}`;
	let session = await env.TOKEN_CACHE.get(sessionKey, 'json') as Session | null;
	
	if (!session?.copilot_token) {
		return jsonResponse({ error: 'Not authenticated. Please sign in first.' }, 401);
	}
	
	// Refresh token if expired
	if (isTokenExpired(session.copilot_expires_at)) {
		try {
			const copilotData = await getCopilotToken(session.github_token);
			session.copilot_token = copilotData.token;
			session.copilot_expires_at = new Date(Date.now() + COPILOT_TOKEN_LIFETIME_MINUTES * 60 * 1000).toISOString();
			await env.TOKEN_CACHE.put(sessionKey, JSON.stringify(session), {
				expirationTtl: GITHUB_TOKEN_LIFETIME_DAYS * 24 * 60 * 60,
			});
		} catch (error) {
			return jsonResponse({ error: 'Token refresh failed: ' + String(error) }, 401);
		}
	}
	
	const payload = {
		model,
		messages,
		stream: true,
	};
	
	const response = await fetch(`${COPILOT_API_BASE}/chat/completions`, {
		method: 'POST',
		headers: {
			...COPILOT_HEADERS,
			'Authorization': `Bearer ${session.copilot_token}`,
		},
		body: JSON.stringify(payload),
	});
	
	if (!response.ok) {
		const error = await response.text();
		return jsonResponse({ error: `Copilot API error: ${error}` }, response.status);
	}
	
	// Return streaming response
	return new Response(response.body, {
		headers: {
			'Content-Type': 'text/event-stream',
			'Cache-Control': 'no-cache',
			...corsHeaders(),
		},
	});
}

// =============================================================================
// Direct Proxy Handlers (OpenAI-compatible with auto device flow)
// =============================================================================

/**
 * Proxy chat completions to Copilot API.
 * If no token provided, initiates device flow with streaming SSE events.
 * Uses X-Session-Id header to track auth sessions across requests.
 */
async function proxyChatCompletions(request: Request, env: Env): Promise<Response> {
	const authHeader = request.headers.get('Authorization');
	const token = authHeader?.replace(/^Bearer\s+/i, '');
	const sessionId = request.headers.get('X-Session-Id');
	
	// If we have a token, proxy directly
	if (token) {
		return await doProxyChatCompletions(request, token, env);
	}
	
	// If we have a session ID, check for cached token
	if (sessionId) {
		const sessionKey = `openai_session_${DATA_VERSION}_${sessionId}`;
		const session = await env.TOKEN_CACHE.get(sessionKey, 'json') as Session | null;
		
		if (session?.copilot_token && !isTokenExpired(session.copilot_expires_at)) {
			return await doProxyChatCompletions(request, session.copilot_token, env);
		}
		
		// Try to refresh if github token is valid
		if (session?.github_token && !isTokenExpired(session.github_expires_at)) {
			try {
				const copilotData = await getCopilotToken(session.github_token);
				session.copilot_token = copilotData.token;
				session.copilot_expires_at = new Date(Date.now() + COPILOT_TOKEN_LIFETIME_MINUTES * 60 * 1000).toISOString();
				await env.TOKEN_CACHE.put(sessionKey, JSON.stringify(session), {
					expirationTtl: GITHUB_TOKEN_LIFETIME_DAYS * 24 * 60 * 60,
				});
				return await doProxyChatCompletions(request, session.copilot_token, env);
			} catch {
				// Fall through to device flow
			}
		}
		
		// Check for pending device flow
		const pendingFlowKey = `openai_pending_${DATA_VERSION}_${sessionId}`;
		const pendingFlow = await env.TOKEN_CACHE.get(pendingFlowKey, 'json') as DeviceFlowState | null;
		
		if (pendingFlow) {
			return await pollDeviceFlowStreaming(env, sessionId, pendingFlowKey, sessionKey, pendingFlow, request);
		}
	}
	
	// No token and no valid session - start device flow with streaming events
	return await startDeviceFlowStreaming(env, sessionId, request);
}

/**
 * Start device flow and stream SSE events in OpenAI format
 */
async function startDeviceFlowStreaming(env: Env, existingSessionId: string | null, request: Request): Promise<Response> {
	const body = await request.clone().json() as Record<string, unknown>;
	const isStreaming = body.stream === true;
	
	// Generate or use existing session ID
	const sessionId = existingSessionId || crypto.randomUUID();
	
	try {
		const deviceFlow = await startDeviceFlow();
		
		// Store pending flow
		const pendingFlowKey = `openai_pending_${DATA_VERSION}_${sessionId}`;
		const pendingState: DeviceFlowState = {
			device_code: deviceFlow.device_code,
			user_code: deviceFlow.user_code,
			verification_uri: deviceFlow.verification_uri,
			expires_at: new Date(Date.now() + deviceFlow.expires_in * 1000).toISOString(),
			interval: deviceFlow.interval || 5,
		};
		
		await env.TOKEN_CACHE.put(pendingFlowKey, JSON.stringify(pendingState), {
			expirationTtl: deviceFlow.expires_in,
		});
		
		if (isStreaming) {
			// Return streaming response with device flow info
			return streamingResponse((write) => {
				write(sseChunk(`🔐 GitHub Authorization Required\n\n`));
				write(sseChunk(`Visit: ${deviceFlow.verification_uri}\n`));
				write(sseChunk(`Enter code: ${deviceFlow.user_code}\n\n`));
				write(sseChunk(`Session ID: ${sessionId}\n`));
				write(sseChunk(`Include header "X-Session-Id: ${sessionId}" in your next request.\n`));
				write(sseDone());
			}, sessionId);
		} else {
			// Non-streaming: return JSON with device flow info
			return new Response(JSON.stringify({
				error: {
					message: 'Authorization required',
					type: 'auth_required',
					code: 'device_flow_required',
					device_flow: {
						session_id: sessionId,
						user_code: deviceFlow.user_code,
						verification_uri: deviceFlow.verification_uri,
						expires_in: deviceFlow.expires_in,
						interval: deviceFlow.interval,
					}
				}
			}, null, 2), {
				status: 401,
				headers: {
					'Content-Type': 'application/json',
					'X-Session-Id': sessionId,
					...corsHeaders(),
				},
			});
		}
	} catch (error) {
		return jsonResponse({ error: { message: `Failed to start device flow: ${error}`, type: 'server_error' } }, 500);
	}
}

/**
 * Poll device flow and stream SSE events in OpenAI format
 */
async function pollDeviceFlowStreaming(
	env: Env,
	sessionId: string,
	pendingFlowKey: string,
	sessionKey: string,
	pendingFlow: DeviceFlowState,
	request: Request
): Promise<Response> {
	const body = await request.clone().json() as Record<string, unknown>;
	const isStreaming = body.stream === true;
	
	// Poll up to 6 times (30 seconds with 5s interval)
	const maxPolls = 6;
	const pollInterval = Math.max(pendingFlow.interval, 5);
	
	if (isStreaming) {
		return streamingResponseAsync(async (write) => {
			write(sseChunk(`🔐 Checking authorization for code: ${pendingFlow.user_code}\n\n`));
			
			for (let i = 0; i < maxPolls; i++) {
				write(sseChunk(`⏳ Checking... (${i + 1}/${maxPolls})\n`));
				
				const pollResult = await pollDeviceFlowOnce(pendingFlow.device_code);
				
				if (pollResult.access_token) {
					write(sseChunk(`\n✅ Authorization successful!\n`));
					write(sseChunk(`🔄 Getting Copilot token...\n`));
					
					try {
						const copilotData = await getCopilotToken(pollResult.access_token);
						
						const session: Session = {
							github_token: pollResult.access_token,
							github_expires_at: new Date(Date.now() + GITHUB_TOKEN_LIFETIME_DAYS * 24 * 60 * 60 * 1000).toISOString(),
							copilot_token: copilotData.token,
							copilot_expires_at: new Date(Date.now() + COPILOT_TOKEN_LIFETIME_MINUTES * 60 * 1000).toISOString(),
						};
						
						await env.TOKEN_CACHE.put(sessionKey, JSON.stringify(session), {
							expirationTtl: GITHUB_TOKEN_LIFETIME_DAYS * 24 * 60 * 60,
						});
						await env.TOKEN_CACHE.delete(pendingFlowKey);
						
						write(sseChunk(`✅ Ready! Retry your request with header "X-Session-Id: ${sessionId}"\n`));
						
					} catch (error) {
						const errorMsg = String(error);
						if (errorMsg.includes('NO_COPILOT_ACCESS')) {
							write(sseChunk(`\n❌ Your GitHub account does not have Copilot access.\n`));
						} else {
							write(sseChunk(`\n❌ Failed to get Copilot token: ${errorMsg}\n`));
						}
					}
					
					write(sseDone());
					return;
				}
				
				if (pollResult.error === 'expired_token') {
					await env.TOKEN_CACHE.delete(pendingFlowKey);
					write(sseChunk(`\n❌ Device code expired. Please start a new request.\n`));
					write(sseDone());
					return;
				}
				
				if (pollResult.error && pollResult.error !== 'authorization_pending' && pollResult.error !== 'slow_down') {
					write(sseChunk(`\n❌ Error: ${pollResult.error}\n`));
					write(sseDone());
					return;
				}
				
				if (i < maxPolls - 1) {
					await sleep(pollInterval * 1000);
				}
			}
			
			write(sseChunk(`\n⏸️ Still waiting. Send another request to continue polling.\n`));
			write(sseChunk(`Include header "X-Session-Id: ${sessionId}"\n`));
			write(sseDone());
		}, sessionId);
	} else {
		// Non-streaming: single poll attempt
		const pollResult = await pollDeviceFlowOnce(pendingFlow.device_code);
		
		if (pollResult.access_token) {
			try {
				const copilotData = await getCopilotToken(pollResult.access_token);
				
				const session: Session = {
					github_token: pollResult.access_token,
					github_expires_at: new Date(Date.now() + GITHUB_TOKEN_LIFETIME_DAYS * 24 * 60 * 60 * 1000).toISOString(),
					copilot_token: copilotData.token,
					copilot_expires_at: new Date(Date.now() + COPILOT_TOKEN_LIFETIME_MINUTES * 60 * 1000).toISOString(),
				};
				
				await env.TOKEN_CACHE.put(sessionKey, JSON.stringify(session), {
					expirationTtl: GITHUB_TOKEN_LIFETIME_DAYS * 24 * 60 * 60,
				});
				await env.TOKEN_CACHE.delete(pendingFlowKey);
				
				return new Response(JSON.stringify({
					error: {
						message: 'Authorization complete. Retry your request.',
						type: 'auth_complete',
						code: 'retry_request',
						session_id: sessionId,
					}
				}, null, 2), {
					status: 401,
					headers: {
						'Content-Type': 'application/json',
						'X-Session-Id': sessionId,
						...corsHeaders(),
					},
				});
				
			} catch (error) {
				const errorMsg = String(error);
				return jsonResponse({
					error: {
						message: errorMsg.includes('NO_COPILOT_ACCESS') 
							? 'Your GitHub account does not have Copilot access'
							: `Failed to get Copilot token: ${errorMsg}`,
						type: 'auth_error',
					}
				}, 403);
			}
		}
		
		if (pollResult.error === 'expired_token') {
			await env.TOKEN_CACHE.delete(pendingFlowKey);
			return jsonResponse({
				error: {
					message: 'Device code expired. Start a new request.',
					type: 'auth_expired',
				}
			}, 401);
		}
		
		// Still pending
		return new Response(JSON.stringify({
			error: {
				message: 'Authorization pending',
				type: 'auth_pending',
				code: 'device_flow_pending',
				device_flow: {
					session_id: sessionId,
					user_code: pendingFlow.user_code,
					verification_uri: pendingFlow.verification_uri,
				}
			}
		}, null, 2), {
			status: 401,
			headers: {
				'Content-Type': 'application/json',
				'X-Session-Id': sessionId,
				...corsHeaders(),
			},
		});
	}
}

/**
 * Actually proxy the chat completions request
 */
async function doProxyChatCompletions(request: Request, token: string, env: Env): Promise<Response> {
	const body = await request.json() as Record<string, unknown>;
	const requestId = crypto.randomUUID();
	
	const response = await fetch(`${COPILOT_API_BASE}/chat/completions`, {
		method: 'POST',
		headers: {
			...COPILOT_HEADERS,
			'Authorization': `Bearer ${token}`,
			'X-Request-Id': requestId,
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
				...corsHeaders(),
			},
		});
	}
	
	const data = await response.json();
	return jsonResponse(data, response.status);
}

async function proxyCompletions(request: Request, env: Env): Promise<Response> {
	const authHeader = request.headers.get('Authorization');
	const token = authHeader?.replace(/^Bearer\s+/i, '') || env.COPILOT_TOKEN;
	
	if (!token) {
		return jsonResponse({ error: 'Missing Copilot token. Provide via Authorization: Bearer <token>' }, 401);
	}
	
	const body = await request.json();
	const requestId = crypto.randomUUID();
	
	const response = await fetch(`${COPILOT_API_BASE}/completions`, {
		method: 'POST',
		headers: {
			...COPILOT_HEADERS,
			'Authorization': `Bearer ${token}`,
			'X-Request-Id': requestId,
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
				...corsHeaders(),
			},
		});
	}
	
	const data = await response.json();
	return jsonResponse(data, response.status);
}

async function proxyMessages(request: Request, env: Env): Promise<Response> {
	const authHeader = request.headers.get('Authorization');
	const token = authHeader?.replace(/^Bearer\s+/i, '') || env.COPILOT_TOKEN;
	
	if (!token) {
		return jsonResponse({ error: 'Missing Copilot token. Provide via Authorization: Bearer <token>' }, 401);
	}
	
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
				...corsHeaders(),
			},
		});
	}
	
	const data = await response.json();
	return jsonResponse(data, response.status);
}

async function handleModelsRequest(request: Request, env: Env): Promise<Response> {
	const authHeader = request.headers.get('Authorization');
	const token = authHeader?.replace(/^Bearer\s+/i, '') || env.COPILOT_TOKEN;
	
	if (!token) {
		// Return a static list if no token
		return jsonResponse({
			data: [
				{ id: 'gpt-4o', object: 'model', created: Date.now() },
				{ id: 'gpt-4.1', object: 'model', created: Date.now() },
				{ id: 'gpt-4.1-mini', object: 'model', created: Date.now() },
				{ id: 'claude-sonnet-4', object: 'model', created: Date.now() },
				{ id: 'claude-sonnet-4.5', object: 'model', created: Date.now() },
				{ id: 'gemini-2.5-pro', object: 'model', created: Date.now() },
				{ id: 'o3-mini', object: 'model', created: Date.now() },
				{ id: 'o4-mini', object: 'model', created: Date.now() },
			]
		});
	}
	
	const response = await fetch(`${COPILOT_API_BASE}/models`, {
		headers: { 'Authorization': `Bearer ${token}` },
	});
	
	const data = await response.json();
	return jsonResponse(data, response.status);
}

async function exchangeToken(request: Request, env: Env): Promise<Response> {
	const body = await request.json() as { github_token?: string };
	
	if (!body.github_token) {
		return jsonResponse({ error: 'Missing github_token in request body' }, 400);
	}
	
	try {
		const copilotData = await getCopilotToken(body.github_token);
		return jsonResponse(copilotData);
	} catch (error) {
		const errorMsg = String(error);
		if (errorMsg.includes('INVALID_GITHUB_TOKEN')) {
			return jsonResponse({ error: 'Invalid GitHub token' }, 401);
		}
		if (errorMsg.includes('NO_COPILOT_ACCESS')) {
			return jsonResponse({ error: 'GitHub account does not have Copilot access' }, 403);
		}
		return jsonResponse({ error: errorMsg }, 500);
	}
}

// =============================================================================
// GitHub API Helpers
// =============================================================================

async function startDeviceFlow(): Promise<DeviceFlowResponse> {
	const response = await fetch('https://github.com/login/device/code', {
		method: 'POST',
		headers: {
			'Accept': 'application/json',
			'Content-Type': 'application/x-www-form-urlencoded',
		},
		body: `client_id=${GITHUB_CLIENT_ID}&scope=${GITHUB_SCOPES}`,
	});
	
	if (!response.ok) {
		throw new Error(`Failed to start device flow: ${response.status}`);
	}
	
	return response.json();
}

async function pollDeviceFlowOnce(deviceCode: string): Promise<PollResponse> {
	const response = await fetch('https://github.com/login/oauth/access_token', {
		method: 'POST',
		headers: {
			'Accept': 'application/json',
			'Content-Type': 'application/x-www-form-urlencoded',
		},
		body: `client_id=${GITHUB_CLIENT_ID}&device_code=${deviceCode}&grant_type=urn:ietf:params:oauth:grant-type:device_code`,
	});
	
	if (!response.ok) {
		return { error: `HTTP ${response.status}` };
	}
	
	return response.json();
}

async function getCopilotToken(githubToken: string): Promise<CopilotTokenResponse> {
	const response = await fetch('https://api.github.com/copilot_internal/v2/token', {
		method: 'GET',
		headers: {
			'Authorization': `token ${githubToken}`,
			'Accept': 'application/json',
			'Editor-Version': 'vscode/1.104.1',
			'Editor-Plugin-Version': 'copilot/1.0.0',
			'User-Agent': 'GitHubCopilotChat/1.0.0',
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
		throw new Error(`Failed to get Copilot token: ${response.status} - ${text}`);
	}
	
	return response.json();
}

// =============================================================================
// Utility Functions
// =============================================================================

function isTokenExpired(expiresAt?: string): boolean {
	if (!expiresAt) return true;
	return new Date(expiresAt) < new Date();
}

function sleep(ms: number): Promise<void> {
	return new Promise(resolve => setTimeout(resolve, ms));
}

// =============================================================================
// OpenAI SSE Helpers
// =============================================================================

/**
 * Create an SSE chunk in OpenAI format
 */
function sseChunk(content: string): string {
	const chunk = {
		id: `chatcmpl-${crypto.randomUUID()}`,
		object: 'chat.completion.chunk',
		created: Math.floor(Date.now() / 1000),
		model: 'system',
		choices: [{
			index: 0,
			delta: { content },
			finish_reason: null,
		}],
	};
	return `data: ${JSON.stringify(chunk)}\n\n`;
}

/**
 * Create the final SSE done message
 */
function sseDone(): string {
	return `data: [DONE]\n\n`;
}

/**
 * Create a streaming response (sync)
 */
function streamingResponse(fn: (write: (msg: string) => void) => void, sessionId?: string): Response {
	const encoder = new TextEncoder();
	const stream = new ReadableStream({
		start(controller) {
			const write = (msg: string) => controller.enqueue(encoder.encode(msg));
			fn(write);
			controller.close();
		}
	});
	
	const headers: Record<string, string> = {
		'Content-Type': 'text/event-stream',
		'Cache-Control': 'no-cache',
		...corsHeaders(),
	};
	if (sessionId) {
		headers['X-Session-Id'] = sessionId;
	}
	
	return new Response(stream, { headers });
}

/**
 * Create a streaming response (async)
 */
function streamingResponseAsync(fn: (write: (msg: string) => void) => Promise<void>, sessionId?: string): Response {
	const encoder = new TextEncoder();
	const stream = new ReadableStream({
		async start(controller) {
			const write = (msg: string) => controller.enqueue(encoder.encode(msg));
			try {
				await fn(write);
			} finally {
				controller.close();
			}
		}
	});
	
	const headers: Record<string, string> = {
		'Content-Type': 'text/event-stream',
		'Cache-Control': 'no-cache',
		...corsHeaders(),
	};
	if (sessionId) {
		headers['X-Session-Id'] = sessionId;
	}
	
	return new Response(stream, { headers });
}


