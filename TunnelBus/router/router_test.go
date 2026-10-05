package main

import (
	"bufio"
	"bytes"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"
)

const tok = "test-admin-token"

func freePort(t *testing.T) int {
	t.Helper()
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer l.Close()
	return l.Addr().(*net.TCPAddr).Port
}

func newRouter(t *testing.T) (*Router, string) {
	t.Helper()
	af := filepath.Join(t.TempDir(), "users.json")
	p := freePort(t)
	reg, err := NewRegistry(tok, p, p+3, af)
	if err != nil {
		t.Fatal(err)
	}
	return &Router{Reg: reg, AdminToken: tok, started: time.Now()}, af
}

func call(rt http.Handler, method, path, auth, body string) *httptest.ResponseRecorder {
	req := httptest.NewRequest(method, path, strings.NewReader(body))
	if auth != "" {
		req.Header.Set("Authorization", "Bearer "+auth)
	}
	w := httptest.NewRecorder()
	rt.ServeHTTP(w, req)
	return w
}

func readAuthfile(t *testing.T, path string) map[string][]string {
	t.Helper()
	b, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	m := map[string][]string{}
	if err := json.Unmarshal(b, &m); err != nil {
		t.Fatalf("authfile is not valid JSON: %v\n%s", err, b)
	}
	return m
}

func TestRegisterRequiresAdminToken(t *testing.T) {
	rt, _ := newRouter(t)
	if w := call(rt, "POST", "/_api/register", "", `{"name":"a"}`); w.Code != 401 {
		t.Fatalf("no token: got %d", w.Code)
	}
	if w := call(rt, "POST", "/_api/register", "wrong", `{"name":"a"}`); w.Code != 401 {
		t.Fatalf("wrong token: got %d", w.Code)
	}
}

func TestRegisterAndAuthfile(t *testing.T) {
	rt, af := newRouter(t)
	// before any registration the authfile must still contain a user,
	// otherwise chisel would switch authentication off
	if m := readAuthfile(t, af); len(m) != 1 {
		t.Fatalf("empty registry authfile should hold only the sentinel: %v", m)
	}
	w := call(rt, "POST", "/_api/register", tok, `{"name":"hexad-dev"}`)
	if w.Code != 200 {
		t.Fatalf("register: %d %s", w.Code, w.Body)
	}
	var r struct {
		Port     int
		User     string
		Password string
		Created  bool
		Consumer struct{ User, Password string }
	}
	if err := json.Unmarshal(w.Body.Bytes(), &r); err != nil {
		t.Fatal(err)
	}
	if !r.Created || r.User != "p-hexad-dev" || r.Password == "" || r.Port == 0 {
		t.Fatalf("unexpected response %+v", r)
	}
	m := readAuthfile(t, af)
	pats, ok := m[r.User+":"+r.Password]
	if !ok || len(pats) != 1 {
		t.Fatalf("provider not in authfile: %v", m)
	}
	// the pattern must accept exactly this provider's reverse remote and nothing else
	if !strings.Contains(pats[0], `^R:`) || !strings.HasSuffix(pats[0], ":"+itoa(r.Port)+"$") {
		t.Fatalf("pattern %q does not pin port %d", pats[0], r.Port)
	}
	if _, ok := m[r.Consumer.User+":"+r.Consumer.Password]; !ok {
		t.Fatal("consumer user missing")
	}
	if w := call(rt, "POST", "/_api/register", tok, `{"name":"../x"}`); w.Code != 400 {
		t.Fatalf("bad name accepted: %d", w.Code)
	}
	if w := call(rt, "POST", "/_api/register", tok, `{"name":"_chisel"}`); w.Code != 400 {
		t.Fatalf("reserved name accepted: %d", w.Code)
	}
}

func itoa(i int) string { return strconv.Itoa(i) }

func TestAuthfileRewrittenInPlace(t *testing.T) {
	rt, af := newRouter(t)
	before, _ := os.Stat(af)
	call(rt, "POST", "/_api/register", tok, `{"name":"a"}`)
	call(rt, "POST", "/_api/register", tok, `{"name":"b"}`)
	call(rt, "DELETE", "/_api/register/a", tok, "")
	after, _ := os.Stat(af)
	if !os.SameFile(before, after) {
		t.Fatal("authfile was replaced; chisel's file watch would be lost")
	}
	m := readAuthfile(t, af)
	for k := range m {
		if strings.HasPrefix(k, "p-a:") || strings.HasPrefix(k, "c-a:") {
			t.Fatalf("removed provider still in authfile: %s", k)
		}
	}
	if len(m) != 3 { // sentinel + p-b + c-b
		t.Fatalf("want 3 users, got %d", len(m))
	}
}

