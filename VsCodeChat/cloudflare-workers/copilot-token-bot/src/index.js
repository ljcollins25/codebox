/**
 * Copilot Token Bot - Cloudflare Worker
 * 
 * A Poe server bot that handles GitHub device flow authentication
 * and returns Copilot tokens cached in Workers KV.
 */

// VS Code's public OAuth client ID
const GITHUB_CLIENT_ID = "01ab8ac9400c4e429b23";
const GITHUB_SCOPES = "read:user";

// Token lifetimes
const GITHUB_TOKEN_LIFETIME_DAYS = 30;
const COPILOT_TOKEN_LIFETIME_MINUTES = 25; // Copilot tokens expire in ~30 min, refresh early
const DATA_VERSION = "v1";

// Authorization secret (configure via wrangler secret put AUTH_SECRET)
// No default - requires AUTH_SECRET to be set

/**
 * Send a Poe SSE text event
 */
function sseText(text) {
    return `event: text\ndata: ${JSON.stringify({ text })}\n\n`;
}

/**
 * Send a Poe SSE data event (for storing metadata)
 */
function sseData(metadata) {
    return `event: data\ndata: ${JSON.stringify({ metadata: JSON.stringify(metadata) })}\n\n`;
}

/**
 * Send a Poe SSE done event
 */
function sseDone() {
    return `event: done\ndata: {}\n\n`;
}

/**
 * Create an SSE response for Poe
 */
function poeResponse(messages) {
    const encoder = new TextEncoder();
    const body = messages.join('');
    return new Response(encoder.encode(body), {
        headers: { "Content-Type": "text/event-stream" },
    });
}

/**
 * Create an error response for Poe
 */
function poeError(message) {
    return poeResponse([
        sseText(`❌ Error: ${message}`),
        sseDone()
    ]);
}

/**
 * Start GitHub device flow
 */
async function startDeviceFlow() {
    const response = await fetch("https://github.com/login/device/code", {
        method: "POST",
        headers: {
            "Accept": "application/json",
            "Content-Type": "application/x-www-form-urlencoded",
        },
        body: `client_id=${GITHUB_CLIENT_ID}&scope=${GITHUB_SCOPES}`,
    });
    
    if (!response.ok) {
        throw new Error(`Failed to start device flow: ${response.status}`);
    }
    
    return await response.json();
}

/**
 * Poll for device flow authorization
 */
async function pollDeviceFlow(deviceCode, interval = 5) {
    const response = await fetch("https://github.com/login/oauth/access_token", {
        method: "POST",
        headers: {
            "Accept": "application/json",
            "Content-Type": "application/x-www-form-urlencoded",
        },
        body: `client_id=${GITHUB_CLIENT_ID}&device_code=${deviceCode}&grant_type=urn:ietf:params:oauth:grant-type:device_code`,
    });
    
    if (!response.ok) {
        throw new Error(`Failed to poll device flow: ${response.status}`);
    }
    
    return await response.json();
}

/**
 * Get Copilot token from GitHub token
 */
async function getCopilotToken(githubToken) {
    const response = await fetch("https://api.github.com/copilot_internal/v2/token", {
        method: "GET",
        headers: {
            "Authorization": `token ${githubToken}`,
            "Accept": "application/json",
            "Editor-Version": "vscode/1.104.1",
            "Editor-Plugin-Version": "copilot/1.0.0",
            "User-Agent": "GitHubCopilotChat/1.0.0",
        },
    });
    
    if (response.status === 401) {
        throw new Error("INVALID_GITHUB_TOKEN");
    }
    
    if (response.status === 403) {
        throw new Error("NO_COPILOT_ACCESS");
    }
    
    if (!response.ok) {
        const text = await response.text();
        throw new Error(`Failed to get Copilot token: ${response.status} - ${text}`);
    }
    
    return await response.json();
}

/**
 * Get cached data from KV with expiry check
 */
async function getFromKV(kv, key) {
    const data = await kv.get(key, "json");
    if (!data) return null;
    
    // Check if expired based on stored expiry
    if (data.expires_at && new Date(data.expires_at) < new Date()) {
        await kv.delete(key);
        return null;
    }
    
    return data;
}

/**
 * Store data in KV with expiry
 */
async function putToKV(kv, key, data, expirationTtl) {
    await kv.put(key, JSON.stringify(data), { expirationTtl });
}

/**
 * Main handler
 */
