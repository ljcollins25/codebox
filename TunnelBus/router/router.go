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
	"sync"
	"time"
)

// Router maps <bus>/<name>/... to the port a provider's reverse remote opened.
type Router struct {
	Reg        *Registry
	AdminToken string
	ChiselAddr string // host:port of the local chisel server ("" = none)
	// BaseDomain enables host routing: <name>.<BaseDomain> and <prefix>--<name>.<BaseDomain>.
	BaseDomain string
	// ControlHost serves /_api, /_chisel and /_health under BaseDomain (e.g. ctl.example.com).
	// It is never a provider host.
	ControlHost string
	// LabelSuffix, when set (e.g. "-bus"), is required at the end of a provider host label and
	// stripped: "3000--hexad-bus.example.com" -> prefix "3000", name "hexad". It lets a shared zone
	// use the Worker route "*-bus.example.com/*" (a wildcard may only lead a route host).
	LabelSuffix string
	// AdminEmails are Access identities (verified by the Worker, passed in X-Bus-User) allowed to use
	// the admin API without the admin token. Mutations from an identity also need X-Bus-CSRF: 1.
	AdminEmails    map[string]bool
	AccessRequired bool // reported on the dashboard; enforced by the Worker
	Version        string
	started        time.Time
}

const (
	apiPrefix    = "/_api/"
	chiselPrefix = "/_chisel"
)

// Target is where a request goes.
type Target struct {
	Name       string
	Path       string // path to forward
	HostPrefix string // host routing only: everything left of "--" in the label ("3000" in 3000--hexad)
	ByHost     bool
}

// HostOf is the public host of a request, without port. The Worker sets X-Bus-Host
// (it always overwrites any client value); direct callers (tests, local runs) fall back to Host.
func HostOf(req *http.Request) string {
	h := req.Header.Get("X-Bus-Host")
	if h == "" {
		h = req.Host
	}
	if i := strings.LastIndexByte(h, ':'); i >= 0 && !strings.Contains(h[i:], "]") {
		h = h[:i]
	}
	return strings.ToLower(h)
}

// hostMode reports whether the request's host is a provider host under BaseDomain.
func (rt *Router) hostMode(host string) bool {
	if rt.BaseDomain == "" {
		return false
	}
	return strings.HasSuffix(host, "."+rt.BaseDomain) && host != rt.ControlHost
}

// SplitLabel splits a host label into its pass-through prefix and the provider name.
// "hexad" -> ("", "hexad"); "3000--hexad" -> ("3000", "hexad"); "a--b--hexad" -> ("a--b", "hexad").
// The provider name is what follows the LAST "--"; names never contain "--".
func SplitLabel(label string) (prefix, name string) {
	if i := strings.LastIndex(label, "--"); i >= 0 {
		return label[:i], label[i+2:]
	}
	return "", label
}

// Resolve finds the provider a request is for and the path to forward.
//   - host routing (BaseDomain set, host under it): the single label directly left of
//     BaseDomain; path unchanged. "a.b.example.com" is not routed.
//   - otherwise path routing (workers.dev): the first path segment, which is stripped.
func (rt *Router) Resolve(req *http.Request) (Target, bool) {
	host := HostOf(req)
	if rt.hostMode(host) {
		label := strings.TrimSuffix(host, "."+rt.BaseDomain)
		if label == "" || strings.Contains(label, ".") || strings.HasPrefix(label, "--") {
			return Target{}, false
		}
		if rt.LabelSuffix != "" {
			if !strings.HasSuffix(label, rt.LabelSuffix) {
				return Target{}, false
			}
			label = strings.TrimSuffix(label, rt.LabelSuffix)
		}
		prefix, name := SplitLabel(label)
		if !NameRe.MatchString(name) {
			return Target{}, false
		}
		return Target{Name: name, Path: req.URL.Path, HostPrefix: prefix, ByHost: true}, true
	}
	p := req.URL.Path
	if !strings.HasPrefix(p, "/") || len(p) < 2 {
		return Target{}, false
	}
	rest := p[1:]
	name, fwd := rest, "/"
	if i := strings.IndexByte(rest, '/'); i >= 0 {
		name, fwd = rest[:i], rest[i:]
	}
	return Target{Name: name, Path: fwd}, NameRe.MatchString(name)
}

