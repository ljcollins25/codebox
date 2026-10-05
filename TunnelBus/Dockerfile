# Tunnel bus container: the stock chisel server plus the Go router (supervises chisel).
# Cloudflare Containers need linux/amd64.
FROM --platform=linux/amd64 golang:1.24-alpine AS build
ENV CGO_ENABLED=0 GOFLAGS=-trimpath
ARG CHISEL_VERSION=v1.10.1
RUN go install -ldflags "-s -w" github.com/jpillora/chisel@${CHISEL_VERSION}
WORKDIR /src
COPY router/ ./
RUN go build -ldflags "-s -w" -o /out/router .

FROM --platform=linux/amd64 alpine:3.20
RUN apk add --no-cache ca-certificates && mkdir -p /data
COPY --from=build /go/bin/chisel /usr/local/bin/chisel
COPY --from=build /out/router /usr/local/bin/router
ENV LISTEN=:8080 AUTHFILE=/data/authfile.json CHISEL_BIN=/usr/local/bin/chisel
EXPOSE 8080
# Keep the Go GC lean for the 256 MiB "lite" instance.
ENV GOMEMLIMIT=96MiB
CMD ["/usr/local/bin/router"]
