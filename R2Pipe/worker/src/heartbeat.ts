/** Fixed wire contract shared by every hibernatable pipe socket.
 * Runtime auto-response preserves hibernation; old clients already send these
 * strings. Keep handler fallbacks for compatibility with legacy runtimes. */
export const PIPE_PING = "ping";
export const PIPE_PONG = "pong";
export function pipeAutoResponse(ctx: DurableObjectState) {
  ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair(PIPE_PING, PIPE_PONG));
}
