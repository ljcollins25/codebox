/**
 * JSON Extraction Service - Cloudflare Worker
 * 
 * Evaluates dot-path expressions from query parameters against the request body.
 * 
 * Usage:
 *   POST /?j_title=store.book.0.title&j_price=store.book.0.price
 *   Body: { "store": { "book": [{ "title": "1984", "price": 10 }] } }
 *   
 *   Response: { "title": "1984", "price": 10 }
 */

const PREFIX = 'j_'; // Configurable prefix for query parameters

/**
 * Evaluate a dot-path expression against data
 * @param {any} data - The data object to traverse
 * @param {string} path - Dot-separated path (e.g., "store.book.0.title")
 * @returns {any} The value at the path, or null if not found
 */
function evaluatePath(data, path) {
    if (!path || typeof path !== 'string') return null;
    
    const parts = path.split('.');
    let current = data;
    
    for (const part of parts) {
        if (current === null || current === undefined) return null;
        
        // Handle array index (numeric)
        if (/^\d+$/.test(part)) {
            const index = parseInt(part, 10);
            if (!Array.isArray(current)) return null;
            current = current[index];
        } else {
            // Handle object property
            if (typeof current !== 'object') return null;
            current = current[part];
        }
    }
    
    return current !== undefined ? current : null;
}

/**
 * Create a JSON response
 */
function jsonResponse(data, status = 200) {
    return new Response(JSON.stringify(data), {
        status,
        headers: {
            'Content-Type': 'application/json',
            'Access-Control-Allow-Origin': '*',
        },
    });
}

export default {
    async fetch(request, env, ctx) {
        // Handle CORS preflight
        if (request.method === 'OPTIONS') {
            return new Response(null, {
                headers: {
                    'Access-Control-Allow-Origin': '*',
                    'Access-Control-Allow-Methods': 'POST, OPTIONS',
                    'Access-Control-Allow-Headers': 'Content-Type',
                },
            });
        }

        // Only accept POST requests
        if (request.method !== 'POST') {
            return jsonResponse({ error: 'Method not allowed. Use POST.' }, 405);
        }

        // Parse JSON body
        let data;
        try {
            const bodyText = await request.text();
            if (!bodyText.trim()) {
                return jsonResponse({ error: 'Request body is empty' }, 400);
            }
            data = JSON.parse(bodyText);
        } catch (e) {
            return jsonResponse({ error: 'Invalid JSON in request body' }, 400);
        }

        // Get query parameters
        const url = new URL(request.url);
        const params = url.searchParams;

        // Build result object from prefixed query parameters
        const result = {};

        for (const [key, value] of params.entries()) {
            if (key.startsWith(PREFIX)) {
                const outputKey = key.slice(PREFIX.length);
                if (outputKey) {
                    try {
                        result[outputKey] = evaluatePath(data, value);
                    } catch (e) {
                        result[outputKey] = null;
                    }
                }
            }
        }

        return jsonResponse(result);
    },
};
