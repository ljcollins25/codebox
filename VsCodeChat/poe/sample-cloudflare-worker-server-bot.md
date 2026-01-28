Here's the minimal "Hello World" Poe server bot for Cloudflare Workers:

```javascript
// worker.js

export default {
  async fetch(request) {
    const url = new URL(request.url);

    if (request.method === "POST" && url.pathname === "/") {
      // Stream "Hello World" response
      const { readable, writable } = new TransformStream();
      const writer = writable.getWriter();
      const encoder = new TextEncoder();

      (async () => {
        await writer.write(encoder.encode('event: text\ndata: {"text": "Hello World!"}\n\n'));
        await writer.write(encoder.encode('event: done\ndata: {}\n\n'));
        await writer.close();
      })();

      return new Response(readable, {
        headers: { "Content-Type": "text/event-stream" },
      });
    }

    return new Response("OK");
  },
};
```

---

## wrangler.toml

```toml
name = "hello-world-bot"
main = "worker.js"
compatibility_date = "2024-01-01"
```

---

## Deploy

```bash
wrangler deploy
```

---

That's it! Register the Worker URL at [creator.poe.com](https://creator.poe.com) as a server bot, and it will respond "Hello World!" to every message.