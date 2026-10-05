package main

import (
	"encoding/json"
	"net"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"
)

func providers(t *testing.T, rt *Router) []map[string]any {
	t.Helper()
	w := call(rt, "GET", "/_api/providers", tok, "")
	var out []map[string]any
	if err := json.Unmarshal(w.Body.Bytes(), &out); err != nil {
		t.Fatal(err, w.Body.String())
	}
	return out
}

func TestMetadataSetAndListed(t *testing.T) {
	rt, _ := newRouter(t)
	w := call(rt, "POST", "/_api/register", tok, `{"name":"demo","description":"A demo app","label":"Demo","source":"session x-1 (hexad project)","kind":"Hexad"}`)
	if w.Code != 200 {
		t.Fatal(w.Code, w.Body.String())
	}
	p := providers(t, rt)[0]
	if p["description"] != "A demo app" || p["label"] != "Demo" || p["owner"] != "session x-1 (hexad project)" || p["kind"] != "hexad" {
		t.Fatalf("metadata missing: %v", p)
	}
	if p["registeredAt"] == nil || p["metaUpdatedAt"] == nil {
		t.Fatalf("times missing: %v", p)
	}
}

func TestOldClientsStillWorkAndDoNotWipeMetadata(t *testing.T) {
	rt, _ := newRouter(t)
	if w := call(rt, "POST", "/_api/register", tok, `{"name":"old"}`); w.Code != 200 {
		t.Fatal(w.Body.String())
	}
	call(rt, "POST", "/_api/register", tok, `{"name":"old","description":"keep me","owner":"o"}`)
	// an old client registers again with only a name
	w := call(rt, "POST", "/_api/register", tok, `{"name":"old"}`)
	if w.Code != 200 || strings.Contains(w.Body.String(), `"created":true`) {
		t.Fatal(w.Code, w.Body.String())
	}
	p := providers(t, rt)[0]
	if p["description"] != "keep me" || p["owner"] != "o" {
		t.Fatalf("metadata wiped: %v", p)
	}
	// an explicit empty string clears
	call(rt, "POST", "/_api/register", tok, `{"name":"old","description":""}`)
	if providers(t, rt)[0]["description"] != "" {
		t.Fatal("empty string should clear")
	}
}

func TestPatchUpdatesWithoutReRegistering(t *testing.T) {
	rt, _ := newRouter(t)
	call(rt, "POST", "/_api/register", tok, `{"name":"p1","description":"one","kind":"app"}`)
	before, _ := rt.Reg.Get("p1")
	w := call(rt, "PATCH", "/_api/register/p1", tok, `{"description":"two","label":"P One"}`)
	if w.Code != 200 {
		t.Fatal(w.Code, w.Body.String())
	}
	after, _ := rt.Reg.Get("p1")
	if after.Description != "two" || after.Label != "P One" || after.Kind != "app" {
		t.Fatalf("patch wrong: %+v", after)
	}
	if after.Port != before.Port || !after.RegisteredAt.Equal(before.RegisteredAt) {
		t.Fatal("patch must not change port or registeredAt")
	}
	if w := call(rt, "PATCH", "/_api/register/p1", "", `{}`); w.Code != 401 {
		t.Fatalf("patch needs auth, got %d", w.Code)
	}
	if w := call(rt, "PATCH", "/_api/register/nope", tok, `{"label":"x"}`); w.Code != 404 {
		t.Fatalf("unknown name should be 404, got %d", w.Code)
	}
}

func TestMetadataValidation(t *testing.T) {
	rt, _ := newRouter(t)
	long := strings.Repeat("x", MaxDescription+1)
	if w := call(rt, "POST", "/_api/register", tok, `{"name":"v1","description":"`+long+`"}`); w.Code != 400 {
		t.Fatalf("long description: %d", w.Code)
	}
	if _, ok := rt.Reg.Get("v1"); ok {
		t.Fatal("a rejected registration must not be created")
	}
	if w := call(rt, "POST", "/_api/register", tok, `{"name":"v1","kind":"Not Valid!"}`); w.Code != 400 {
		t.Fatalf("bad kind: %d", w.Code)
	}
	ok := strings.Repeat("é", MaxDescription) // counted in characters, not bytes
	if w := call(rt, "POST", "/_api/register", tok, `{"name":"v1","description":"`+ok+`"}`); w.Code != 200 {
		t.Fatalf("200 characters should fit: %d %s", w.Code, w.Body.String())
	}
}

