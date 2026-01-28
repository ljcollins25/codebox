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

// Authorization secret (configure via environment variable)
const DEFAULT_AUTH_SECRET = "111111";

/**
 * Send a Poe SSE text event
 */
function sseText(text) {
    return `event: text\ndata: ${JSON.stringify({ text })}\n\n`;
}

/**
 * Send a Poe SSE JSON event (for returning structured data)
 */
function sseJson(data) {
    return `event: json\ndata: ${JSON.stringify({ text: JSON.stringify(data, null, 2) })}\n\n`;
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
        
        // Check authorization
        const authSecret = env.AUTH_SECRET || DEFAULT_AUTH_SECRET;
        const authHeader = request.headers.get("Authorization");
        if (authHeader !== `Bearer ${authSecret}` && authHeader !== authSecret) {
            return new Response("Unauthorized", { status: 401 });
        }
        
        // Parse Poe request body
        let poeBody;
        try {
            poeBody = await request.json();
        } catch (e) {
            return poeError("Invalid request body");
        }
        
        const conversationId = poeBody.conversation_id || null;
        const userId = poeBody.user_id || null;
        const queryText = poeBody.query?.[0]?.content?.toLowerCase() || "";
        
        // KV namespace binding
        const kv = env.TOKEN_CACHE;
        if (!kv) {
            return poeError("KV namespace not configured. Add TOKEN_CACHE binding.");
        }
        
        try {
            // Check for pending device flow
            const pendingFlow = await getFromKV(kv, "device_flow_pending");
            
            // If user says "check" or "poll", try to complete pending device flow
            if (pendingFlow && (queryText.includes("check") || queryText.includes("poll") || queryText.includes("done"))) {
                const pollResult = await pollDeviceFlow(pendingFlow.device_code);
                
                if (pollResult.access_token) {
                    // Success! Store the GitHub token
                    const expiresAt = new Date();
                    expiresAt.setDate(expiresAt.getDate() + GITHUB_TOKEN_LIFETIME_DAYS);
                    
                    await putToKV(kv, "github_token", {
                        token: pollResult.access_token,
                        expires_at: expiresAt.toISOString(),
                        lifetime_days: GITHUB_TOKEN_LIFETIME_DAYS,
                    }, GITHUB_TOKEN_LIFETIME_DAYS * 24 * 60 * 60);
                    
                    // Clear pending flow
                    await kv.delete("device_flow_pending");
                    
                    return poeResponse([
                        sseText("✅ GitHub authorization successful! Token cached.\n\n"),
                        sseText("Send any message to get your Copilot token."),
                        sseDone()
                    ]);
                } else if (pollResult.error === "authorization_pending") {
                    return poeResponse([
                        sseText("⏳ Still waiting for authorization...\n\n"),
                        sseText(`Please visit: ${pendingFlow.verification_uri}\n`),
                        sseText(`Enter code: **${pendingFlow.user_code}**\n\n`),
                        sseText("Say 'check' when done."),
                        sseDone()
                    ]);
                } else if (pollResult.error === "expired_token") {
                    await kv.delete("device_flow_pending");
                    return poeResponse([
                        sseText("❌ Device code expired. Starting new flow...\n\n"),
                        sseText("Send any message to start fresh."),
                        sseDone()
                    ]);
                } else {
                    return poeError(`Authorization error: ${pollResult.error}`);
                }
            }
            
            // Check for cached GitHub token
            let githubData = await getFromKV(kv, "github_token");
            
            // If no GitHub token, start device flow
            if (!githubData) {
                // Check if we already have a pending flow
                if (pendingFlow) {
                    return poeResponse([
                        sseText("🔐 **Device Flow In Progress**\n\n"),
                        sseText(`Visit: ${pendingFlow.verification_uri}\n`),
                        sseText(`Enter code: **${pendingFlow.user_code}**\n\n`),
                        sseText("Say **'check'** when you've authorized."),
                        sseDone()
                    ]);
                }
                
                // Start new device flow
                const deviceFlow = await startDeviceFlow();
                
                // Cache the pending flow (expires when device code expires)
                await putToKV(kv, "device_flow_pending", {
                    device_code: deviceFlow.device_code,
                    user_code: deviceFlow.user_code,
                    verification_uri: deviceFlow.verification_uri,
                    expires_at: new Date(Date.now() + deviceFlow.expires_in * 1000).toISOString(),
                }, deviceFlow.expires_in);
                
                return poeResponse([
                    sseText("🔐 **GitHub Authorization Required**\n\n"),
                    sseText(`Visit: ${deviceFlow.verification_uri}\n`),
                    sseText(`Enter code: **${deviceFlow.user_code}**\n\n`),
                    sseText("Say **'check'** when you've authorized."),
                    sseDone()
                ]);
            }
            
            // We have a GitHub token, check for cached Copilot token
            let copilotData = await getFromKV(kv, "copilot_token");
            
            // Fetch new Copilot token if needed
            if (!copilotData) {
                try {
                    const copilotResponse = await getCopilotToken(githubData.token);
                    
                    const expiresAt = new Date();
                    expiresAt.setMinutes(expiresAt.getMinutes() + COPILOT_TOKEN_LIFETIME_MINUTES);
                    
                    copilotData = {
                        token: copilotResponse.token,
                        expires_at: expiresAt.toISOString(),
                        lifetime_minutes: COPILOT_TOKEN_LIFETIME_MINUTES,
                        endpoints: copilotResponse.endpoints || null,
                    };
                    
                    await putToKV(kv, "copilot_token", copilotData, COPILOT_TOKEN_LIFETIME_MINUTES * 60);
                } catch (e) {
                    if (e.message === "INVALID_GITHUB_TOKEN") {
                        // GitHub token is invalid, clear it and start over
                        await kv.delete("github_token");
                        return poeResponse([
                            sseText("⚠️ GitHub token expired or invalid. Cleared cache.\n\n"),
                            sseText("Send any message to start device flow again."),
                            sseDone()
                        ]);
                    } else if (e.message === "NO_COPILOT_ACCESS") {
                        return poeError("Your GitHub account doesn't have Copilot access.");
                    }
                    throw e;
                }
            }
            
            // Return the token info
            const result = {
                copilot_token: copilotData.token,
                copilot_expires_at: copilotData.expires_at,
                conversation_id: conversationId,
                user_id: userId,
                github_token_expires_at: githubData.expires_at,
            };
            
            return poeResponse([
                sseText("✅ **Copilot Token Ready**\n\n"),
                sseText("```json\n"),
                sseText(JSON.stringify(result, null, 2)),
                sseText("\n```"),
                sseDone()
            ]);
            
        } catch (e) {
            console.error("Error:", e);
            return poeError(e.message || "Unknown error");
        }
    },
};
