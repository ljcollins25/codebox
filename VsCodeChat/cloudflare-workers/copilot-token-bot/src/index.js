/**
 * Copilot Token Bot - Cloudflare Worker
 * 
 * A Poe server bot that handles GitHub device flow authentication
 * and returns Copilot tokens cached in Workers KV.
 */

import YAML from 'yaml';

// =============================================================================
// Constants
// =============================================================================

const GITHUB_CLIENT_ID = "01ab8ac9400c4e429b23";
const GITHUB_SCOPES = "read:user";
const GITHUB_TOKEN_LIFETIME_DAYS = 30;
const COPILOT_TOKEN_LIFETIME_MINUTES = 25;
const DATA_VERSION = "v1";

// =============================================================================
// Main Handler (Entry Point)
// =============================================================================

export default {
    async fetch(request, env, ctx) {
        const handler = new CopilotTokenHandler(request, env);
        return handler.handle();
    },
};

// =============================================================================
// Request Handler Class
// =============================================================================

class CopilotTokenHandler {
    constructor(request, env) {
        this.request = request;
        this.env = env;
        this.url = new URL(request.url);
        this.config = null;
        this.kv = null;
        this.keys = null;
    }

    /**
     * Main request handler - dispatches to appropriate action
     */
    async handle() {
        // Handle health check
        if (this.request.method === "GET" && this.url.pathname === "/") {
            return new Response("Copilot Token Bot OK");
        }

        // Only handle POST to root
        if (this.request.method !== "POST" || this.url.pathname !== "/") {
            return new Response("Not Found", { status: 404 });
        }

        // Validate authorization
        const authResult = this.validateAuthorization();
        if (authResult) return authResult;

        // Parse request body
        const bodyResult = await this.parseRequestBody();
        if (bodyResult.error) return bodyResult.error;
        this.poeBody = bodyResult.body;

        // Handle special request types
        if (this.poeBody.type === "settings") {
            return this.getSettingsResponse();
        }
        if (this.poeBody.type === "report_error") {
            return this.handleReportError();
        }

        // Parse configuration from various sources
        this.config = this.parseConfig();

        // Validate KV binding
        this.kv = this.env.TOKEN_CACHE;
        if (!this.kv) {
            return poeError("KV namespace not configured. Add TOKEN_CACHE binding.");
        }

        // Setup versioned KV keys
        this.keys = this.getKVKeys();

        // Process the token request
        return this.processTokenRequest();
    }

    // =========================================================================
    // Authorization & Validation
    // =========================================================================

    validateAuthorization() {
        const authSecret = this.env.AUTH_SECRET;
        if (!authSecret) {
            return new Response("AUTH_SECRET not configured", { status: 500 });
        }

        const authHeader = this.request.headers.get("Authorization");
        const queryKey = this.url.searchParams.get("key");

        const isHeaderValid = authHeader === `Bearer ${authSecret}` || authHeader === authSecret;
        const isQueryValid = queryKey === authSecret;

        if (!isHeaderValid && !isQueryValid) {
            return new Response("Unauthorized", { status: 401 });
        }

        return null; // Authorized
    }

    async parseRequestBody() {
        const shouldTrace = this.url.searchParams.get("trace") === "true";
        const bodyText = await this.request.text();

        if (shouldTrace) {
            console.log("Trace Request:");
            console.log("Method:", this.request.method);
            console.log("URL:", this.request.url);
            console.log("Headers:", JSON.stringify(Object.fromEntries(this.request.headers)));
            console.log("Body:", bodyText);
        }

        try {
            return { body: bodyText ? JSON.parse(bodyText) : {} };
        } catch (e) {
            return { error: poeError("Invalid request body") };
        }
    }

    // =========================================================================
    // Settings Response
    // =========================================================================

