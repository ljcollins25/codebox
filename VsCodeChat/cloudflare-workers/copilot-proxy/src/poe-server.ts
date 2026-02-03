/**
 * /poe/server - Poe Server Bot Translation
 * /poe/settings - Poe Parameter Controls
 * 
 * Translates Poe Server Bot API requests/responses to/from OpenAI-compatible format.
 * See: https://creator.poe.com/docs/server-bots/quick-start
 * 
 * Monetization API:
 * See: https://creator.poe.com/docs/server-bots/poe-bot-monetization-api-documentation
 */

import { Env, corsHeaders, jsonResponse, errorResponse } from './shared';
import { handleCopilotProxy } from './copilot';

// =============================================================================
// Model Pricing Configuration
// =============================================================================

/**
 * Model pricing in points per 1k tokens.
 * USD rate: $0.03/1M tokens = 1 point/1k tokens
 * 
 * Fill in model-specific values here.
 */
interface ModelPricing {
	input_points_per_1k: number;   // Points per 1k input tokens
	output_points_per_1k: number;  // Points per 1k output tokens
	cache_discount?: number;       // Optional cache discount (0.0-1.0, e.g., 0.9 = 90% discount)
}

const MODEL_PRICING: Record<string, ModelPricing> = {
	// GPT-4 series
	'gpt-4.1': { input_points_per_1k: 67, output_points_per_1k: 267 },
	'gpt-4o': { input_points_per_1k: 83, output_points_per_1k: 333 },
	'gpt-4o-mini': { input_points_per_1k: 5, output_points_per_1k: 20 },
	
	// GPT-5 series (placeholder values - fill in actual rates)
	'gpt-5-mini': { input_points_per_1k: 10, output_points_per_1k: 40 },
	'gpt-5': { input_points_per_1k: 167, output_points_per_1k: 667 },
	'gpt-5-codex': { input_points_per_1k: 200, output_points_per_1k: 800 },
	'gpt-5.1': { input_points_per_1k: 200, output_points_per_1k: 800 },
	'gpt-5.1-codex': { input_points_per_1k: 250, output_points_per_1k: 1000 },
	'gpt-5.1-codex-max': { input_points_per_1k: 500, output_points_per_1k: 2000 },
	'gpt-5.1-codex-mini': { input_points_per_1k: 50, output_points_per_1k: 200 },
	'gpt-5.2': { input_points_per_1k: 250, output_points_per_1k: 1000 },
	'gpt-5.2-codex': { input_points_per_1k: 300, output_points_per_1k: 1200 },
	
	// Claude series
	'claude-haiku-4.5': { input_points_per_1k: 33, output_points_per_1k: 167 },
	'claude-opus-4.5': { input_points_per_1k: 50, output_points_per_1k: 250 },
	'claude-sonnet-4': { input_points_per_1k: 100, output_points_per_1k: 500 },
	'claude-sonnet-4.5': { input_points_per_1k: 100, output_points_per_1k: 500 },
	
	// Gemini series (placeholder values - fill in actual rates)
	'gemini-2.5-pro': { input_points_per_1k: 42, output_points_per_1k: 167 },
	'gemini-3-flash-preview': { input_points_per_1k: 25, output_points_per_1k: 100 },
	'gemini-3-pro-preview': { input_points_per_1k: 42, output_points_per_1k: 167 },
};

// Default pricing for unknown models
const DEFAULT_PRICING: ModelPricing = { input_points_per_1k: 10, output_points_per_1k: 40 };

/**
 * Get pricing for a model, with fallback to default
 */
function getModelPricing(model: string): ModelPricing {
	return MODEL_PRICING[model] || DEFAULT_PRICING;
}

/**
 * Convert points per 1k tokens to USD milli-cents per token
 * 1 point/1k tokens = $0.03/1M tokens = 0.03 cents/1k tokens = 0.00003 cents/token = 0.03 milli-cents/token
 * So: points_per_1k * 30 = milli_cents per 1k tokens
 */
function pointsToMilliCentsPerToken(pointsPer1k: number): number {
	// points_per_1k tokens -> milli-cents per token
	// 1 point = $0.03/1M tokens = 30 milli-cents/1M tokens = 0.00003 cents/token = 0.03 milli-cents/token
	// So points_per_1k * 0.03 = milli-cents per token
	return pointsPer1k * 0.03;
}