func TestReRegistrationIsStable(t *testing.T) {
	rt, af := newRouter(t)
	reg := func(r *Router) Entry {
		e, _, err := r.Reg.Register("svc")
		if err != nil {
			t.Fatal(err)
		}
		return e
	}
	a := reg(rt)
	b := reg(rt)
	if a != b {
		t.Fatal("re-register changed the entry")
	}
	other, _, _ := rt.Reg.Register("other")
	if other.Port == a.Port {
		t.Fatal("ports collide")
	}
	// router restart: a fresh registry with the same secret hands out the same credentials
	reg2, err := NewRegistry(tok, a.Port, a.Port+3, af)
	if err != nil {
		t.Fatal(err)
	}
	c, created, _ := reg2.Register("svc")
	if !created || c.Password != a.Password || c.ConsumerPassword != a.ConsumerPassword {
		t.Fatalf("credentials changed across restart: %+v vs %+v", a, c)
	}
	// a different secret gives different credentials
	reg3, _ := NewRegistry("other-secret", a.Port, a.Port+3, filepath.Join(t.TempDir(), "x.json"))
	d, _, _ := reg3.Register("svc")
	if d.Password == a.Password {
		t.Fatal("password does not depend on the secret")
	}
}

func TestPortExhaustion(t *testing.T) {
	af := filepath.Join(t.TempDir(), "u.json")
	reg, _ := NewRegistry(tok, 30000, 30001, af)
	reg.Register("a")
	reg.Register("b")
	if _, _, err := reg.Register("c"); err == nil {
		t.Fatal("expected no free ports")
	}
	reg.Remove("a")
	if _, _, err := reg.Register("c"); err != nil {
		t.Fatalf("port should have been freed: %v", err)
	}
}

// startBackend serves h on the port registered for name.
func startBackend(t *testing.T, rt *Router, name string, h http.Handler) {
	t.Helper()
	e, _, err := rt.Reg.Register(name)
	if err != nil {
		t.Fatal(err)
	}
	l, err := net.Listen("tcp", "127.0.0.1:"+itoa(e.Port))
	if err != nil {
		t.Fatal(err)
	}
	srv := &http.Server{Handler: h}
	go srv.Serve(l)
	t.Cleanup(func() { srv.Close() })
}

func TestRoutingStripsPrefix(t *testing.T) {
	rt, _ := newRouter(t)
	startBackend(t, rt, "app", http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		io.WriteString(w, r.URL.RequestURI()+"|"+r.Header.Get("X-Forwarded-Prefix")+"|"+r.Host)
	}))
	front := httptest.NewServer(rt)
	defer front.Close()
	get := func(p string) (int, string) {
		resp, err := http.Get(front.URL + p)
		if err != nil {
			t.Fatal(err)
		}
		defer resp.Body.Close()
		b, _ := io.ReadAll(resp.Body)
		return resp.StatusCode, string(b)
	}
	host := strings.TrimPrefix(front.URL, "http://")
	if c, b := get("/app/a/b?x=1"); c != 200 || b != "/a/b?x=1|/app|"+host {
		t.Fatalf("got %d %q", c, b)
	}
	if c, b := get("/app/"); c != 200 || !strings.HasPrefix(b, "/|/app|") {
		t.Fatalf("root: %d %q", c, b)
	}
	if c, _ := get("/nobody/x"); c != 404 {
		t.Fatalf("unregistered name: %d", c)
	}
	if c, _ := get("/"); c != 404 {
		t.Fatalf("bare root: %d", c)
	}
	// "/app" (no slash) redirects to "/app/"; do not follow
	cl := &http.Client{CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	resp, err := cl.Get(front.URL + "/app?q=1")
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != 307 || resp.Header.Get("Location") != "/app/?q=1" {
		t.Fatalf("redirect: %d %s", resp.StatusCode, resp.Header.Get("Location"))
	}
}

