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
}

// Map of hostnames to service bindings
const SERVICE_BINDINGS: Record<string, keyof Env> = {
	'copilot-proxy.ref12cf.workers.dev': 'COPILOT_PROXY',
};

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

		try {
			// Check if we have a service binding for this host
			const serviceBindingKey = SERVICE_BINDINGS[targetHost];

			if (serviceBindingKey && env[serviceBindingKey]) {
				// Use service binding for Worker-to-Worker communication
				const service = env[serviceBindingKey] as Fetcher;
				return service.fetch(
					new Request(targetUrl, {
						method: request.method,
						headers: request.headers,
						body: request.body,
					})
				);
			} else {
				// Direct fetch passthrough
				return fetch(targetUrl, {
					method: request.method,
					headers: request.headers,
					body: request.body,
				});
			}
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
