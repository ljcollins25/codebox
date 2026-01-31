/**
 * /copilot/v1/* - OpenAI-Compatible Copilot Proxy
 * 
 * Accepts GitHub OAuth token in Authorization header, resolves to Copilot token
 * (cached), injects VS Code emulation headers, and forwards to Copilot API.
 * 
 * For codex models, automatically translates chat completions <-> /responses API.
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

// =============================================================================
// Types
// =============================================================================

interface CopilotTokenCache {
	copilot_token: string;
	expires_at: string;
}

interface ChatMessage {
	role: 'system' | 'user' | 'assistant';
	content: string;
}

interface ChatCompletionsRequest {
	model: string;
	messages: ChatMessage[];
	stream?: boolean;
	temperature?: number;
	max_tokens?: number;
	top_p?: number;
	frequency_penalty?: number;
	presence_penalty?: number;
}

interface ResponsesRequest {
	model: string;
	input: string;
	stream?: boolean;
	temperature?: number;
	max_output_tokens?: number;
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

// =============================================================================
// Chat Completions <-> Responses Translation
// =============================================================================

/**
 * Check if model uses /responses endpoint (codex models)
 */
function isCodexModel(model: string): boolean {
	return /codex/i.test(model);
}

/**
 * Convert chat messages to a single input string for /responses API
 */
function messagesToInput(messages: ChatMessage[]): string {
	return messages.map(msg => `${msg.role}: ${msg.content}`).join('\n');
}

/**
 * Transform chat completions request to /responses request
 */
function transformToResponsesRequest(chatReq: ChatCompletionsRequest): ResponsesRequest {
	const responsesReq: ResponsesRequest = {
		model: chatReq.model,
		input: messagesToInput(chatReq.messages),
		stream: chatReq.stream,
	};
	
	if (chatReq.temperature !== undefined) {
		responsesReq.temperature = chatReq.temperature;
	}
	if (chatReq.max_tokens !== undefined) {
		responsesReq.max_output_tokens = chatReq.max_tokens;
	}
	
	return responsesReq;
}

/**
 * Transform /responses SSE stream to chat completions SSE stream
 */
function transformResponsesStreamToChatCompletions(
	responsesStream: ReadableStream<Uint8Array>,
	model: string
): ReadableStream<Uint8Array> {
	const encoder = new TextEncoder();
	const decoder = new TextDecoder();
	
	// Generate a unique ID for this completion
	const completionId = `chatcmpl-${Date.now().toString(36)}${Math.random().toString(36).slice(2, 8)}`;
	const created = Math.floor(Date.now() / 1000);
	
	let buffer = '';
	
	return new ReadableStream({
		async start(controller) {
			const reader = responsesStream.getReader();
			
			try {
				while (true) {
					const { done, value } = await reader.read();
					if (done) break;
					
					buffer += decoder.decode(value, { stream: true });
					const lines = buffer.split('\n');
					buffer = lines.pop() || '';
					
					let currentEvent = '';
					
					for (const line of lines) {
						if (line.startsWith('event: ')) {
							currentEvent = line.slice(7).trim();
							continue;
						}
						
						if (!line.startsWith('data: ')) continue;
						const data = line.slice(6);
						if (data === '[DONE]') {
							controller.enqueue(encoder.encode('data: [DONE]\n\n'));
							continue;
						}
						
						try {
							const json = JSON.parse(data);
							
							// Handle response.output_text.delta events
							if (currentEvent === 'response.output_text.delta' && json.delta) {
								const chatChunk = {
									id: completionId,
									object: 'chat.completion.chunk',
									created,
									model,
									choices: [{
										index: 0,
										delta: { content: json.delta },
										finish_reason: null,
									}],
								};
								controller.enqueue(encoder.encode(`data: ${JSON.stringify(chatChunk)}\n\n`));
							}
							// Handle response.completed event
							else if (currentEvent === 'response.completed' || json.type === 'response.completed') {
								const chatChunk = {
									id: completionId,
									object: 'chat.completion.chunk',
									created,
									model,
									choices: [{
										index: 0,
										delta: {},
										finish_reason: 'stop',
									}],
								};
								controller.enqueue(encoder.encode(`data: ${JSON.stringify(chatChunk)}\n\n`));
								controller.enqueue(encoder.encode('data: [DONE]\n\n'));
							}
						} catch {
							// Ignore parse errors
						}
					}
				}
			} finally {
				reader.releaseLock();
				controller.close();
			}
		}
	});
}