    getSettingsResponse() {
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
                                default_value: "query_token",
                                options: [
                                    { value: "query_token", name: "Query Token (JSON only)" },
                                    { value: "auth_flow", name: "Auth Flow (user-friendly messages)" },
                                    { value: "dev", name: "Dev (full debug output)" }
                                ]
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

    handleReportError() {
        console.error("Poe reported error:", this.poeBody);
        return new Response(JSON.stringify({}), {
            headers: { "Content-Type": "application/json" }
        });
    }

    // =========================================================================
    // Configuration Parsing
    // =========================================================================

    parseConfig() {
        const url = this.url;
        const poeBody = this.poeBody;

        // Start with defaults from query params (query_token is default mode)
        const config = {
            pollInterval: parseInt(url.searchParams.get("poll_interval_secs")) || 0,
            pollCount: parseInt(url.searchParams.get("poll_count")) || 1,
            salt: url.searchParams.get("salt") || "",
            mode: url.searchParams.get("mode") || "query_token",
            refresh: false,
            reset: false,
            conversationId: poeBody.conversation_id || null,
            userId: poeBody.user_id || null,
        };

        // Override with message parameters and content
        if (poeBody.query && poeBody.query.length > 0) {
            const lastMsg = poeBody.query[poeBody.query.length - 1];
            this.applyMessageParameters(config, lastMsg);
            this.applyMessageContent(config, lastMsg);
        }

        // Enforce minimum poll interval
        if (config.pollInterval > 0 && config.pollInterval < 5) {
            config.pollInterval = 5;
        }

        return config;
    }

    applyMessageParameters(config, lastMsg) {
        if (!lastMsg.parameters) return;

        const params = lastMsg.parameters;
        if (params.poll_interval_secs != null) config.pollInterval = parseInt(params.poll_interval_secs) || config.pollInterval;
        if (params.poll_count != null) config.pollCount = parseInt(params.poll_count) || config.pollCount;
        if (params.salt != null) config.salt = String(params.salt);
        if (params.mode != null) config.mode = String(params.mode);
        if (params.refresh === true || params.refresh === "true") config.refresh = true;
        if (params.reset === true || params.reset === "true") config.reset = true;
    }

    applyMessageContent(config, lastMsg) {
        if (!lastMsg.content || !lastMsg.content.trim()) return;

        const content = lastMsg.content.trim();

        // Check for simple text commands
        if (content.toLowerCase() === 'refresh') {
            config.refresh = true;
            return;
        }
        if (content.toLowerCase() === 'reset') {
            config.reset = true;
            return;
        }

        // Try parsing as YAML/JSON config
        try {
            const parsed = YAML.parse(content);
            if (parsed && typeof parsed === 'object') {
                if (parsed.poll_interval_secs != null) config.pollInterval = parseInt(parsed.poll_interval_secs) || config.pollInterval;
                if (parsed.poll_count != null) config.pollCount = parseInt(parsed.poll_count) || config.pollCount;
                if (parsed.salt != null) config.salt = String(parsed.salt);
                if (parsed.mode != null) config.mode = String(parsed.mode);
                if (parsed.refresh === true) config.refresh = true;
                if (parsed.reset === true) config.reset = true;
            }
        } catch (ignore) {}
    }

    getKVKeys() {
        const suffix = this.config.salt ? `_${this.config.salt}` : "";
        return {
            pendingFlow: `device_flow_pending_${DATA_VERSION}${suffix}`,
            githubToken: `github_token_${DATA_VERSION}${suffix}`,
            copilotToken: `copilot_token_${DATA_VERSION}${suffix}`,
            lastPoll: `last_poll_${DATA_VERSION}${suffix}`,
        };
    }

    // =========================================================================
    // Token Request Processing
    // =========================================================================

    async processTokenRequest() {
        try {
            // Handle reset command
            if (this.config.reset) {
                await this.clearAllTokens();
            }

            // Load cached tokens
            const pendingFlow = await getFromKV(this.kv, this.keys.pendingFlow);
            let githubData = await getFromKV(this.kv, this.keys.githubToken);

            // No GitHub token - handle device flow
            if (!githubData) {
                return this.handleDeviceFlow(pendingFlow);
            }

            // Have GitHub token - handle token retrieval
            return this.handleTokenRetrieval(githubData);

        } catch (e) {
            console.error("Error:", e);
            return poeError(e.message || "Unknown error");
        }
    }

    async clearAllTokens() {
        await this.kv.delete(this.keys.pendingFlow);
        await this.kv.delete(this.keys.githubToken);
        await this.kv.delete(this.keys.copilotToken);
    }

    // =========================================================================
    // Device Flow Handling
    // =========================================================================

    async shouldSkipPoll() {
        const { pollInterval } = this.config;
        const lastPollData = await getFromKV(this.kv, this.keys.lastPoll);
        
        if (lastPollData && lastPollData.timestamp) {
            const elapsed = (Date.now() - lastPollData.timestamp) / 1000;
            if (elapsed < pollInterval) {
                return { skip: true, waitSeconds: Math.ceil(pollInterval - elapsed) };
            }
        }
        return { skip: false };
    }

    async recordPollTimestamp() {
        await putToKV(this.kv, this.keys.lastPoll, { timestamp: Date.now() }, 60);
    }

    async clearPollTimestamp() {
        await this.kv.delete(this.keys.lastPoll);
    }

    async handleDeviceFlow(pendingFlow) {
        const { mode, pollCount } = this.config;

        // Have pending flow with polling configured
        if (pendingFlow && pollCount > 0) {
            if (mode === "query_token") {
                return this.pollDeviceFlowQuiet(pendingFlow);
            }
            // auth_flow and dev modes use streaming
            return this.pollDeviceFlowStreaming(pendingFlow);
        }

        // Have pending flow but no polling
        if (pendingFlow) {
            return this.showPendingFlowInfo(pendingFlow);
        }

        // Start new device flow
        return this.startNewDeviceFlow();
    }

    async pollDeviceFlowQuiet(pendingFlow) {
        let { pollInterval, pollCount } = this.config;

        let pollResult;
        let success = false;
        let actuallyPolled = false;

        for (let i = 0; i < pollCount; i++) {
            // Check if we polled too recently (but always allow at least one poll attempt)
            const skipCheck = await this.shouldSkipPoll();
            if (skipCheck.skip && actuallyPolled) {
                // Already polled this request, skip remaining
                if (i < pollCount - 1) {
                    await new Promise(r => setTimeout(r, pollInterval * 1000));
                }
                continue;
            }

            actuallyPolled = true;
            pollResult = await pollDeviceFlow(pendingFlow.device_code);

            if (pollResult.access_token) {
                success = true;
                await this.clearPollTimestamp();
                break;
            } else if (pollResult.error === "slow_down") {
                pollInterval = Math.max(pollInterval, 5) + 5;
                await this.recordPollTimestamp();
            } else if (pollResult.error === "authorization_pending") {
                await this.recordPollTimestamp();
            } else if (pollResult.error) {
                if (pollResult.error === "expired_token") {
                    await this.kv.delete(this.keys.pendingFlow);
                    await this.clearPollTimestamp();
                }
                return this.respondWithJson({
                    status: pollResult.error,
                    user_code: pendingFlow.user_code,
                    verification_uri: pendingFlow.verification_uri
                });
            }

            if (i < pollCount - 1) {
                await new Promise(r => setTimeout(r, pollInterval * 1000));
            }
        }

        if (!success) {
            return this.respondWithJson({
                status: "authorization_pending",
                user_code: pendingFlow.user_code,
                verification_uri: pendingFlow.verification_uri
            });
        }

        // Success - store GitHub token and fetch Copilot token
        const githubData = await this.storeGitHubToken(pollResult.access_token);
        return this.handleTokenRetrieval(githubData);
    }

    async pollDeviceFlowStreaming(pendingFlow) {
        const { mode } = this.config;
        let { pollInterval, pollCount } = this.config;

        return poeStreamingResponse(async (write) => {
            // dev mode shows context info
            if (mode === "dev") {
                for (const line of this.getContextLines()) write(line);
            }

            write(sseText("🔐 **Device Flow In Progress**\n\n"));
            write(sseText(`Visit: ${pendingFlow.verification_uri}\n`));
            write(sseText(`Enter code: **${pendingFlow.user_code}**\n\n`));
            write(sseText(`Polling for authorization (${pollCount} attempts, ${pollInterval}s interval)...\n\n`));

            let pollResult;
            let success = false;
            let actuallyPolled = false;

            for (let i = 0; i < pollCount; i++) {
                // Check if we polled too recently (but always allow at least one poll attempt)
                const skipCheck = await this.shouldSkipPoll();
                if (skipCheck.skip && actuallyPolled) {
                    write(sseText(`⏳ Poll ${i + 1}/${pollCount}... ⏸️ Skipped (wait ${skipCheck.waitSeconds}s)\n`));
                    if (i < pollCount - 1) {
                        await new Promise(r => setTimeout(r, pollInterval * 1000));
                    }
                    continue;
                }

                actuallyPolled = true;
                write(sseText(`⏳ Poll ${i + 1}/${pollCount}... `));
                pollResult = await pollDeviceFlow(pendingFlow.device_code);

                if (pollResult.access_token) {
                    write(sseText("✅ Authorized!\n\n"));
                    success = true;
                    await this.clearPollTimestamp();
                    break;
                } else if (pollResult.error === "authorization_pending") {
                    write(sseText("⏸️ Pending\n"));
                    await this.recordPollTimestamp();
                } else if (pollResult.error === "slow_down") {
                    write(sseText("🐢 Rate limited, slowing down\n"));
                    pollInterval = Math.max(pollInterval, 5) + 5;
                    await this.recordPollTimestamp();
                } else if (pollResult.error === "expired_token") {
                    write(sseText("❌ Device code expired\n\n"));
                    await this.kv.delete(this.keys.pendingFlow);
                    await this.clearPollTimestamp();
                    write(sseText("Send another message to start a new device flow."));
                    write(sseDone());
                    return;
                } else if (pollResult.error) {
                    write(sseText(`❌ Error: ${pollResult.error}\n`));
                    write(sseDone());
                    return;
                }

                if (i < pollCount - 1) {
                    await new Promise(r => setTimeout(r, pollInterval * 1000));
                }
            }

            if (success && pollResult.access_token) {
                const githubData = await this.storeGitHubToken(pollResult.access_token);
                
                if (mode === "auth_flow") {
                    // auth_flow: just confirm GitHub auth succeeded, don't fetch Copilot token
                    write(sseText("✅ **GitHub Authorization Complete**"));
                } else {
                    // dev mode: fetch and show Copilot token
                    await this.fetchAndWriteCopilotToken(write, githubData);
                }
            } else {
                write(sseText("\n⏸️ Authorization still pending. Send another message to continue polling."));
            }

            write(sseDone());
        });
    }

    async fetchAndWriteCopilotToken(write, githubData) {
        const { mode } = this.config;

        write(sseText("🔄 Fetching Copilot token...\n\n"));

        try {
            const copilotData = await this.fetchAndStoreCopilotToken(githubData.token);
            
            if (mode === "auth_flow") {
                // auth_flow: just show success message, no JSON
                write(sseText("✅ **Copilot Token Ready**\n\nToken has been cached. Use query_token mode to retrieve it."));
            } else {
                // dev mode: show full JSON output
                const result = this.buildCopilotResult(copilotData, githubData);
                write(sseText("✅ **Copilot Token Ready**\n\n"));
                write(sseDataAndJson(result, false));
                write(sseSuggestedReply("refresh"));
                write(sseSuggestedReply("reset"));
            }
        } catch (e) {
            if (e.message === "NO_COPILOT_ACCESS") {
                write(sseText("❌ Your GitHub account doesn't have Copilot access."));
            } else {
                write(sseText(`❌ Error: ${e.message}`));
            }
        }
    }

    showPendingFlowInfo(pendingFlow) {
        const { mode } = this.config;

        if (mode === "query_token") {
            return this.respondWithJson({
                status: "authorization_pending",
                user_code: pendingFlow.user_code,
                verification_uri: pendingFlow.verification_uri
            });
        }

        // auth_flow and dev modes show user-friendly message
        const messages = [];
        if (mode === "dev") {
            messages.push(...this.getContextLines());
        }
        messages.push(
            sseText("🔐 **Device Flow In Progress**\n\n"),
            sseText(`Visit: ${pendingFlow.verification_uri}\n`),
            sseText(`Enter code: **${pendingFlow.user_code}**\n\n`),
            sseText("Send another message after authorizing."),
            sseDone()
        );
        return poeResponse(messages);
    }

    async startNewDeviceFlow() {
        const { mode } = this.config;

        const deviceFlow = await startDeviceFlow();

        await putToKV(this.kv, this.keys.pendingFlow, {
            device_code: deviceFlow.device_code,
            user_code: deviceFlow.user_code,
            verification_uri: deviceFlow.verification_uri,
            expires_at: new Date(Date.now() + deviceFlow.expires_in * 1000).toISOString(),
        }, deviceFlow.expires_in);

        if (mode === "query_token") {
            return this.respondWithJson({
                status: "authorization_pending",
                user_code: deviceFlow.user_code,
                verification_uri: deviceFlow.verification_uri
            });
        }

        // auth_flow and dev modes show user-friendly message
        const messages = [];
        if (mode === "dev") {
            messages.push(...this.getContextLines());
        }
        messages.push(
            sseText("🔐 **GitHub Authorization Required**\n\n"),
            sseText(`Visit: ${deviceFlow.verification_uri}\n`),
            sseText(`Enter code: **${deviceFlow.user_code}**\n\n`),
            sseText("Send another message after authorizing."),
            sseDone()
        );
        return poeResponse(messages);
    }

    // =========================================================================
    // Token Retrieval
    // =========================================================================

    async handleTokenRetrieval(githubData) {
        const { mode, refresh } = this.config;

        // Get or refresh Copilot token
        let copilotData = await getFromKV(this.kv, this.keys.copilotToken);

        if (refresh && copilotData) {
            await this.kv.delete(this.keys.copilotToken);
            copilotData = null;
        }

        if (!copilotData) {
            try {
                copilotData = await this.fetchAndStoreCopilotToken(githubData.token);
            } catch (e) {
                if (e.message === "INVALID_GITHUB_TOKEN") {
                    await this.kv.delete(this.keys.githubToken);
                    if (mode === "query_token") {
                        return this.respondWithJson({
                            status: "invalid_github_token",
                            message: "GitHub token expired or invalid. Send another message to start device flow again."
                        });
                    }
                    const messages = [];
                    if (mode === "dev") {
                        messages.push(...this.getContextLines());
                    }
                    messages.push(
                        sseText("⚠️ GitHub token expired or invalid. Cleared cache.\n\n"),
                        sseText("Send another message to start device flow again."),
                        sseDone()
                    );
                    return poeResponse(messages);
                } else if (e.message === "NO_COPILOT_ACCESS") {
                    if (mode === "query_token") {
                        return this.respondWithJson({
                            status: "no_copilot_access",
                            message: "Your GitHub account doesn't have Copilot access."
                        });
                    }
                    return poeError("Your GitHub account doesn't have Copilot access.");
                }
                throw e;
            }
        }

        return this.respondWithCopilotToken(copilotData, githubData);
    }

    async fetchAndStoreCopilotToken(githubToken) {
        const copilotResponse = await getCopilotToken(githubToken);

        const expiresAt = new Date();
        expiresAt.setMinutes(expiresAt.getMinutes() + COPILOT_TOKEN_LIFETIME_MINUTES);

        const copilotData = {
            token: copilotResponse.token,
            expires_at: expiresAt.toISOString(),
            acquired_at: new Date().toISOString(),
            lifetime_minutes: COPILOT_TOKEN_LIFETIME_MINUTES,
            endpoints: copilotResponse.endpoints || null,
        };

        await putToKV(this.kv, this.keys.copilotToken, copilotData, COPILOT_TOKEN_LIFETIME_MINUTES * 60);
        return copilotData;
    }

    async storeGitHubToken(accessToken) {
        const expiresAt = new Date();
        expiresAt.setDate(expiresAt.getDate() + GITHUB_TOKEN_LIFETIME_DAYS);

        const githubData = {
            token: accessToken,
            expires_at: expiresAt.toISOString(),
            acquired_at: new Date().toISOString(),
            lifetime_days: GITHUB_TOKEN_LIFETIME_DAYS,
        };

        await putToKV(this.kv, this.keys.githubToken, githubData, GITHUB_TOKEN_LIFETIME_DAYS * 24 * 60 * 60);
        await this.kv.delete(this.keys.pendingFlow);

        return githubData;
    }

    // =========================================================================
    // Response Builders
    // =========================================================================

    getContextLines() {
        const { conversationId, userId } = this.config;
        return [
            sseText("📋 **Request Info**\n\n"),
            sseText(`• Conversation ID: \`${conversationId || 'N/A'}\`\n`),
            sseText(`• User ID: \`${userId || 'N/A'}\`\n\n`),
        ];
    }

    respondWithJson(data) {
        // Returns JSON as text for user to see, plus data event for programmatic access
        const jsonText = JSON.stringify(data, null, 2);
        return poeResponse([
            sseText(jsonText),
            sseData(data),
            sseDone()
        ]);
    }

    respondWithCopilotToken(copilotData, githubData) {
        const { mode } = this.config;
        const result = this.buildCopilotResult(copilotData, githubData);

        if (mode === "query_token") {
            // query_token: plain JSON only
            return this.respondWithJson(result);
        }

        if (mode === "auth_flow") {
            // auth_flow: just show success message, no JSON
            return poeResponse([
                sseText("✅ **Copilot Token Ready**\n\nToken has been cached."),
                sseDone()
            ]);
        }

        // dev mode: full output with context and JSON
        return poeResponse([
            ...this.getContextLines(),
            sseText("✅ **Copilot Token Ready**\n\n"),
            sseDataAndJson(result, false),
            sseSuggestedReply("refresh"),
            sseSuggestedReply("reset"),
            sseDone()
        ]);
    }

    buildCopilotResult(copilotData, githubData) {
        const { mode, conversationId, userId } = this.config;
        
        const result = {
            status: "acquired",
            version: DATA_VERSION,
            current_time_utc: new Date().toISOString(),
            copilot_token: copilotData.token,
            copilot_expires_at: copilotData.expires_at,
            copilot_acquired_at: copilotData.acquired_at,
            github_token_expires_at: githubData.expires_at,
            github_token_acquired_at: githubData.acquired_at,
        };

        // Only include debug info in dev mode
        if (mode === "dev") {
            result.conversation_id = conversationId;
            result.user_id = userId;
        }

        return result;
    }
}

// =============================================================================
// SSE Response Helpers
// =============================================================================

function sseText(text) {
    return `event: text\ndata: ${JSON.stringify({ text })}\n\n`;
}

function sseData(metadata) {
    return `event: data\ndata: ${JSON.stringify({ metadata: JSON.stringify(metadata) })}\n\n`;
}

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

function sseDone() {
    return `event: done\ndata: {}\n\n`;
}

function sseSuggestedReply(text) {
    return `event: suggested_reply\ndata: ${JSON.stringify({ text })}\n\n`;
}

function poeResponse(messages) {
    const encoder = new TextEncoder();
    const body = messages.join('');
    return new Response(encoder.encode(body), {
        headers: { "Content-Type": "text/event-stream" },
    });
}

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

function poeError(message) {
    return poeResponse([
        sseText(`❌ Error: ${message}`),
        sseDone()
    ]);
}

// =============================================================================
// GitHub API Helpers
// =============================================================================

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

    return response.json();
}

async function pollDeviceFlow(deviceCode) {
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

    return response.json();
}

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

    return response.json();
}

// =============================================================================
// KV Storage Helpers
// =============================================================================

async function getFromKV(kv, key) {
    const data = await kv.get(key, "json");
    if (!data) return null;

    if (data.expires_at && new Date(data.expires_at) < new Date()) {
        await kv.delete(key);
        return null;
    }

    return data;
}

async function putToKV(kv, key, data, expirationTtl) {
    await kv.put(key, JSON.stringify(data), { expirationTtl });
}
