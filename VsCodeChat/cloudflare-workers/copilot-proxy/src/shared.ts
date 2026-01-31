/**
 * Shared types, constants, and helpers
 */

export interface Env {
	TOKEN_CACHE: KVNamespace;
	SERVER_SECRET?: string;
	AUTH_SECRET?: string;
}

// =============================================================================
// Constants
// =============================================================================

export const GITHUB_CLIENT_ID = '01ab8ac9400c4e429b23';
export const GITHUB_SCOPES = 'read:user';
export const COPILOT_TOKEN_LIFETIME_MINUTES = 25;
export const COPILOT_API_BASE = 'https://api.githubcopilot.com';

// VS Code emulation headers
export const VSCODE_HEADERS: Record<string, string> = {
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

export function corsHeaders(): Record<string, string> {
	return {
		'Access-Control-Allow-Origin': '*',
		'Access-Control-Allow-Methods': 'GET, POST, PUT, DELETE, OPTIONS',
		'Access-Control-Allow-Headers': 'Content-Type, Authorization, X-Request-Id, X-Poe-Access-Key',
		'Access-Control-Max-Age': '86400',
	};
}

export function handleCORS(): Response {
	return new Response(null, { status: 204, headers: corsHeaders() });
}

export function jsonResponse(data: unknown, status = 200, extraHeaders: Record<string, string> = {}): Response {
	return new Response(JSON.stringify(data, null, 2), {
		status,
		headers: { 'Content-Type': 'application/json', ...corsHeaders(), ...extraHeaders },
	});
}

export function errorResponse(message: string, status: number, type = 'error'): Response {
	return jsonResponse({ error: { message, type } }, status);
}
