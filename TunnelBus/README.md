# Tunnel bus (work in progress)

Chisel server + Go router in a Cloudflare Container, behind a Worker + Durable Object.
Status: router and tests done (`cd router && go test ./...`); first deploy blocked, see the session report
(`GET /containers/me` returned 403: the API token lacks the Containers permission, Account > Cloudchamber > Edit).