func TestUnreachableProviderIs502(t *testing.T) {
	rt, _ := newRouter(t)
	rt.Reg.Register("down")
	if w := call(rt, "GET", "/down/", "", ""); w.Code != 502 {
		t.Fatalf("got %d", w.Code)
	}
}

func TestStreamingIsNotBuffered(t *testing.T) {
	rt, _ := newRouter(t)
	release := make(chan struct{})
	startBackend(t, rt, "sse", http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/event-stream")
		io.WriteString(w, "data: one\n\n")
		w.(http.Flusher).Flush()
		<-release
		io.WriteString(w, "data: two\n\n")
	}))
	front := httptest.NewServer(rt)
	defer front.Close()
	resp, err := http.Get(front.URL + "/sse/events")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	line := make(chan string, 1)
	go func() { s, _ := bufio.NewReader(resp.Body).ReadString('\n'); line <- s }()
	select {
	case s := <-line:
		if s != "data: one\n" {
			t.Fatalf("got %q", s)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("first event not delivered while the backend is still streaming: output is buffered")
	}
	close(release)
}

func TestWebSocketUpgradePassesThrough(t *testing.T) {
	rt, _ := newRouter(t)
	startBackend(t, rt, "ws", http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Upgrade") != "websocket" {
			http.Error(w, "no upgrade", 400)
			return
		}
		c, rw, err := w.(http.Hijacker).Hijack()
		if err != nil {
			return
		}
		defer c.Close()
		rw.WriteString("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n")
		rw.Flush()
		buf := make([]byte, 64)
		for {
			n, err := rw.Read(buf) // raw echo stands in for WebSocket frames
			if err != nil {
				return
			}
			c.Write(bytes.ToUpper(buf[:n]))
		}
	}))
	front := httptest.NewServer(rt)
	defer front.Close()
	c, err := net.Dial("tcp", strings.TrimPrefix(front.URL, "http://"))
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()
	c.SetDeadline(time.Now().Add(5 * time.Second))
	io.WriteString(c, "GET /ws/sock HTTP/1.1\r\nHost: x\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n")
	br := bufio.NewReader(c)
	status, err := br.ReadString('\n')
	if err != nil || !strings.Contains(status, "101") {
		t.Fatalf("no 101 through the router: %q %v", status, err)
	}
	for {
		l, _ := br.ReadString('\n')
		if l == "\r\n" || l == "" {
			break
		}
	}
	io.WriteString(c, "ping")
	got := make([]byte, 4)
	if _, err := io.ReadFull(br, got); err != nil || string(got) != "PING" {
		t.Fatalf("echo through upgraded connection: %q %v", got, err)
	}
}

func TestChiselPathIsStripped(t *testing.T) {
	rt, _ := newRouter(t)
	chisel := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { io.WriteString(w, "chisel:"+r.URL.Path) }))
	defer chisel.Close()
	rt.ChiselAddr = strings.TrimPrefix(chisel.URL, "http://")
	if w := call(rt, "GET", "/_chisel/health", "", ""); w.Body.String() != "chisel:/health" {
		t.Fatalf("got %q", w.Body)
	}
	if w := call(rt, "GET", "/_chisel", "", ""); w.Body.String() != "chisel:/" {
		t.Fatalf("got %q", w.Body)
	}
}

func TestRegistryListReportsUp(t *testing.T) {
	rt, _ := newRouter(t)
	startBackend(t, rt, "up", http.NotFoundHandler())
	rt.Reg.Register("down")
	w := call(rt, "GET", "/_api/registry", tok, "")
	var rows []struct {
		Name string
		Up   bool
	}
	json.Unmarshal(w.Body.Bytes(), &rows)
	if len(rows) != 2 || rows[0].Name != "down" || rows[0].Up || rows[1].Name != "up" || !rows[1].Up {
		t.Fatalf("%s", w.Body)
	}
}

// ---- host routing ----

func hostReq(rt *Router, host, path string) *httptest.ResponseRecorder {
	req := httptest.NewRequest("GET", path, nil)
	req.Host = host
	w := httptest.NewRecorder()
	rt.ServeHTTP(w, req)
	return w
}