// The router stores text as given (minus control characters) and the API returns it as JSON;
// escaping happens where HTML is built. Here: markup survives as inert text and control chars go.
func TestMetadataIsPlainTextInJSON(t *testing.T) {
	rt, _ := newRouter(t)
	call(rt, "POST", "/_api/register", tok, `{"name":"x1","description":"<img src=x onerror=alert(1)>\u0007 line\nbreak","label":"<b>hi</b>"}`)
	w := call(rt, "GET", "/_api/providers", tok, "")
	if ct := w.Header().Get("Content-Type"); ct != "application/json" {
		t.Fatal(ct)
	}
	if strings.Contains(w.Body.String(), "<img") {
		t.Fatalf("encoder should escape < in JSON: %s", w.Body.String())
	}
	e, _ := rt.Reg.Get("x1")
	if strings.ContainsRune(e.Description, 7) || strings.Contains(e.Description, "\n") {
		t.Fatalf("control characters kept: %q", e.Description)
	}
}

func TestConnectionTimes(t *testing.T) {
	rt, _ := newRouter(t)
	call(rt, "POST", "/_api/register", tok, `{"name":"c1"}`)
	e, _ := rt.Reg.Get("c1")
	t0 := time.Date(2026, 10, 5, 12, 0, 0, 0, time.UTC)
	if !e.ConnectedSince.IsZero() || e.Connected {
		t.Fatal("new registration is not connected")
	}
	rt.Reg.Observe("c1", true, t0)
	rt.Reg.Observe("c1", true, t0.Add(time.Minute)) // still the same connection
	e, _ = rt.Reg.Get("c1")
	if !e.ConnectedSince.Equal(t0) || !e.LastSeen.Equal(t0.Add(time.Minute)) || e.Reconnects != 0 {
		t.Fatalf("first connection: %+v", e)
	}
	rt.Reg.Observe("c1", false, t0.Add(2*time.Minute))
	e, _ = rt.Reg.Get("c1")
	if !e.ConnectedSince.IsZero() || e.Connected || !e.LastSeen.Equal(t0.Add(time.Minute)) {
		t.Fatalf("after disconnect: %+v", e)
	}
	rt.Reg.Observe("c1", true, t0.Add(3*time.Minute))
	e, _ = rt.Reg.Get("c1")
	if !e.ConnectedSince.Equal(t0.Add(3*time.Minute)) || e.Reconnects != 1 {
		t.Fatalf("reconnect should reset connectedSince and count: %+v", e)
	}
}

func TestProvidersShowConnectedSinceFromLivePort(t *testing.T) {
	rt, _ := newRouter(t)
	call(rt, "POST", "/_api/register", tok, `{"name":"live"}`)
	e, _ := rt.Reg.Get("live")
	if p := providers(t, rt)[0]; p["up"] != false || p["connectedSince"] != nil {
		t.Fatalf("down provider: %v", p)
	}
	l, err := net.Listen("tcp", "127.0.0.1:"+strconv.Itoa(e.Port))
	if err != nil {
		t.Skip("port taken")
	}
	defer l.Close()
	p := providers(t, rt)[0]
	if p["up"] != true || p["connectedSince"] == nil || p["lastSeen"] == nil {
		t.Fatalf("up provider: %v", p)
	}
}

func TestStatePersistsAcrossRestart(t *testing.T) {
	af := filepath.Join(t.TempDir(), "users.json")
	sf := filepath.Join(t.TempDir(), "state.json")
	p := freePort(t)
	reg1, _ := NewRegistry(tok, p, p+3, af)
	if err := reg1.LoadState(sf); err != nil {
		t.Fatal(err)
	}
	e1, _, _ := reg1.RegisterWith("keep", MetaPatch{Description: ptr("persisted"), Kind: ptr("app")})
	reg2, _ := NewRegistry(tok, p, p+3, af)
	if err := reg2.LoadState(sf); err != nil {
		t.Fatal(err)
	}
	e2, ok := reg2.Get("keep")
	if !ok || e2.Description != "persisted" || e2.Kind != "app" || e2.Port != e1.Port || !e2.RegisteredAt.Equal(e1.RegisteredAt) {
		t.Fatalf("not restored: %+v", e2)
	}
	if e2.Connected || !e2.ConnectedSince.IsZero() || e2.Password != e1.Password {
		t.Fatalf("connection state must start clean, credentials stay derivable: %+v", e2)
	}
	b, _ := os.ReadFile(sf)
	raw := string(b)
	if strings.Contains(raw, e1.Password) {
		t.Fatal("state file must not hold credentials")
	}
	reg2.Remove("keep")
	reg3, _ := NewRegistry(tok, p, p+3, af)
	_ = reg3.LoadState(sf)
	if _, ok := reg3.Get("keep"); ok {
		t.Fatal("removal must persist")
	}
}

func ptr(s string) *string { return &s }