/**
 * Calculate cost in USD milli-cents from token counts and pricing
 */
function calculateCostMilliCents(inputTokens: number, outputTokens: number, pricing: ModelPricing): number {
	const inputCost = inputTokens * pointsToMilliCentsPerToken(pricing.input_points_per_1k);
	const outputCost = outputTokens * pointsToMilliCentsPerToken(pricing.output_points_per_1k);
	return Math.round(inputCost + outputCost);
}

/**
 * Generate rate card markdown for a model
 * USD rate: points_per_1k * 0.03 = $ per 1M tokens
 */
function generateRateCard(model: string, pricing: ModelPricing): string {
	// Convert points/1k to $/1M: points_per_1k * 0.03 = $/1M
	const inputUsdPerMillion = (pricing.input_points_per_1k * 0.03).toFixed(2);
	const outputUsdPerMillion = (pricing.output_points_per_1k * 0.03).toFixed(2);
	
	let rateCard = `Message cost is variable, so longer messages are more expensive than shorter messages.\n\n`;
	rateCard += `| Type | Rate (USD) | Rate (Points) |\n`;
	rateCard += `|------|------------|---------------|\n`;
	rateCard += `| Input | $${inputUsdPerMillion}/1M tokens | ${pricing.input_points_per_1k} points/1k tokens |\n`;
	rateCard += `| Output | $${outputUsdPerMillion}/1M tokens | ${pricing.output_points_per_1k} points/1k tokens |\n`;
	
	if (pricing.cache_discount) {
		const discountPercent = Math.round(pricing.cache_discount * 100);
		rateCard += `| Cache discount | ${discountPercent}% | ${discountPercent}% |\n`;
	}
	
	return rateCard;
}

/**
 * Generate cost label for a model (short summary)
 */
function generateCostLabel(pricing: ModelPricing): string {
	const inputMilliCents = Math.round(pricing.input_points_per_1k * 30);
	return `[usd_milli_cents=${inputMilliCents}]+`;
}

// =============================================================================
// Types
// =============================================================================

interface PoeMessage {
	role: 'system' | 'user' | 'bot' | 'tool';
	content: string;
	content_type?: string;
	attachments?: PoeAttachment[];
	tool_call_id?: string;
	tool_calls?: PoeToolCall[];
	parameters?: Record<string, unknown>;
}

interface PoeAttachment {
	url: string;
	content_type: string;
	name?: string;
	parsed_content?: string;
}

interface PoeToolCall {
	id: string;
	type: 'function';
	function: {
		name: string;
		arguments: string;
	};
}

interface PoeTool {
	type: 'function';
	function: {
		name: string;
		description?: string;
		parameters?: Record<string, unknown>;
	};
}

interface PoeQueryRequest {
	version: string;
	type: 'query';
	query: PoeMessage[];
	user_id: string;
	conversation_id: string;
	message_id: string;
	metadata?: string;
	api_key?: string;
	access_key?: string;
	temperature?: number;
	skip_system_prompt?: boolean;
	logit_bias?: Record<string, number>;
	stop_sequences?: string[];
	language_code?: string;
	tools?: PoeTool[];
	tool_calls?: PoeToolCall[];
	tool_results?: Array<{
		role: 'tool';
		tool_call_id: string;
		content: string;
	}>;
}

interface PoeSettingsRequest {
	version: string;
	type: 'settings';
}

type PoeRequest = PoeQueryRequest | PoeSettingsRequest;

interface OpenAIMessage {
	role: 'system' | 'user' | 'assistant' | 'tool';
	content: string | null;
	tool_calls?: Array<{
		id: string;
		type: 'function';
		function: {
			name: string;
			arguments: string;
		};
	}>;
	tool_call_id?: string;
}

interface OpenAITool {
	type: 'function';
	function: {
		name: string;
		description: string;
		parameters: Record<string, unknown>;
	};
}

interface OpenAIRequest {
	model: string;
	messages: OpenAIMessage[];
	stream: boolean;
	temperature?: number;
	stop?: string[];
	tools?: OpenAITool[];
	tool_choice?: 'auto' | 'none' | { type: 'function'; function: { name: string } };
}