func (rt *Router) ServeHTTP(w http.ResponseWriter, req *http.Request) {
	p := req.URL.Path
	if rt.hostMode(HostOf(req)) {
		rt.toProvider(w, req)
		return
	}
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

// principal says who is calling the admin API: the admin token (programs), or an Access identity on the
// allow-list (browsers; the Worker verified the JWT and set X-Bus-User, and strips any client-sent value).
// status: 0 = ok, 401 = nobody, 403 = identity without the CSRF header on a mutation.
func (rt *Router) principal(req *http.Request) (who string, status int) {
	if rt.authorized(req) {
		return "admin-token", 0
	}
	email := strings.ToLower(strings.TrimSpace(req.Header.Get("X-Bus-User")))
	if email == "" || !rt.AdminEmails[email] {
		return "", 401
	}
	if req.Method != http.MethodGet && req.Method != http.MethodHead && req.Header.Get("X-Bus-CSRF") != "1" {
		return "", 403
	}
	return email, 0
}

// providerView is the dashboard's view of a registration. It never carries credentials.
type providerView struct {
	Name         string     `json:"name"`
	Port         int        `json:"port"`
	Up           bool       `json:"up"`
	RegisteredAt time.Time  `json:"registeredAt"`
	LastSeen     *time.Time `json:"lastSeen"`
	Host         string     `json:"host,omitempty"`         // <name>[suffix].<base>
	PrefixedHost string     `json:"prefixedHost,omitempty"` // <prefix>--<name>[suffix].<base> (literal "<prefix>")
	Path         string     `json:"path"`                   // path form, for workers.dev
}

// probe checks every registered provider's reverse port concurrently and records last-seen times.
func (rt *Router) probe() []providerView {
	entries := rt.Reg.List()
	views := make([]providerView, len(entries))
	var wg sync.WaitGroup
	for i, e := range entries {
		wg.Add(1)
		go func(i int, e Entry) {
			defer wg.Done()
			v := providerView{Name: e.Name, Port: e.Port, RegisteredAt: e.RegisteredAt, Path: "/" + e.Name + "/"}
			if rt.BaseDomain != "" {
				v.Host = e.Name + rt.LabelSuffix + "." + rt.BaseDomain
				v.PrefixedHost = "<prefix>--" + v.Host
			}
			v.Up = portOpen(e.Port)
			if v.Up {
				now := time.Now().UTC()
				rt.Reg.Touch(e.Name, now)
				v.LastSeen = &now
			} else if !e.LastSeen.IsZero() {
				t := e.LastSeen
				v.LastSeen = &t
			}
			views[i] = v
		}(i, e)
	}
	wg.Wait()
	return views
}

func (rt *Router) status() map[string]any {
	views := rt.probe()
	conns := 0
	for _, v := range views {
		if v.Up {
			conns++
		}
	}
	return map[string]any{
		"version": rt.Version, "uptimeSeconds": int(time.Since(rt.started).Seconds()),
		"providers": len(views), "connections": conns, "accessRequired": rt.AccessRequired,
		"baseDomain": rt.BaseDomain, "controlHost": rt.ControlHost, "labelSuffix": rt.LabelSuffix,
	}
}

func writeJSON(w http.ResponseWriter, code int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(v)
}

func (rt *Router) api(w http.ResponseWriter, req *http.Request) {
	who, st := rt.principal(req)
	switch st {
	case 401:
		writeJSON(w, 401, map[string]string{"error": "admin token or allowed Access identity required"})
		return
	case 403:
		writeJSON(w, 403, map[string]string{"error": "missing X-Bus-CSRF header"})
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
		if rt.ControlHost != "" && body.Name+rt.LabelSuffix == strings.SplitN(rt.ControlHost, ".", 2)[0] {
			writeJSON(w, 400, map[string]string{"error": "name is reserved for the control host"})
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
	case sub == "providers" && req.Method == http.MethodGet:
		writeJSON(w, 200, rt.probe())
	case sub == "status" && req.Method == http.MethodGet:
		out := rt.status()
		out["you"] = who
		writeJSON(w, 200, out)
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
func newProxy(hostport, path, prefix, publicHost, hostPrefix string) *httputil.ReverseProxy {
	target := &url.URL{Scheme: "http", Host: hostport}
	return &httputil.ReverseProxy{
		FlushInterval: -1,
		Rewrite: func(pr *httputil.ProxyRequest) {
			pr.SetURL(target)
			pr.Out.URL.Path = path
			pr.Out.URL.RawPath = ""
			if publicHost == "" {
				publicHost = pr.In.Host
			}
			pr.Out.Host = publicHost // keep the public Host (WebSocket Origin checks, absolute URLs)
			pr.SetXForwarded()
			pr.Out.Header.Set("X-Forwarded-Host", publicHost)
			pr.Out.Header.Del("X-Bus-Host")
			pr.Out.Header.Del("X-Bus-User") // never reaches providers
			pr.Out.Header.Del("X-Bus-CSRF")
			pr.Out.Header.Del("X-Bus-Host-Prefix")
			if hostPrefix != "" {
				pr.Out.Header.Set("X-Bus-Host-Prefix", hostPrefix)
			}
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
	newProxy(rt.ChiselAddr, path, "", "", "").ServeHTTP(w, req)
}

func (rt *Router) toProvider(w http.ResponseWriter, req *http.Request) {
	t, ok := rt.Resolve(req)
	if !ok {
		http.Error(w, "tunnel bus: unknown host or path (see /_health)", http.StatusNotFound)
		return
	}
	e, found := rt.Reg.Get(t.Name)
	if !found {
		http.Error(w, fmt.Sprintf("tunnel bus: %q is not registered", t.Name), http.StatusNotFound)
		return
	}
	hostport := "127.0.0.1:" + strconv.Itoa(e.Port)
	if t.ByHost {
		newProxy(hostport, t.Path, "", HostOf(req), t.HostPrefix).ServeHTTP(w, req)
		return
	}
	if t.Path == "/" && !strings.HasSuffix(req.URL.Path, "/") {
		http.Redirect(w, req, "/"+t.Name+"/"+queryOf(req), http.StatusTemporaryRedirect)
		return
	}
	newProxy(hostport, t.Path, "/"+t.Name, "", "").ServeHTTP(w, req)
}

func queryOf(r *http.Request) string {
	if r.URL.RawQuery == "" {
		return ""
	}
	return "?" + r.URL.RawQuery
}
