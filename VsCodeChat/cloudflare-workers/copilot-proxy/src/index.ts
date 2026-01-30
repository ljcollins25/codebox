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
 * 
 * References:
 * - https://creator.poe.com/docs/server-bots/quick-start
 * - https://creator.poe.com/docs/server-bots/parameter-controls
 * - https://creator.poe.com/docs/server-bots/function-calling
 */

export interface Env {
TOKEN_CACHE: KVNamespace;
SERVER_SECRET?: string;
}

// =============================================================================
// Constants
// =============================================================================

const GITHUB_CLIENT_ID = '01ab8ac9400c4e429b23';
const GITHUB_SCOPES = 'read:user';
const COPILOT_TOKEN_LIFETIME_MINUTES = 25;
const COPILOT_API_BASE = 'https://api.githubcopilot.com';

// VS Code emulation headers
const VSCODE_HEADERS: Record<string, string> = {
'User-Agent': 'GitHubCopilotChat/1.0.0',
'Editor-Version': 'vscode/1.96.0',
'Editor-Plugin-Version': 'copilot-chat/0.26.0',
'Openai-Organization': 'github-copilot',
'Copilot-Integration-Id': 'vscode-chat',
'Content-Type': 'application/json',
};

// =============================================================================
// CORS Helpers
// =============================================================================

function corsHeaders(): Record<string, string> {
return {
'Access-Control-Allow-Origin': '*',
'Access-Control-Allow-Methods': 'GET, POST, PUT, DELETE, OPTIONS',
'Access-Control-Allow-Headers': 'Content-Type, Authorization, X-Request-Id, X-Poe-Access-Key',
'Access-Control-Max-Age': '86400',
};
}

function handleCORS(): Response {
return new Response(null, { status: 204, headers: corsHeaders() });
}

function jsonResponse(data: unknown, status = 200, extraHeaders: Record<string, string> = {}): Response {
return new Response(JSON.stringify(data, null, 2), {
status,
headers: { 'Content-Type': 'application/json', ...corsHeaders(), ...extraHeaders },
});
}

function errorResponse(message: string, status: number, type = 'error'): Response {
return jsonResponse({ error: { message, type } }, status);
}

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
return await handlePoeSettings(request, env);
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

// =============================================================================
// /login - Device Flow Initiation
// =============================================================================

interface DeviceFlowResponse {
device_code: string;
user_code: string;
verification_uri: string;
verification_uri_complete?: string;
expires_in: number;
interval: number;
}

async function handleLogin(request: Request, env: Env): Promise<Response> {
const response = await fetch('https://github.com/login/device/code', {
method: 'POST',
headers: {
'Accept': 'application/json',
'Content-Type': 'application/x-www-form-urlencoded',
},
body: `client_id=${GITHUB_CLIENT_ID}&scope=${GITHUB_SCOPES}`,
});

if (!response.ok) {
const text = await response.text();
return errorResponse(`GitHub device flow failed: ${text}`, response.status);
}

const data: DeviceFlowResponse = await response.json();

return jsonResponse({
device_code: data.device_code,
user_code: data.user_code,
verification_uri: data.verification_uri,
verification_uri_complete: data.verification_uri_complete || `${data.verification_uri}?user_code=${data.user_code}`,
expires_in: data.expires_in,
expires_at: new Date(Date.now() + data.expires_in * 1000).toISOString(),
interval: data.interval || 5,
});
}

// =============================================================================
// /copilot/v1/* - OpenAI-Compatible Copilot Proxy
// =============================================================================

interface CopilotTokenCache {
copilot_token: string;
expires_at: string;
}

function extractGitHubToken(request: Request): string | null {
const auth = request.headers.get('Authorization');
if (!auth) return null;
if (auth.toLowerCase().startsWith('bearer ')) {
return auth.slice(7).trim();
}
return auth.trim();
}

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

async function handleCopilotProxy(request: Request, env: Env, path: string): Promise<Response> {
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

// =============================================================================
// /poe/server - Poe Server Bot Translation
// =============================================================================

interface PoeMessage {
role: 'system' | 'user' | 'bot' | 'tool';
content: string;
content_type?: string;
attachments?: PoeAttachment[];
tool_call_id?: string;
tool_calls?: PoeToolCall[];
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
encoder: TextEncoder
): Promise<void> {
const reader = openaiStream.getReader();
const decoder = new TextDecoder();
let buffer = '';

const toolCallAccumulator: Map<number, {
id: string;
name: string;
arguments: string;
}> = new Map();

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
} catch {
// Ignore parse errors
}
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

async function handlePoeServer(request: Request, env: Env, url: URL): Promise<Response> {
const poeRequest = await request.json() as PoeQueryRequest;

let targetUrl: string;
const targetParam = url.searchParams.get('target');

if (targetParam) {
if (!validateTargetUrl(targetParam)) {
return errorResponse('Invalid target URL. Must be https and not a private address.', 400, 'invalid_target');
}
targetUrl = targetParam;
} else {
const base = new URL(request.url);
targetUrl = `${base.origin}/copilot/v1/chat/completions`;
}

const model = url.searchParams.get('model') || 'gpt-4o';
const openaiRequest = translatePoeToOpenAI(poeRequest, model);

const headers: Record<string, string> = {
'Content-Type': 'application/json',
};

const authHeader = request.headers.get('Authorization');
if (authHeader) {
headers['Authorization'] = authHeader;
}

const response = await fetch(targetUrl, {
method: 'POST',
headers,
body: JSON.stringify(openaiRequest),
});

if (!response.ok) {
const error = await response.text();
return poeErrorResponse(`Backend error: ${error}`, true);
}

const { readable, writable } = new TransformStream();
const writer = writable.getWriter();
const encoder = new TextEncoder();

(async () => {
try {
if (response.body) {
await translateOpenAIStreamToPoe(response.body, writer, encoder);
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
// /poe/settings - Poe Parameter Controls
// =============================================================================

interface PoeSettingsResponse {
server_bot_dependencies?: Record<string, number>;
allow_attachments?: boolean;
expand_text_attachments?: boolean;
enable_image_comprehension?: boolean;
introduction_message?: string;
enforce_author_role_alternation?: boolean;
enable_multi_bot_chat_prompting?: boolean;
}

async function handlePoeSettings(request: Request, env: Env): Promise<Response> {
const settings: PoeSettingsResponse = {
server_bot_dependencies: {},
allow_attachments: true,
expand_text_attachments: true,
enable_image_comprehension: false,
introduction_message: "Hello! I'm a GitHub Copilot proxy bot. Send me a message to get started.",
enforce_author_role_alternation: false,
enable_multi_bot_chat_prompting: false,
};

return jsonResponse(settings);
}