interface PoeSettingsResponse {
	// Protocol version to use for default values
	response_version?: number;
	// Bot dependencies for bot query API
	server_bot_dependencies?: Record<string, number>;
	// Parameter controls UI
	parameter_controls?: {
		api_version: string;
		sections: Array<{
			name?: string;
			controls?: Array<Record<string, unknown>>;
			collapsed_by_default?: boolean;
		}>;
	};
	// Monetization - rate card and cost label
	rate_card?: string;
	cost_label?: string;
	// Attachment settings
	allow_attachments?: boolean;
	expand_text_attachments?: boolean;
	enable_image_comprehension?: boolean;
	// Introduction message (Markdown)
	introduction_message?: string;
	// Message handling
	enforce_author_role_alternation?: boolean;
	enable_multi_entity_prompting?: boolean;
}

// =============================================================================
// SSRF Protection
// =============================================================================

function validateTargetUrl(target: string): boolean {
	try {
		const url = new URL(target);
		if (url.protocol !== 'https:') return false;
		const hostname = url.hostname.toLowerCase();
		if (hostname === 'localhost' || hostname === '127.0.0.1' || hostname === '[::1]') return false;
		if (hostname.startsWith('192.168.') || hostname.startsWith('10.') || hostname.startsWith('172.')) return false;
		if (hostname.endsWith('.local') || hostname.endsWith('.internal')) return false;
		return true;
	} catch {
		return false;
	}
}

// =============================================================================
// Poe <-> OpenAI Translation
// =============================================================================

function translatePoeToOpenAI(poeRequest: PoeQueryRequest, model: string): OpenAIRequest {
	const messages: OpenAIMessage[] = [];

	for (const msg of poeRequest.query) {
		const openaiMessage: OpenAIMessage = {
			role: msg.role === 'bot' ? 'assistant' : msg.role,
			content: msg.content,
		};

		if (msg.attachments && msg.attachments.length > 0) {
			const attachmentText = msg.attachments
				.filter(a => a.parsed_content)
				.map(a => `\n\n[Attachment: ${a.name || 'file'}]\n${a.parsed_content}`)
				.join('');
			if (attachmentText && openaiMessage.content) {
				openaiMessage.content += attachmentText;
			}
		}

		if (msg.role === 'bot' && msg.tool_calls) {
			openaiMessage.tool_calls = msg.tool_calls.map(tc => ({
				id: tc.id,
				type: 'function' as const,
				function: {
					name: tc.function.name,
					arguments: tc.function.arguments,
				},
			}));
			if (!openaiMessage.content) {
				openaiMessage.content = null;
			}
		}

		if (msg.role === 'tool' && msg.tool_call_id) {
			openaiMessage.tool_call_id = msg.tool_call_id;
		}

		messages.push(openaiMessage);
	}

	if (poeRequest.tool_results) {
		for (const result of poeRequest.tool_results) {
			messages.push({
				role: 'tool',
				content: result.content,
				tool_call_id: result.tool_call_id,
			});
		}
	}

	const openaiRequest: OpenAIRequest = {
		model,
		messages,
		stream: true,
	};

	if (poeRequest.temperature !== undefined && poeRequest.temperature !== null) {
		openaiRequest.temperature = poeRequest.temperature;
	}

	if (poeRequest.stop_sequences && poeRequest.stop_sequences.length > 0) {
		openaiRequest.stop = poeRequest.stop_sequences;
	}

	if (poeRequest.tools && poeRequest.tools.length > 0) {
		openaiRequest.tools = poeRequest.tools.map(tool => ({
			type: 'function' as const,
			function: {
				name: tool.function.name,
				description: tool.function.description || '',
				parameters: tool.function.parameters || { type: 'object', properties: {} },
			},
		}));
		openaiRequest.tool_choice = 'auto';
	}

	return openaiRequest;
}