/**
 * Transform non-streaming /responses response to chat completions response
 */
function transformResponsesToChatCompletions(responsesData: Record<string, unknown>, model: string): Record<string, unknown> {
	const completionId = `chatcmpl-${Date.now().toString(36)}${Math.random().toString(36).slice(2, 8)}`;
	const created = Math.floor(Date.now() / 1000);
	
	// Extract output text from responses format
	let outputText = '';
	if (responsesData.output && Array.isArray(responsesData.output)) {
		for (const item of responsesData.output) {
			if ((item as Record<string, unknown>).type === 'message') {
				const content = (item as Record<string, unknown>).content;
				if (Array.isArray(content)) {
					for (const c of content) {
						if ((c as Record<string, unknown>).type === 'output_text') {
							outputText += (c as Record<string, unknown>).text || '';
						}
					}
				}
			}
		}
	}
	
	return {
		id: completionId,
		object: 'chat.completion',
		created,
		model,
		choices: [{
			index: 0,
			message: {
				role: 'assistant',
				content: outputText,
			},
			finish_reason: 'stop',
		}],
		usage: responsesData.usage || {
			prompt_tokens: 0,
			completion_tokens: 0,
			total_tokens: 0,
		},
	};
}

// =============================================================================
// Main Handler
// =============================================================================

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

	const headers: Record<string, string> = {
		...VSCODE_HEADERS,
		'Authorization': `Bearer ${copilotToken}`,
	};

	let body: string | null = null;
	let isStreaming = false;
	let useResponsesApi = false;
	let model = '';

	if (request.method === 'POST') {
		const bodyData = await request.json() as ChatCompletionsRequest;
		isStreaming = bodyData.stream === true;
		model = bodyData.model || '';
		
		// Check if this is a chat completions request with a codex model
		const isChatCompletions = path === '/copilot/v1/chat/completions';
		useResponsesApi = isChatCompletions && isCodexModel(model);
		
		if (useResponsesApi) {
			// Transform chat completions request to /responses format
			const responsesReq = transformToResponsesRequest(bodyData);
			body = JSON.stringify(responsesReq);
		} else {
			body = JSON.stringify(bodyData);
		}
	}

	// Determine target URL
	const copilotPath = useResponsesApi 
		? '/responses'
		: path.replace('/copilot/v1', '');
	const targetUrl = `${COPILOT_API_BASE}${copilotPath}`;

	const response = await fetch(targetUrl, {
		method: request.method,
		headers,
		body,
	});

	if (!response.ok) {
		const errorText = await response.text();
		return errorResponse(`Copilot API error: ${errorText}`, response.status, 'api_error');
	}

	// Handle streaming response
	if (isStreaming && response.body) {
		let responseBody = response.body;
		
		// Transform /responses stream to chat completions format
		if (useResponsesApi) {
			responseBody = transformResponsesStreamToChatCompletions(response.body, model);
		}
		
		return new Response(responseBody, {
			status: response.status,
			headers: {
				'Content-Type': 'text/event-stream',
				'Cache-Control': 'no-cache',
				...corsHeaders(),
			},
		});
	}

	// Handle non-streaming response
	const data = await response.json() as Record<string, unknown>;
	
	// Transform /responses response to chat completions format
	if (useResponsesApi) {
		const chatData = transformResponsesToChatCompletions(data, model);
		return jsonResponse(chatData, response.status);
	}

	return jsonResponse(data, response.status);
}