export default {
    async fetch(request, env, ctx) {
        const url = new URL(request.url);
        
        // Health check
        if (request.method === "GET" && url.pathname === "/") {
            return new Response("Copilot Token Bot OK");
        }
        
        // Only handle POST to root
        if (request.method !== "POST" || url.pathname !== "/") {
            return new Response("Not Found", { status: 404 });
        }
        
        // Check authorization - AUTH_SECRET must be configured
        const authSecret = env.AUTH_SECRET;
        if (!authSecret) {
            return new Response("AUTH_SECRET not configured", { status: 500 });
        }
        
        const authHeader = request.headers.get("Authorization");
        const queryKey = url.searchParams.get("key");
        
        const isHeaderValid = authHeader === `Bearer ${authSecret}` || authHeader === authSecret;
        const isQueryValid = queryKey === authSecret;

        if (!isHeaderValid && !isQueryValid) {
            return new Response("Unauthorized", { status: 401 });
        }
        
        // Trace request if requested
        const shouldTrace = url.searchParams.get("trace") === "true";
        const requestBodyText = await request.text();

        if (shouldTrace) {
            console.log("Trace Request:");
            console.log("Method:", request.method);
            console.log("URL:", request.url);
            console.log("Headers:", JSON.stringify(Object.fromEntries(request.headers)));
            console.log("Body:", requestBodyText);
        }

        // Parse Poe request body
        let poeBody;
        try {
            poeBody = requestBodyText ? JSON.parse(requestBodyText) : {};
        } catch (e) {
            return poeError("Invalid request body");
        }
        
        // Handle settings request
        if (poeBody.type === "settings") {
            return new Response(JSON.stringify({
                introduction_message: "I manage GitHub Device Flow authentication and provide Copilot tokens.",
                allow_attachments: false,
                enable_image_comprehension: false
            }), {
                headers: { "Content-Type": "application/json" }
            });
        }
        
        // Handle report_error request
        if (poeBody.type === "report_error") {
            console.error("Poe reported error:", poeBody);
            return new Response(JSON.stringify({}), {
                headers: { "Content-Type": "application/json" }
            });
        }
        
        const conversationId = poeBody.conversation_id || null;
        const userId = poeBody.user_id || null;
        
        // Extract polling configuration sent via query params or first query message
        let pollInterval = parseInt(url.searchParams.get("poll_interval_secs")) || 0;
        let pollCount = parseInt(url.searchParams.get("poll_count")) || 1;
        
        if (poeBody.query && poeBody.query.length > 0) {
            try {
                const firstMsg = poeBody.query[0];
                if (firstMsg.content && firstMsg.content.trim().startsWith('{')) {
                    const config = JSON.parse(firstMsg.content);
                    if (Number.isInteger(config.poll_interval_secs)) pollInterval = config.poll_interval_secs;
                    if (Number.isInteger(config.poll_count)) pollCount = config.poll_count;
                }
            } catch (ignore) {}
        }
        
        // KV namespace binding
        const kv = env.TOKEN_CACHE;
        if (!kv) {
            return poeError("KV namespace not configured. Add TOKEN_CACHE binding.");
        }
        
        // Define versioned keys
        const KEY_PENDING_FLOW = `device_flow_pending_${DATA_VERSION}`;
        const KEY_GITHUB_TOKEN = `github_token_${DATA_VERSION}`;
        const KEY_COPILOT_TOKEN = `copilot_token_${DATA_VERSION}`;
        
        try {
            // Show request context first
            const contextLines = [
                sseText("📋 **Request Info**\n\n"),
                sseText(`• Conversation ID: \`${conversationId || 'N/A'}\`\n`),
                sseText(`• User ID: \`${userId || 'N/A'}\`\n\n`),
            ];
            
            // Check for pending device flow
            const pendingFlow = await getFromKV(kv, KEY_PENDING_FLOW);
            
            // Check for cached GitHub token
            let githubData = await getFromKV(kv, KEY_GITHUB_TOKEN);
            
            // If no GitHub token, handle device flow
            if (!githubData) {
                // Check if we already have a pending flow - try to poll it
                if (pendingFlow) {
                    let pollResult;
                    
                    // Poll with configured retries
                    for (let i = 0; i < pollCount; i++) {
                        pollResult = await pollDeviceFlow(pendingFlow.device_code);
                        
                        // If success or fatal error, stop polling
                        if (pollResult.access_token || (pollResult.error && pollResult.error !== "authorization_pending" && pollResult.error !== "slow_down")) {
                            break;
                        }
                        
                        // Wait before next retry if not last attempt
                        if (i < pollCount - 1) {
                            await new Promise(r => setTimeout(r, pollInterval * 1000));
                        }
                    }
                    
                    if (pollResult && pollResult.access_token) {
                        // Success! Store the GitHub token
                        const expiresAt = new Date();
                        expiresAt.setDate(expiresAt.getDate() + GITHUB_TOKEN_LIFETIME_DAYS);
                        
                        githubData = {
                            token: pollResult.access_token,
                            expires_at: expiresAt.toISOString(),
                            acquired_at: new Date().toISOString(),
                            lifetime_days: GITHUB_TOKEN_LIFETIME_DAYS,
                        };
                        
                        await putToKV(kv, KEY_GITHUB_TOKEN, githubData, GITHUB_TOKEN_LIFETIME_DAYS * 24 * 60 * 60);
                        
                        // Clear pending flow
                        await kv.delete(KEY_PENDING_FLOW);
                        
                        // Continue to get Copilot token below
                    } else if (pollResult.error === "authorization_pending") {
                        return poeResponse([
                            ...contextLines,
                            sseText("🔐 **Device Flow In Progress**\n\n"),
                            sseText(`Visit: ${pendingFlow.verification_uri}\n`),
                            sseText(`Enter code: **${pendingFlow.user_code}**\n\n`),
                            sseText("Send another message after authorizing."),
                            sseDone()
                        ]);
                    } else if (pollResult.error === "expired_token") {
                        await kv.delete(KEY_PENDING_FLOW);
                        // Fall through to start new device flow
                    } else if (pollResult.error) {
                        return poeError(`Authorization error: ${pollResult.error}`);
                    }
                }
                
                // Start new device flow if still no GitHub token
                if (!githubData) {
                    const deviceFlow = await startDeviceFlow();
                    
                    // Cache the pending flow (expires when device code expires)
                    await putToKV(kv, KEY_PENDING_FLOW, {
                        device_code: deviceFlow.device_code,
                        user_code: deviceFlow.user_code,
                        verification_uri: deviceFlow.verification_uri,
                        expires_at: new Date(Date.now() + deviceFlow.expires_in * 1000).toISOString(),
                    }, deviceFlow.expires_in);
                    
                    return poeResponse([
                        ...contextLines,
                        sseText("🔐 **GitHub Authorization Required**\n\n"),
                        sseText(`Visit: ${deviceFlow.verification_uri}\n`),
                        sseText(`Enter code: **${deviceFlow.user_code}**\n\n`),
                        sseText("Send another message after authorizing."),
                        sseDone()
                    ]);
                }
            }
            
            // We have a GitHub token, check for cached Copilot token
            let copilotData = await getFromKV(kv, KEY_COPILOT_TOKEN);
            
            // Fetch new Copilot token if needed
            if (!copilotData) {
                try {
                    const copilotResponse = await getCopilotToken(githubData.token);
                    
                    const expiresAt = new Date();
                    expiresAt.setMinutes(expiresAt.getMinutes() + COPILOT_TOKEN_LIFETIME_MINUTES);
                    
                    copilotData = {
                        token: copilotResponse.token,
                        expires_at: expiresAt.toISOString(),
                        acquired_at: new Date().toISOString(),
                        lifetime_minutes: COPILOT_TOKEN_LIFETIME_MINUTES,
                        endpoints: copilotResponse.endpoints || null,
                    };
                    
                    await putToKV(kv, KEY_COPILOT_TOKEN, copilotData, COPILOT_TOKEN_LIFETIME_MINUTES * 60);
                } catch (e) {
                    if (e.message === "INVALID_GITHUB_TOKEN") {
                        // GitHub token is invalid, clear it and start over
                        await kv.delete(KEY_GITHUB_TOKEN);
                        return poeResponse([
                            ...contextLines,
                            sseText("⚠️ GitHub token expired or invalid. Cleared cache.\n\n"),
                            sseText("Send another message to start device flow again."),
                            sseDone()
                        ]);
                    } else if (e.message === "NO_COPILOT_ACCESS") {
                        return poeError("Your GitHub account doesn't have Copilot access.");
                    }
                    throw e;
                }
            }
            
            // Build the result object
            const result = {
                version: DATA_VERSION,
                copilot_token: copilotData.token,
                copilot_expires_at: copilotData.expires_at,
                copilot_acquired_at: copilotData.acquired_at,
                conversation_id: conversationId,
                user_id: userId,
                github_token_expires_at: githubData.expires_at,
                github_token_acquired_at: githubData.acquired_at,
            };
            
            // Return the token info with both text display and data event
            return poeResponse([
                ...contextLines,
                sseText("✅ **Copilot Token Ready**\n\n"),
                sseText("```json\n"),
                sseText(JSON.stringify(result, null, 2)),
                sseText("\n```"),
                sseData(result),
                sseDone()
            ]);
            
        } catch (e) {
            console.error("Error:", e);
            return poeError(e.message || "Unknown error");
        }
    },
};