func TestHostRouting(t *testing.T) {
	rt, _ := newRouter(t)
	rt.BaseDomain = "example.com"
	rt.ControlHost = "bus.example.com"
	startBackend(t, rt, "hexad", http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		io.WriteString(w, strings.Join([]string{r.URL.RequestURI(), r.Host, r.Header.Get("X-Forwarded-Host"), r.Header.Get("X-Bus-Host-Prefix"), r.Header.Get("X-Forwarded-Prefix")}, "|"))
	}))
	cases := []struct{ host, path, want string }{
		{"hexad.example.com", "/a/b?x=1", "/a/b?x=1|hexad.example.com|hexad.example.com||"},
		{"HEXAD.example.com:443", "/", "/|hexad.example.com|hexad.example.com||"},
		{"3000--hexad.example.com", "/p", "/p|3000--hexad.example.com|3000--hexad.example.com|3000|"},
		{"a--b--hexad.example.com", "/", "/|a--b--hexad.example.com|a--b--hexad.example.com|a--b|"},
	}
	for _, c := range cases {
		if w := hostReq(rt, c.host, c.path); w.Code != 200 || w.Body.String() != c.want {
			t.Errorf("%s%s: %d %q, want %q", c.host, c.path, w.Code, w.Body, c.want)
		}
	}
	for _, h := range []string{"nobody.example.com", "x.hexad.example.com", "--hexad.example.com", "example.com"} {
		if w := hostReq(rt, h, "/"); w.Code != 404 {
			t.Errorf("%s: want 404 got %d", h, w.Code)
		}
	}
	// a different domain falls back to path routing
	if w := hostReq(rt, "other.test", "/hexad/z"); w.Code != 200 || !strings.HasPrefix(w.Body.String(), "/z|") {
		t.Errorf("path routing on foreign host: %d %q", w.Code, w.Body)
	}
	// the control host serves the API, never a provider
	if w := hostReq(rt, "bus.example.com", "/_health"); w.Code != 200 {
		t.Errorf("control host health: %d", w.Code)
	}
	if w := hostReq(rt, "bus.example.com", "/hexad/"); w.Code != 404 && w.Code != 200 {
		t.Errorf("control host path: %d", w.Code)
	}
	if w := call(rt, "POST", "/_api/register", tok, `{"name":"bus"}`); w.Code != 400 {
		t.Errorf("control label registrable: %d", w.Code)
	}
}

func TestBusHostHeaderOverridesHost(t *testing.T) {
	rt, _ := newRouter(t)
	rt.BaseDomain = "example.com"
	startBackend(t, rt, "app", http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { io.WriteString(w, r.Host+"|"+r.Header.Get("X-Bus-Host")) }))
	req := httptest.NewRequest("GET", "/", nil)
	req.Host = "tunnel-bus.workers.dev"
	req.Header.Set("X-Bus-Host", "5000--app.example.com")
	w := httptest.NewRecorder()
	rt.ServeHTTP(w, req)
	if w.Body.String() != "5000--app.example.com|" {
		t.Fatalf("%q", w.Body)
	}
}

func TestNamesRejectDoubleDash(t *testing.T) {
	rt, _ := newRouter(t)
	for _, n := range []string{"a--b", "-a", "a-", strings.Repeat("a", 41)} {
		if _, _, err := rt.Reg.Register(n); err == nil {
			t.Errorf("%q accepted", n)
		}
	}
	if _, _, err := rt.Reg.Register("a-b"); err != nil {
		t.Error(err)
	}
}

func TestHostWebSocketPassThrough(t *testing.T) {
	rt, _ := newRouter(t)
	rt.BaseDomain = "example.com"
	startBackend(t, rt, "ws", http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		c, rw, err := w.(http.Hijacker).Hijack()
		if err != nil {
			return
		}
		defer c.Close()
		rw.WriteString("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n")
		rw.Flush()
		io.Copy(c, rw)
	}))
	front := httptest.NewServer(rt)
	defer front.Close()
	c, _ := net.Dial("tcp", strings.TrimPrefix(front.URL, "http://"))
	defer c.Close()
	c.SetDeadline(time.Now().Add(5 * time.Second))
	io.WriteString(c, "GET /sock HTTP/1.1\r\nHost: 3000--ws.example.com\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n")
	br := bufio.NewReader(c)
	if s, _ := br.ReadString('\n'); !strings.Contains(s, "101") {
		t.Fatalf("no 101: %q", s)
	}
}
