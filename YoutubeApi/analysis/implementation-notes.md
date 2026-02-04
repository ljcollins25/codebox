# Implementation Notes

## Decisions & Rationale

### Using `new Function()` instead of `eval()`

For executing extracted decipher and n-transform functions, we use `new Function()`:
- Slightly safer than direct `eval()`
- Creates function in global scope, avoiding scope leakage
- Better performance for repeated calls

```javascript
// Preferred
const decipherFn = new Function('sig', functionBody);

// Avoid
eval(`var result = (function(sig) { ${functionBody} })(signature)`);
```

### Cookie Storage Format

Storing cookies as a single string (like HTTP Cookie header) rather than parsed object:
- Simpler to pass directly to fetch requests
- Preserves original format
- Easy to update with new Set-Cookie values

### Error Recovery Strategy

1. **Extraction failures**: Return partial data when possible
2. **Signature failures**: Try multiple regex patterns before failing
3. **Cookie expiry**: Clear indication to client, prompt re-login

---

## Known Challenges

### 1. YouTube's Anti-Bot Measures

YouTube may:
- Require CAPTCHA
- Block datacenter IPs
- Detect headless/automated access

Mitigations:
- Use authenticated requests (cookies)
- Proper User-Agent
- Rate limiting
- Consider residential proxy for heavy use

### 2. Signature Algorithm Changes

YouTube frequently updates base.js. Our patterns must be robust:
- Multiple fallback regex patterns
- Pattern version detection
- Logging failed extractions for pattern updates

### 3. Cookie Freshness

YouTube cookies expire and need refresh:
- Check response for new Set-Cookie headers
- Update KV storage on every successful request
- Implement token health check endpoint

### 4. CORS on Video URLs

Direct video URLs may not work in browser due to CORS:
- Implement `/proxy/media` endpoint for streaming
- Use Range header support for seeking
- Consider chunked transfer

---

## Performance Considerations

### Caching Strategy

**Cache in KV:**
- Extracted base.js functions (keyed by player version)
- Video metadata (short TTL, ~5 minutes)

**Don't cache:**
- Video URLs (they expire)
- Cookies (always use latest)

### Request Minimization

1. Check if we have cached player functions before fetching base.js
2. Use single request for player response when possible
3. Batch subtitle language list with video metadata

---

## Testing Strategy

### Unit Tests
- URL encoding/decoding
- HTML rewriting
- Cookie parsing
- Player response parsing

### Integration Tests
- Full extraction flow with mock responses
- Proxy rewriting with real YouTube pages
- OAuth flow simulation

### Manual Testing
- Real YouTube login
- Various video types (public, age-restricted, members-only)
- Playlist extraction
- Subtitle download

---

## Deployment Notes

### Environment Variables

```
# wrangler.toml secrets
WORKER_URL=https://your-worker.workers.dev
```

### KV Namespace Setup

```bash
# Create KV namespace
wrangler kv:namespace create "TOKENS"

# For preview/dev
wrangler kv:namespace create "TOKENS" --preview
```

### Initial Deployment

```bash
# Deploy
wrangler deploy

# Tail logs
wrangler tail
```
