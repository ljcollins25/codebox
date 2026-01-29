/**
 * Copilot Token Bot - Cloudflare Worker
 * 
 * A Poe server bot that handles GitHub device flow authentication
 * and returns Copilot tokens cached in Workers KV.
 */

import YAML from 'yaml';

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
 * Send a Poe SSE data event with formatted JSON text display
 * @param {object} metadata - The data to send
 * @param {boolean} includeMarkdown - Whether to wrap JSON in markdown code block
 */
function sseDataAndJson(metadata, includeMarkdown = false) {
    const jsonText = JSON.stringify(metadata, null, 2);
    if (includeMarkdown) {
        return [
            sseText("```json\n"),
            sseText(jsonText),
            sseText("\n```"),
            sseData(metadata)
        ].join('');
    }
    return [
        sseText(jsonText),
        sseData(metadata)
    ].join('');
}

/**
 * Send a Poe SSE done event
 */
function sseDone() {
    return `event: done\ndata: {}\n\n`;
}

/**
 * Send a Poe SSE suggested_reply event
 */
function sseSuggestedReply(text) {
    return `event: suggested_reply\ndata: ${JSON.stringify({ text })}\n\n`;
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
 * Create a streaming SSE response for Poe
 */
function poeStreamingResponse(streamFn) {
    const encoder = new TextEncoder();
    const stream = new ReadableStream({
        async start(controller) {
            const write = (msg) => controller.enqueue(encoder.encode(msg));
            try {
                await streamFn(write);
            } finally {
                controller.close();
            }
        }
    });
    return new Response(stream, {
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
                enable_image_comprehension: false,
                server_bot_dependencies: {},
                parameter_controls: {
                    api_version: "2",
                    sections: [
                        {
                            name: "Token Settings",
                            controls: [
                                {
                                    control: "text_field",
                                    label: "Salt",
                                    description: "Optional salt to create separate token namespaces",
                                    parameter_name: "salt",
                                    default_value: "",
                                    placeholder: "my-namespace"
                                },
                                {
                                    control: "drop_down",
                                    label: "Mode",
                                    description: "Response output mode",
                                    parameter_name: "mode",
                                    default_value: "",
                                    options: [
                                        { value: "", name: "Normal" },
                                        { value: "query_token", name: "Query Token (JSON only)" },
                                        { value: "poll_only", name: "Poll Only (device flow only)" }
                                    ]
                                },
                                {
                                    control: "toggle_switch",
                                    label: "Markdown",
                                    description: "Wrap JSON output in markdown code blocks",
                                    parameter_name: "markdown",
                                    default_value: false
                                },
                                {
                                    control: "toggle_switch",
                                    label: "Data Only",
                                    description: "Only emit SSE data event without printing text",
                                    parameter_name: "data_only",
                                    default_value: false
                                }
                            ]
                        },
                        {
                            name: "Polling",
                            collapsed_by_default: true,
                            controls: [
                                {
                                    control: "slider",
                                    label: "Poll Interval (seconds)",
                                    description: "Interval between device flow polls",
                                    parameter_name: "poll_interval_secs",
                                    default_value: 5,
                                    min_value: 5,
                                    max_value: 30,
                                    step: 1
                                },
                                {
                                    control: "slider",
                                    label: "Poll Count",
                                    description: "Number of times to poll for authorization",
                                    parameter_name: "poll_count",
                                    default_value: 1,
                                    min_value: 1,
                                    max_value: 60,
                                    step: 1
                                }
                            ]
                        }
                    ]
                }
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
        
        // Extract polling configuration and salt sent via query params or last query message (YAML/JSON)
        let pollInterval = parseInt(url.searchParams.get("poll_interval_secs")) || 0;
        let pollCount = parseInt(url.searchParams.get("poll_count")) || 1;
        let salt = url.searchParams.get("salt") || "";
        let mode = url.searchParams.get("mode") || "";
        let markdown = url.searchParams.get("markdown") === "true";
        let dataOnly = url.searchParams.get("data_only") === "true";
        let refresh = false;
        let reset = false;
        
        if (poeBody.query && poeBody.query.length > 0) {
            try {
                const lastMsg = poeBody.query[poeBody.query.length - 1];
                
                // Extract config from message parameters (settings)
                if (lastMsg.parameters) {
                    const params = lastMsg.parameters;
                    if (params.poll_interval_secs != null) pollInterval = parseInt(params.poll_interval_secs) || pollInterval;
                    if (params.poll_count != null) pollCount = parseInt(params.poll_count) || pollCount;
                    if (params.salt != null) salt = String(params.salt);
                    if (params.mode != null) mode = String(params.mode);
                    if (params.markdown === true || params.markdown === "true") markdown = true;
                    if (params.data_only === true || params.data_only === "true") dataOnly = true;
                    if (params.refresh === true || params.refresh === "true") refresh = true;
                    if (params.reset === true || params.reset === "true") reset = true;
                }
                
                if (lastMsg.content && lastMsg.content.trim()) {
                    const content = lastMsg.content.trim();
                    
                    // Check for simple text commands
                    if (content.toLowerCase() === 'refresh') {
                        refresh = true;
                    } else if (content.toLowerCase() === 'reset') {
                        reset = true;
                    } else {
                        // Try parsing as YAML/JSON config
                        const config = YAML.parse(content);
                        if (config && typeof config === 'object') {
                            if (config.poll_interval_secs != null) pollInterval = parseInt(config.poll_interval_secs) || pollInterval;
                            if (config.poll_count != null) pollCount = parseInt(config.poll_count) || pollCount;
                            if (config.salt != null) salt = String(config.salt);
                            if (config.mode != null) mode = String(config.mode);
                            if (config.markdown === true) markdown = true;
                            if (config.data_only === true) dataOnly = true;
                            if (config.refresh === true) refresh = true;
                            if (config.reset === true) reset = true;
                        }
                    }
                }
            } catch (ignore) {}
        }
        
        // Enforce minimum poll interval of 5 seconds
        if (pollInterval > 0 && pollInterval < 5) pollInterval = 5;
        
        // KV namespace binding
        const kv = env.TOKEN_CACHE;
        if (!kv) {
            return poeError("KV namespace not configured. Add TOKEN_CACHE binding.");
        }
        
        // Define versioned keys
        const suffix = salt ? `_${salt}` : "";
        const KEY_PENDING_FLOW = `device_flow_pending_${DATA_VERSION}${suffix}`;
        const KEY_GITHUB_TOKEN = `github_token_${DATA_VERSION}${suffix}`;
        const KEY_COPILOT_TOKEN = `copilot_token_${DATA_VERSION}${suffix}`;
        
        try {
            // Show request context first
            const contextLines = [
                sseText("📋 **Request Info**\n\n"),
                sseText(`• Conversation ID: \`${conversationId || 'N/A'}\`\n`),
                sseText(`• User ID: \`${userId || 'N/A'}\`\n\n`),
            ];
            
            // Handle reset command - clear all tokens and start fresh
            if (reset) {
                await kv.delete(KEY_PENDING_FLOW);
                await kv.delete(KEY_GITHUB_TOKEN);
                await kv.delete(KEY_COPILOT_TOKEN);
                // Fall through to start device flow from scratch
            }
            
            // Check for pending device flow
            const pendingFlow = await getFromKV(kv, KEY_PENDING_FLOW);
            
            // Check for cached GitHub token
            let githubData = await getFromKV(kv, KEY_GITHUB_TOKEN);
            
            // If no GitHub token, handle device flow
            if (!githubData) {
                // Check if we already have a pending flow - try to poll it
                if (pendingFlow && pollCount > 0) {
                    // query_token or poll_only mode: poll silently and return JSON only
                    if (mode === "query_token" || mode === "poll_only") {
                        let pollResult;
                        let success = false;
                        
                        for (let i = 0; i < pollCount; i++) {
                            pollResult = await pollDeviceFlow(pendingFlow.device_code);
                            
                            if (pollResult.access_token) {
                                success = true;
                                break;
                            } else if (pollResult.error === "slow_down") {
                                pollInterval = Math.max(pollInterval, 5) + 5;
                            } else if (pollResult.error && pollResult.error !== "authorization_pending") {
                                // Fatal error
                                if (pollResult.error === "expired_token") {
                                    await kv.delete(KEY_PENDING_FLOW);
                                }
                                return poeResponse([
                                    dataOnly ? sseData({
                                        status: pollResult.error,
                                        user_code: pendingFlow.user_code,
                                        verification_uri: pendingFlow.verification_uri
                                    }) : sseDataAndJson({
                                        status: pollResult.error,
                                        user_code: pendingFlow.user_code,
                                        verification_uri: pendingFlow.verification_uri
                                    }, markdown),
                                    sseDone()
                                ]);
                            }
                            
                            if (i < pollCount - 1) {
                                await new Promise(r => setTimeout(r, pollInterval * 1000));
                            }
                        }
                        
                        if (!success) {
                            return poeResponse([
                                dataOnly ? sseData({
                                    status: "authorization_pending",
                                    user_code: pendingFlow.user_code,
                                    verification_uri: pendingFlow.verification_uri
                                }) : sseDataAndJson({
                                    status: "authorization_pending",
                                    user_code: pendingFlow.user_code,
                                    verification_uri: pendingFlow.verification_uri
                                }, markdown),
                                sseDone()
                            ]);
                        }
                        
                        // Success - store GitHub token
                        const expiresAt = new Date();
                        expiresAt.setDate(expiresAt.getDate() + GITHUB_TOKEN_LIFETIME_DAYS);
                        
                        githubData = {
                            token: pollResult.access_token,
                            expires_at: expiresAt.toISOString(),
                            acquired_at: new Date().toISOString(),
                            lifetime_days: GITHUB_TOKEN_LIFETIME_DAYS,
                        };
                        
                        await putToKV(kv, KEY_GITHUB_TOKEN, githubData, GITHUB_TOKEN_LIFETIME_DAYS * 24 * 60 * 60);
                        await kv.delete(KEY_PENDING_FLOW);
                        
                        // poll_only mode: return GitHub token info only, don't fetch Copilot token
                        if (mode === "poll_only") {
                            const result = {
                                status: "github_token_acquired",
                                version: DATA_VERSION,
                                current_time_utc: new Date().toISOString(),
                                github_token: githubData.token,
                                github_token_expires_at: githubData.expires_at,
                                github_token_acquired_at: githubData.acquired_at,
                                conversation_id: conversationId,
                                user_id: userId,
                            };
                            return poeResponse([
                                sseData(result),
                                sseDone()
                            ]);
                        }
                        
                        // query_token mode: Fall through to fetch Copilot token below
                    } else {
                        // Normal streaming mode
                        return poeStreamingResponse(async (write) => {
                            // Send context first
                            for (const line of contextLines) write(line);
                            
                            write(sseText("🔐 **Device Flow In Progress**\n\n"));
                            write(sseText(`Visit: ${pendingFlow.verification_uri}\n`));
                            write(sseText(`Enter code: **${pendingFlow.user_code}**\n\n`));
                            write(sseText(`Polling for authorization (${pollCount} attempts, ${pollInterval}s interval)...\n\n`));
                            
                            let pollResult;
                            let success = false;
                            
                            // Poll with configured retries
                            for (let i = 0; i < pollCount; i++) {
                                write(sseText(`⏳ Poll ${i + 1}/${pollCount}... `));
                                pollResult = await pollDeviceFlow(pendingFlow.device_code);
                                
                                if (pollResult.access_token) {
                                    write(sseText("✅ Authorized!\n\n"));
                                    success = true;
                                    break;
                                } else if (pollResult.error === "authorization_pending") {
                                    write(sseText("⏸️ Pending\n"));
                                } else if (pollResult.error === "slow_down") {
                                    write(sseText("🐢 Rate limited, slowing down\n"));
                                    pollInterval = Math.max(pollInterval, 5) + 5;
                                } else if (pollResult.error === "expired_token") {
                                    write(sseText("❌ Device code expired\n\n"));
                                    await kv.delete(KEY_PENDING_FLOW);
                                    write(sseText("Send another message to start a new device flow."));
                                    write(sseDone());
                                    return;
                                } else if (pollResult.error) {
                                    write(sseText(`❌ Error: ${pollResult.error}\n`));
                                    write(sseDone());
                                    return;
                                }
                                
                                // Wait before next retry if not last attempt
                                if (i < pollCount - 1) {
                                    await new Promise(r => setTimeout(r, pollInterval * 1000));
                                }
                            }
                            
                            if (success && pollResult.access_token) {
                                // Store the GitHub token
                                const expiresAt = new Date();
                                expiresAt.setDate(expiresAt.getDate() + GITHUB_TOKEN_LIFETIME_DAYS);
                                
                                githubData = {
                                    token: pollResult.access_token,
                                    expires_at: expiresAt.toISOString(),
                                    acquired_at: new Date().toISOString(),
                                    lifetime_days: GITHUB_TOKEN_LIFETIME_DAYS,
                                };
                                
                                await putToKV(kv, KEY_GITHUB_TOKEN, githubData, GITHUB_TOKEN_LIFETIME_DAYS * 24 * 60 * 60);
                                await kv.delete(KEY_PENDING_FLOW);
                                
                                // Now fetch Copilot token
                                write(sseText("🔄 Fetching Copilot token...\n\n"));
                                
                                try {
                                    const copilotResponse = await getCopilotToken(githubData.token);
                                    
                                    const copilotExpiresAt = new Date();
                                    copilotExpiresAt.setMinutes(copilotExpiresAt.getMinutes() + COPILOT_TOKEN_LIFETIME_MINUTES);
                                    
                                    const copilotData = {
                                        token: copilotResponse.token,
                                        expires_at: copilotExpiresAt.toISOString(),
                                        acquired_at: new Date().toISOString(),
                                        lifetime_minutes: COPILOT_TOKEN_LIFETIME_MINUTES,
                                        endpoints: copilotResponse.endpoints || null,
                                    };
                                    
                                    await putToKV(kv, KEY_COPILOT_TOKEN, copilotData, COPILOT_TOKEN_LIFETIME_MINUTES * 60);
                                    
                                    const result = {
                                        status: "acquired",
                                        version: DATA_VERSION,
                                        current_time_utc: new Date().toISOString(),
                                        copilot_token: copilotData.token,
                                        copilot_expires_at: copilotData.expires_at,
                                        copilot_acquired_at: copilotData.acquired_at,
                                        conversation_id: conversationId,
                                        user_id: userId,
                                        github_token_expires_at: githubData.expires_at,
                                        github_token_acquired_at: githubData.acquired_at,
                                    };
                                    
                                    write(sseText("✅ **Copilot Token Ready**\n\n"));
                                    write(dataOnly ? sseData(result) : sseDataAndJson(result, markdown));
                                    write(sseSuggestedReply("refresh"));
                                    write(sseSuggestedReply("reset"));
                                } catch (e) {
                                    if (e.message === "NO_COPILOT_ACCESS") {
                                        write(sseText("❌ Your GitHub account doesn't have Copilot access."));
                                    } else {
                                        write(sseText(`❌ Error: ${e.message}`));
                                    }
                                }
                            } else {
                                write(sseText("\n⏸️ Authorization still pending. Send another message to continue polling."));
                            }
                            
                            write(sseDone());
                        });
                    }
                } else if (pendingFlow) {
                    // No polling configured, just show the pending flow info
                    if (mode === "query_token" || mode === "poll_only") {
                        return poeResponse([
                            dataOnly ? sseData({
                                status: "authorization_pending",
                                user_code: pendingFlow.user_code,
                                verification_uri: pendingFlow.verification_uri
                            }) : sseDataAndJson({
                                status: "authorization_pending",
                                user_code: pendingFlow.user_code,
                                verification_uri: pendingFlow.verification_uri
                            }, markdown),
                            sseDone()
                        ]);
                    }
                    return poeResponse([
                        ...contextLines,
                        sseText("🔐 **Device Flow In Progress**\n\n"),
                        sseText(`Visit: ${pendingFlow.verification_uri}\n`),
                        sseText(`Enter code: **${pendingFlow.user_code}**\n\n`),
                        sseText("Send another message after authorizing."),
                        sseDone()
                    ]);
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
                    
                    if (mode === "query_token" || mode === "poll_only") {
                        return poeResponse([
                            dataOnly ? sseData({
                                status: "authorization_pending",
                                user_code: deviceFlow.user_code,
                                verification_uri: deviceFlow.verification_uri
                            }) : sseDataAndJson({
                                status: "authorization_pending",
                                user_code: deviceFlow.user_code,
                                verification_uri: deviceFlow.verification_uri
                            }, markdown),
                            sseDone()
                        ]);
                    }
                    
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
            
            // We have a GitHub token
            
            // poll_only mode: return GitHub token info only, don't fetch Copilot token
            if (mode === "poll_only") {
                const result = {
                    status: "github_token_acquired",
                    version: DATA_VERSION,
                    current_time_utc: new Date().toISOString(),
                    github_token: githubData.token,
                    github_token_expires_at: githubData.expires_at,
                    github_token_acquired_at: githubData.acquired_at,
                    conversation_id: conversationId,
                    user_id: userId,
                };
                return poeResponse([
                    sseData(result),
                    sseDone()
                ]);
            }
            
            // Check for cached Copilot token
            let copilotData = await getFromKV(kv, KEY_COPILOT_TOKEN);
            
            // Handle refresh command - delete cached Copilot token to force refresh
            if (refresh && copilotData) {
                await kv.delete(KEY_COPILOT_TOKEN);
                copilotData = null;
            }
            
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
                status: "acquired",
                version: DATA_VERSION,
                current_time_utc: new Date().toISOString(),
                copilot_token: copilotData.token,
                copilot_expires_at: copilotData.expires_at,
                copilot_acquired_at: copilotData.acquired_at,
                conversation_id: conversationId,
                user_id: userId,
                github_token_expires_at: githubData.expires_at,
                github_token_acquired_at: githubData.acquired_at,
            };
            
            // query_token mode: return data event only
            if (mode === "query_token") {
                return poeResponse([
                    dataOnly ? sseData(result) : sseDataAndJson(result, markdown),
                    sseDone()
                ]);
            }
            
            // Return the token info with both text display and data event
            return poeResponse([
                ...contextLines,
                sseText("✅ **Copilot Token Ready**\n\n"),
                dataOnly ? sseData(result) : sseDataAndJson(result, markdown),
                sseSuggestedReply("refresh"),
                sseSuggestedReply("reset"),
                sseDone()
            ]);
            
        } catch (e) {
            console.error("Error:", e);
            return poeError(e.message || "Unknown error");
        }
    },
};