async function translateOpenAIStreamToPoe(
	openaiStream: ReadableStream<Uint8Array>,
	writer: WritableStreamDefaultWriter<Uint8Array>,
	encoder: TextEncoder,
	pricing?: ModelPricing
): Promise<void> {
	const reader = openaiStream.getReader();
	const decoder = new TextDecoder();
	let buffer = '';

	const toolCallAccumulator: Map<number, {
		id: string;
		name: string;
		arguments: string;
	}> = new Map();

	// Track token usage for cost capture
	let promptTokens = 0;
	let completionTokens = 0;

	const sendEvent = async (event: string, data: unknown) => {
		await writer.write(encoder.encode(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`));
	};

	try {
		while (true) {
			const { done, value } = await reader.read();
			if (done) break;

			buffer += decoder.decode(value, { stream: true });
			const lines = buffer.split('\n');
			buffer = lines.pop() || '';

			for (const line of lines) {
				if (!line.startsWith('data: ')) continue;
				const data = line.slice(6);
				if (data === '[DONE]') continue;

				try {
					const json = JSON.parse(data);
					const choice = json.choices?.[0];
					if (!choice) continue;

					const content = choice.delta?.content;
					if (content) {
						await sendEvent('text', { text: content });
					}

					const toolCalls = choice.delta?.tool_calls;
					if (toolCalls) {
						for (const tc of toolCalls) {
							const idx = tc.index ?? 0;
							let accumulated = toolCallAccumulator.get(idx);

							if (!accumulated) {
								accumulated = { id: '', name: '', arguments: '' };
								toolCallAccumulator.set(idx, accumulated);
							}

							if (tc.id) accumulated.id = tc.id;
							if (tc.function?.name) accumulated.name = tc.function.name;
							if (tc.function?.arguments) accumulated.arguments += tc.function.arguments;
						}
					}

					if (choice.finish_reason === 'tool_calls') {
						for (const [, tc] of toolCallAccumulator) {
							await sendEvent('tool_call', {
								id: tc.id,
								function: {
									name: tc.name,
									arguments: tc.arguments,
								},
							});
						}
						toolCallAccumulator.clear();
					}

					// Capture usage from streaming response (typically in final chunk)
					if (json.usage) {
						promptTokens = json.usage.prompt_tokens || json.usage.input_tokens || 0;
						completionTokens = json.usage.completion_tokens || json.usage.output_tokens || 0;
					}
				} catch {
					// Ignore parse errors
				}
			}
		}

		// Send cost capture event if pricing is configured and we have usage data
		if (pricing && (promptTokens > 0 || completionTokens > 0)) {
			const totalCostMilliCents = calculateCostMilliCents(promptTokens, completionTokens, pricing);
			if (totalCostMilliCents > 0) {
				await sendEvent('cost_capture', {
					amounts: [
						{
							amount_usd_milli_cents: totalCostMilliCents,
							description: `${promptTokens} input + ${completionTokens} output tokens`
						}
					]
				});
			}
		}

		await sendEvent('done', {});
	} catch (error) {
		await sendEvent('error', {
			text: String(error),
			allow_retry: true,
		});
	}
}

function poeErrorResponse(message: string, allowRetry: boolean): Response {
	const encoder = new TextEncoder();
	const body = encoder.encode(
		`event: error\ndata: ${JSON.stringify({ text: message, allow_retry: allowRetry })}\n\n` +
		`event: done\ndata: {}\n\n`
	);

	return new Response(body, {
		headers: {
			'Content-Type': 'text/event-stream',
			'Cache-Control': 'no-cache',
			...corsHeaders(),
		},
	});
}

// =============================================================================
// Handlers
// =============================================================================

/**
 * Handle POST /poe/server
 */
export async function handlePoeServer(request: Request, env: Env, url: URL): Promise<Response> {
	// Verify request authorization against AUTH_SECRET if configured
	if (env.AUTH_SECRET) {
		const authHeader = request.headers.get('Authorization');
		const expectedAuth = env.AUTH_SECRET.toLowerCase().startsWith('bearer ')
			? env.AUTH_SECRET
			: `Bearer ${env.AUTH_SECRET}`;
		if (!authHeader || authHeader !== expectedAuth) {
			return poeErrorResponse('Unauthorized', false);
		}
	}

	const poeRequest = await request.json() as PoeRequest;

	// Handle settings request
	if (poeRequest.type === 'settings') {
		return handlePoeSettingsRequest(env, url);
	}

	// Handle query request
	const queryRequest = poeRequest as PoeQueryRequest;
	const targetParam = url.searchParams.get('target');
	
	// Get parameters from the last message
	const lastMessage = queryRequest.query?.[queryRequest.query.length - 1];
	const params = lastMessage?.parameters || {};
	
	// Get model: 1) query param, 2) custom_model if model_id=custom, 3) model_id param, 4) env default, 5) hardcoded
	let model = url.searchParams.get('model');
	if (!model) {
		const paramModel = params.model_id as string | undefined;
		if (paramModel === 'custom') {
			model = (params.custom_model as string) || env.DEFAULT_MODEL || 'gpt-4o';
		} else {
			model = paramModel || env.DEFAULT_MODEL || 'gpt-4o';
		}
	}
	
	// Build OpenAI request with additional parameters
	const openaiRequest = translatePoeToOpenAI(queryRequest, model);
	
	// Apply optional parameters from parameter controls
	if (typeof params.temperature === 'number') {
		openaiRequest.temperature = params.temperature;
	}
	if (typeof params.max_tokens === 'number' && params.max_tokens > 0) {
		(openaiRequest as Record<string, unknown>).max_tokens = params.max_tokens;
	}
	if (typeof params.top_p === 'number') {
		(openaiRequest as Record<string, unknown>).top_p = params.top_p;
	}
	if (typeof params.frequency_penalty === 'number') {
		(openaiRequest as Record<string, unknown>).frequency_penalty = params.frequency_penalty;
	}
	if (typeof params.presence_penalty === 'number') {
		(openaiRequest as Record<string, unknown>).presence_penalty = params.presence_penalty;
	}

	const headers: Record<string, string> = {
		'Content-Type': 'application/json',
	};

	// Get token from query param for backing API authorization
	const tokenParam = url.searchParams.get('token');
	if (tokenParam) {
		// Prepend 'Bearer ' if not already present
		headers['Authorization'] = tokenParam.toLowerCase().startsWith('bearer ')
			? tokenParam
			: `Bearer ${tokenParam}`;
	}

	let response: Response;

	if (targetParam) {
		// External target - validate and use fetch
		if (!validateTargetUrl(targetParam)) {
			return errorResponse('Invalid target URL. Must be https and not a private address.', 400, 'invalid_target');
		}
		response = await fetch(targetParam, {
			method: 'POST',
			headers,
			body: JSON.stringify(openaiRequest),
		});
	} else {
		// Internal copilot proxy - call handler directly to avoid self-fetch issues
		const copilotRequest = new Request(
			`${url.origin}/copilot/v1/chat/completions`,
			{
				method: 'POST',
				headers,
				body: JSON.stringify(openaiRequest),
			}
		);
		response = await handleCopilotProxy(copilotRequest, env, '/copilot/v1/chat/completions');
	}

	if (!response.ok) {
		const error = await response.text();
		return poeErrorResponse(`Backend error: ${error}`, true);
	}

	// Get pricing for cost events (only if cost=true query param)
	const costEnabled = url.searchParams.get('cost') === 'true';
	const pricing = costEnabled ? getModelPricing(model) : undefined;

	const { readable, writable } = new TransformStream();
	const writer = writable.getWriter();
	const encoder = new TextEncoder();

	(async () => {
		try {
			// Send meta event to trigger settings refetch (helps Poe recognize tool support)
			// await writer.write(encoder.encode(`event: meta\ndata: ${JSON.stringify({
			// 	refetch_settings: true
			// })}\n\n`));

			// Send cost_authorize event with estimated cost (based on rough input token estimate)
			if (pricing) {
				// Rough estimate: ~4 chars per token, estimate output as 500 tokens
				const estimatedInputTokens = Math.ceil(JSON.stringify(queryRequest.query).length / 4);
				const estimatedOutputTokens = 500;
				const estimatedCost = calculateCostMilliCents(estimatedInputTokens, estimatedOutputTokens, pricing);
				if (estimatedCost > 0) {
					await writer.write(encoder.encode(`event: cost_authorize\ndata: ${JSON.stringify({
						amounts: [{
							amount_usd_milli_cents: estimatedCost,
							description: `Estimated: ~${estimatedInputTokens} input tokens`
						}]
					})}\n\n`));
				}
			}

			if (response.body) {
				await translateOpenAIStreamToPoe(response.body, writer, encoder, pricing);
			}
		} finally {
			await writer.close();
		}
	})();

	return new Response(readable, {
		headers: {
			'Content-Type': 'text/event-stream',
			'Cache-Control': 'no-cache',
			...corsHeaders(),
		},
	});
}

/**
 * Build settings response with parameter controls
 */
function handlePoeSettingsRequest(env: Env, url: URL): Response {
	const defaultModel = env.DEFAULT_MODEL || 'gpt-4o';
	const modelFromQuery = url.searchParams.get('model');
	
	// Build sections array - only include Connection section if model not specified in query
	const sections: Array<Record<string, unknown>> = [];
	
	if (!modelFromQuery) {
		sections.push({
			name: "Connection",
			controls: [
				{
					control: "drop_down",
					label: "Model",
					parameter_name: "model_id",
					description: "Model to use",
					default_value: defaultModel,
					options: [
						{ value: "gpt-4.1", name: "GPT-4.1" },
						{ value: "gpt-4o", name: "GPT-4o" },
						{ value: "gpt-5-mini", name: "GPT-5 mini" },
						{ value: "claude-haiku-4.5", name: "Claude Haiku 4.5" },
						{ value: "claude-opus-4.5", name: "Claude Opus 4.5" },
						{ value: "claude-sonnet-4", name: "Claude Sonnet 4" },
						{ value: "claude-sonnet-4.5", name: "Claude Sonnet 4.5" },
						{ value: "gemini-2.5-pro", name: "Gemini 2.5 Pro" },
						{ value: "gemini-3-flash-preview", name: "Gemini 3 Flash (Preview)" },
						{ value: "gemini-3-pro-preview", name: "Gemini 3 Pro (Preview)" },
						{ value: "gpt-5", name: "GPT-5" },
						{ value: "gpt-5-codex", name: "GPT-5-Codex (Preview)" },
						{ value: "gpt-5.1", name: "GPT-5.1" },
						{ value: "gpt-5.1-codex", name: "GPT-5.1-Codex" },
						{ value: "gpt-5.1-codex-max", name: "GPT-5.1-Codex-Max" },
						{ value: "gpt-5.1-codex-mini", name: "GPT-5.1-Codex-Mini (Preview)" },
						{ value: "gpt-5.2", name: "GPT-5.2" },
						{ value: "gpt-5.2-codex", name: "GPT-5.2-Codex" },
						{ value: "custom", name: "Custom..." },
					],
				},
				{
					control: "text_field",
					label: "Custom Model",
					parameter_name: "custom_model",
					description: "Used when 'Custom...' is selected above",
					placeholder: "gpt-4-turbo",
				},
			],
			collapsed_by_default: false,
		});
	}
	
	sections.push({
	name: "Model Parameters",
		controls: [
			{
				control: "slider",
				label: "Temperature",
				parameter_name: "temperature",
				description: "Controls randomness (0 = deterministic, 2 = creative)",
				default_value: 1.0,
				min_value: 0.0,
				max_value: 2.0,
				step: 0.1,
			},
			{
				control: "slider",
				label: "Max Tokens",
				parameter_name: "max_tokens",
				description: "Maximum tokens in response (0 = no limit)",
				default_value: 0,
				min_value: 0,
				max_value: 16384,
				step: 256,
			},
			{
				control: "slider",
				label: "Top P",
				parameter_name: "top_p",
				description: "Nucleus sampling threshold",
				default_value: 1.0,
				min_value: 0.0,
				max_value: 1.0,
				step: 0.05,
			},
			{
				control: "slider",
				label: "Frequency Penalty",
				parameter_name: "frequency_penalty",
				description: "Penalizes repeated tokens based on frequency",
				default_value: 0.0,
				min_value: -2.0,
				max_value: 2.0,
				step: 0.1,
			},
			{
				control: "slider",
				label: "Presence Penalty",
				parameter_name: "presence_penalty",
				description: "Penalizes tokens based on presence in text",
				default_value: 0.0,
				min_value: -2.0,
				max_value: 2.0,
				step: 0.1,
			},
		],
		collapsed_by_default: true,
	});
	
	const settings: PoeSettingsResponse = {
		response_version: 2,
		server_bot_dependencies: {},
		parameter_controls: {
			api_version: "2",
			sections,
		},
		allow_attachments: true,
		expand_text_attachments: true,
		enable_image_comprehension: false,
		enforce_author_role_alternation: false,
		enable_multi_entity_prompting: true,
	};

	// Add rate card and cost label only when model is specified and cost=true
	const costEnabled = url.searchParams.get('cost') === 'true';
	if (modelFromQuery && costEnabled) {
		const pricing = getModelPricing(modelFromQuery);
		settings.rate_card = generateRateCard(modelFromQuery, pricing);
		settings.cost_label = generateCostLabel(pricing);
	}

	return jsonResponse(settings);
}

/**
 * Handle POST /poe/settings (legacy endpoint)
 */
export async function handlePoeSettings(request: Request, env: Env, url: URL): Promise<Response> {
	return handlePoeSettingsRequest(env, url);
}
