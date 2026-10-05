package main

import (
	"crypto/subtle"
	"encoding/json"
	"fmt"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"strconv"
	"strings"
	"time"
)

// Router maps <bus>/<name>/... to the port a provider's reverse remote opened.
type Router struct {
	Reg        *Registry
	AdminToken string
	ChiselAddr string // host:port of the local chisel server ("" = none)
	started    time.Time
}

const (
	apiPrefix    = "/_api/"
	chiselPrefix = "/_chisel"
)

// Resolve finds the provider a request is for and the path to forward.
// This is the one place to add subdomain routing later: look at req.Host
// (<name>.bus.example.com) first and, when it matches, return the path unchanged.
func (rt *Router) Resolve(req *http.Request) (name, forwardPath string, ok bool) {
	p := req.URL.Path
	if !strings.HasPrefix(p, "/") || len(p) < 2 {
		return "", "", false
	}
	rest := p[1:]
	name = rest
	forwardPath = "/"
	if i := strings.IndexByte(rest, '/'); i >= 0 {
		name, forwardPath = rest[:i], rest[i:]
	}
	return name, forwardPath, NameRe.MatchString(name)
}

func (rt *Router) ServeHTTP(w http.ResponseWriter, req *http.Request) {
	p := req.URL.Path
	switch {
	case p == "/_health":
		writeJSON(w, 200, map[string]any{"ok": true, "uptimeSeconds": int(time.Since(rt.started).Seconds()), "providers": len(rt.Reg.List())})
	case strings.HasPrefix(p, apiPrefix):
		rt.api(w, req)
	case p == chiselPrefix || strings.HasPrefix(p, chiselPrefix+"/"):
		rt.toChisel(w, req)
	default:
		rt.toProvider(w, req)
	}
}

func (rt *Router) authorized(req *http.Request) bool {
	h := req.Header.Get("Authorization")
	tok := strings.TrimPrefix(h, "Bearer ")
	return rt.AdminToken != "" && strings.HasPrefix(h, "Bearer ") &&
		subtle.ConstantTimeCompare([]byte(tok), []byte(rt.AdminToken)) == 1
}

func writeJSON(w http.ResponseWriter, code int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(v)
}

func (rt *Router) api(w http.ResponseWriter, req *http.Request) {
	if !rt.authorized(req) {
		writeJSON(w, 401, map[string]string{"error": "admin token required"})
		return
	}
	sub := strings.TrimPrefix(req.URL.Path, apiPrefix)
	switch {
	case sub == "register" && req.Method == http.MethodPost:
		var body struct {
			Name string `json:"name"`
		}
		if err := json.NewDecoder(http.MaxBytesReader(w, req.Body, 4096)).Decode(&body); err != nil {
			writeJSON(w, 400, map[string]string{"error": "bad json: " + err.Error()})
			return
		}
		e, created, err := rt.Reg.Register(body.Name)
		if err != nil {
			writeJSON(w, 400, map[string]string{"error": err.Error()})
			return
		}
		writeJSON(w, 200, map[string]any{
			"name": e.Name, "created": created, "port": e.Port,
			"user": e.User, "password": e.Password,
			"chiselPath": chiselPrefix, "path": "/" + e.Name + "/",
			"consumer": map[string]any{"user": e.ConsumerUser, "password": e.ConsumerPassword, "remoteHost": "127.0.0.1", "remotePort": e.Port},
		})
	case strings.HasPrefix(sub, "register/") && req.Method == http.MethodDelete:
		name := strings.TrimPrefix(sub, "register/")
		if !rt.Reg.Remove(name) {
			writeJSON(w, 404, map[string]string{"error": "not registered"})
			return
		}
		writeJSON(w, 200, map[string]any{"removed": name})
	case sub == "registry" && req.Method == http.MethodGet:
		type row struct {
			Name string `json:"name"`
			Port int    `json:"port"`
			Up   bool   `json:"up"`
		}
		rows := []row{}
		for _, e := range rt.Reg.List() {
			rows = append(rows, row{e.Name, e.Port, portOpen(e.Port)})
		}
		writeJSON(w, 200, rows)
	default:
		writeJSON(w, 404, map[string]string{"error": "unknown api call"})
	}
}

func portOpen(port int) bool {
	c, err := net.DialTimeout("tcp", net.JoinHostPort("127.0.0.1", strconv.Itoa(port)), 500*time.Millisecond)
	if err != nil {
		return false
	}
	c.Close()
	return true
}

// newProxy builds a reverse proxy to host:port that rewrites the path.
// ReverseProxy passes WebSocket upgrades through; FlushInterval -1 flushes
// every write, so SSE and streaming bodies are not buffered.
func newProxy(hostport, path, prefix string) *httputil.ReverseProxy {
	target := &url.URL{Scheme: "http", Host: hostport}
	return &httputil.ReverseProxy{
		FlushInterval: -1,
		Rewrite: func(pr *httputil.ProxyRequest) {
			pr.SetURL(target)
			pr.Out.URL.Path = path
			pr.Out.URL.RawPath = ""
			pr.Out.Host = pr.In.Host // keep the public Host (WebSocket Origin checks, absolute URLs)
			pr.SetXForwarded()
			if prefix != "" {
				pr.Out.Header.Set("X-Forwarded-Prefix", prefix)
			}
		},
		ErrorHandler: func(w http.ResponseWriter, r *http.Request, err error) {
			http.Error(w, "tunnel bus: provider unreachable: "+err.Error(), http.StatusBadGateway)
		},
	}
}

func (rt *Router) toChisel(w http.ResponseWriter, req *http.Request) {
	if rt.ChiselAddr == "" {
		http.Error(w, "chisel not running", http.StatusServiceUnavailable)
		return
	}
	path := strings.TrimPrefix(req.URL.Path, chiselPrefix)
	if path == "" {
		path = "/"
	}
	newProxy(rt.ChiselAddr, path, "").ServeHTTP(w, req)
}

func (rt *Router) toProvider(w http.ResponseWriter, req *http.Request) {
	name, fwd, ok := rt.Resolve(req)
	if !ok {
		http.Error(w, "tunnel bus: use /<name>/... (see /_health)", http.StatusNotFound)
		return
	}
	e, found := rt.Reg.Get(name)
	if !found {
		http.Error(w, fmt.Sprintf("tunnel bus: %q is not registered", name), http.StatusNotFound)
		return
	}
	if fwd == "/" && !strings.HasSuffix(req.URL.Path, "/") {
		http.Redirect(w, req, "/"+name+"/"+queryOf(req), http.StatusTemporaryRedirect)
		return
	}
	newProxy("127.0.0.1:"+strconv.Itoa(e.Port), fwd, "/"+name).ServeHTTP(w, req)
}

func queryOf(r *http.Request) string {
	if r.URL.RawQuery == "" {
		return ""
	}
	return "?" + r.URL.RawQuery
}