func TestStatusShowsDisplayNameFromWorker(t *testing.T) {
	rt, _ := newRouter(t)
	rt.AdminEmails = map[string]bool{"pat@example.test": true}
	get := func(display string) string {
		req := httptest.NewRequest("GET", "/_api/status", nil)
		req.Header.Set("X-Bus-User", "pat@example.test")
		if display != "" {
			req.Header.Set("X-Bus-Display", display)
		}
		w := httptest.NewRecorder()
		rt.ServeHTTP(w, req)
		var m map[string]any
		_ = json.Unmarshal(w.Body.Bytes(), &m)
		return m["you"].(string)
	}
	if got := get("patex"); got != "patex" {
		t.Fatal(got)
	}
	if got := get("P%C3%A4t%20Ex"); got != "Pät Ex" {
		t.Fatal(got)
	}
	if got := get(""); got != "pat@example.test" {
		t.Fatalf("no display name: email is the last fallback, got %q", got)
	}
}

func TestSessionObjectAndURL(t *testing.T) {
	rt, _ := newRouter(t)
	w := call(rt, "POST", "/_api/register", tok, `{"name":"s1","session":{"name":"csharp-wasm-2","id":"s-20261005-083139-c037","hexad":"hexad project"},"sessionUrl":"https://hexad.example.test/s/s-1"}`)
	if w.Code != 200 || !strings.Contains(w.Body.String(), `"name":"csharp-wasm-2"`) {
		t.Fatal(w.Code, w.Body.String())
	}
	p := providers(t, rt)[0]
	s := p["session"].(map[string]any)
	if s["name"] != "csharp-wasm-2" || s["id"] != "s-20261005-083139-c037" || s["hexad"] != "hexad project" || p["sessionUrl"] != "https://hexad.example.test/s/s-1" {
		t.Fatalf("session missing: %v", p)
	}
	// an old client re-registering keeps the session; an explicit {} clears it
	call(rt, "POST", "/_api/register", tok, `{"name":"s1"}`)
	if providers(t, rt)[0]["session"].(map[string]any)["name"] != "csharp-wasm-2" {
		t.Fatal("old client wiped the session")
	}
	if w := call(rt, "PATCH", "/_api/register/s1", tok, `{"session":{"name":"renamed"}}`); w.Code != 200 {
		t.Fatal(w.Body.String())
	}
	s = providers(t, rt)[0]["session"].(map[string]any)
	if s["name"] != "renamed" || s["id"] != nil {
		t.Fatalf("session is replaced as a whole: %v", s)
	}
	call(rt, "PATCH", "/_api/register/s1", tok, `{"session":{},"sessionUrl":""}`)
	if providers(t, rt)[0]["session"].(map[string]any)["name"] != nil {
		t.Fatal("{} should clear")
	}
}

func TestSessionValidation(t *testing.T) {
	rt, _ := newRouter(t)
	for _, body := range []string{
		`{"name":"s2","sessionUrl":"javascript:alert(1)"}`,
		`{"name":"s2","sessionUrl":"//evil.test/x"}`,
		`{"name":"s2","session":{"name":"` + strings.Repeat("x", MaxSessionName+1) + `"}}`,
		`{"name":"s2","session":{"hexad":"` + strings.Repeat("x", MaxHexad+1) + `"}}`,
	} {
		if w := call(rt, "POST", "/_api/register", tok, body); w.Code != 400 {
			t.Fatalf("%s: got %d", body, w.Code)
		}
	}
	if _, ok := rt.Reg.Get("s2"); ok {
		t.Fatal("rejected registration was created")
	}
	call(rt, "POST", "/_api/register", tok, `{"name":"s2","session":{"name":"<script>x</script>\u0007"}}`)
	e, _ := rt.Reg.Get("s2")
	if strings.ContainsRune(e.Session.Name, 7) {
		t.Fatal("control characters kept")
	}
}

func TestSessionSurvivesRestart(t *testing.T) {
	af := filepath.Join(t.TempDir(), "users.json")
	sf := filepath.Join(t.TempDir(), "state.json")
	p := freePort(t)
	r1, _ := NewRegistry(tok, p, p+3, af)
	_ = r1.LoadState(sf)
	_, _, err := r1.RegisterWith("k", MetaPatch{Session: &Session{Name: "n", ID: "i", Hexad: "h"}, SessionURL: ptr("https://x.test/s")})
	if err != nil {
		t.Fatal(err)
	}
	r2, _ := NewRegistry(tok, p, p+3, af)
	_ = r2.LoadState(sf)
	e, _ := r2.Get("k")
	if e.Session != (Session{"n", "i", "h"}) || e.SessionURL != "https://x.test/s" {
		t.Fatalf("%+v", e)
	}
}
