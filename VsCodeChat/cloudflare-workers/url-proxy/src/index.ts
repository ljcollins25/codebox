/**
 * URL Proxy Worker
 * 
 * Pure passthrough proxy - no modifications to request or response.
 * URL format: https://{worker host}/{target host}/{target path and query}
 * 
 * Examples:
 *   https://proxy.example.com/api.github.com/users/octocat
 *     -> https://api.github.com/users/octocat
 * 
 *   https://proxy.example.com/httpbin.org/get?foo=bar
 *     -> https://httpbin.org/get?foo=bar
 */

export interface Env {
	// Service binding for copilot-proxy Worker
	COPILOT_PROXY: Fetcher;
	// Azure Blob Storage SAS URL for request logging (full URL with SAS token as query string)
	BLOB_SAS_URL?: string;
}

// Map of hostnames to service bindings
const SERVICE_BINDINGS: Record<string, keyof Env> = {
	'copilot-proxy.ref12cf.workers.dev': 'COPILOT_PROXY',
};

/**
 * Store request body to Azure Blob Storage with timestamp
 */
async function storeRequestBody(request: Request, targetUrl: string, ctx: ExecutionContext, blobSasUrl: string): Promise<string> {
	// Clone request to read body without consuming it
	const body = await request.clone().text();
	if (!body) return ''; // Skip empty bodies

	// Parse container URL and SAS token from the full SAS URL
	const sasUrl = new URL(blobSasUrl);
	const containerUrl = `${sasUrl.origin}${sasUrl.pathname}`;
	const sasToken = sasUrl.search.slice(1); // Remove leading '?'

	// Create timestamped blob name
	const now = new Date();
	const timestamp = now.toISOString().replace(/[:.]/g, '-');
	const blobName = `${timestamp}-request.json`;
	const blobUrl = `${containerUrl}/${blobName}?${sasToken}`;

	// Prepare payload with metadata
	const payload = JSON.stringify({
		timestamp: now.toISOString(),
		type: 'request',
		method: request.method,
		targetUrl,
		headers: Object.fromEntries(request.headers),
		body: tryParseJson(body),
	}, null, 2);

	// Fire and forget - don't block the response
	ctx.waitUntil(
		fetch(blobUrl, {
			method: 'PUT',
			headers: {
				'Content-Type': 'application/json',
				'x-ms-blob-type': 'BlockBlob',
			},
			body: payload,
		}).catch(err => console.error('Failed to store request:', err))
	);

	return timestamp;
}

/**
 * Store response body to Azure Blob Storage
 */
async function storeResponseBody(
	response: Response,
	targetUrl: string,
	timestamp: string,
	ctx: ExecutionContext,
	blobSasUrl: string
): Promise<Response> {
	// Parse container URL and SAS token from the full SAS URL
	const sasUrl = new URL(blobSasUrl);
	const containerUrl = `${sasUrl.origin}${sasUrl.pathname}`;
	const sasToken = sasUrl.search.slice(1); // Remove leading '?'

	const blobName = `${timestamp}-response.json`;
	const blobUrl = `${containerUrl}/${blobName}?${sasToken}`;

	// Clone response to read body without consuming it
	const body = await response.clone().text();

	// Prepare payload with metadata
	const payload = JSON.stringify({
		timestamp: new Date().toISOString(),
		type: 'response',
		status: response.status,
		statusText: response.statusText,
		targetUrl,
		headers: Object.fromEntries(response.headers),
		body: tryParseJson(body),
	}, null, 2);

	// Fire and forget - don't block the response
	ctx.waitUntil(
		fetch(blobUrl, {
			method: 'PUT',
			headers: {
				'Content-Type': 'application/json',
				'x-ms-blob-type': 'BlockBlob',
			},
			body: payload,
		}).catch(err => console.error('Failed to store response:', err))
	);

	return response;
}

/**
 * Try to parse JSON, return original string if not valid JSON
 */
function tryParseJson(str: string): unknown {
	try {
		return JSON.parse(str);
	} catch {
		return str;
	}
}

export default {
	async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
		const url = new URL(request.url);

		// Root path - return usage info
		if (url.pathname === '/' || url.pathname === '') {
			return new Response(JSON.stringify({
				usage: 'https://{this-worker}/{target-host}/{target-path}',
				example: `https://${url.host}/httpbin.org/get?foo=bar`,
			}, null, 2), {
				headers: { 'Content-Type': 'application/json' },
			});
		}

		// Parse target URL from path
		const pathParts = url.pathname.slice(1).split('/');
		const targetHost = pathParts[0];
		const targetPath = '/' + pathParts.slice(1).join('/');
		const targetUrl = `https://${targetHost}${targetPath}${url.search}`;

		if (!targetHost || !targetHost.includes('.')) {
			return new Response(JSON.stringify({
				error: 'Invalid target host',
				received: targetHost,
			}), {
				status: 400,
				headers: { 'Content-Type': 'application/json' },
			});
		}

		// Store request body to Azure Blob (non-blocking)
		let requestTimestamp = '';
		if ((request.method === 'POST' || request.method === 'PUT') && env.BLOB_SAS_URL) {
			requestTimestamp = await storeRequestBody(request, targetUrl, ctx, env.BLOB_SAS_URL);
		}

		try {
			// Check if we have a service binding for this host
			const serviceBindingKey = SERVICE_BINDINGS[targetHost];
			let response: Response;

			if (serviceBindingKey && env[serviceBindingKey]) {
				// Use service binding for Worker-to-Worker communication
				const service = env[serviceBindingKey] as Fetcher;
				response = await service.fetch(
					new Request(targetUrl, {
						method: request.method,
						headers: request.headers,
						body: request.body,
					})
				);
			} else {
				// Direct fetch passthrough
				response = await fetch(targetUrl, {
					method: request.method,
					headers: request.headers,
					body: request.body,
				});
			}

			// Store response body to Azure Blob (non-blocking)
			if (requestTimestamp && env.BLOB_SAS_URL) {
				storeResponseBody(response.clone(), targetUrl, requestTimestamp, ctx, env.BLOB_SAS_URL);
			}

			return response;
		} catch (error) {
			return new Response(JSON.stringify({
				error: 'Proxy request failed',
				message: String(error),
				target: targetUrl,
			}), {
				status: 502,
				headers: { 'Content-Type': 'application/json' },
			});
		}
	},
};
