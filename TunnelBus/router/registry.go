package main

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"sync"
	"time"
	"unicode"
)

// NameRe is the set of valid provider names. Leading "_" is reserved for the
// router's own paths (/_api, /_chisel, /_health).
var NameRe = regexp.MustCompile(`^[a-z0-9]+(-[a-z0-9]+)*$`) // no "--": that separates a host prefix from the name

const maxNameLen = 40

// Entry is one live registration.
type Entry struct {
	Name     string `json:"name"`
	Port     int    `json:"port"`     // port chisel opens on the server for the reverse remote
	User     string `json:"user"`     // chisel user allowed to open only the reverse remote on Port
	Password string `json:"password"` // derived from the admin token, so it survives router restarts
	// Consumer credentials: may only forward to 127.0.0.1:Port (TCP access).
	RegisteredAt     time.Time `json:"registeredAt"`
	LastSeen         time.Time `json:"lastSeen"` // last time the provider's reverse port was seen open (zero = never)
	ConsumerUser     string    `json:"consumerUser"`
	ConsumerPassword string    `json:"consumerPassword"`

	// Optional metadata shown on the dashboard (all untrusted text: agents write it).
	Description string    `json:"description,omitempty"`
	Label       string    `json:"label,omitempty"`
	Owner       string    `json:"owner,omitempty"` // who registered it, e.g. "hexad project"
	Kind        string    `json:"kind,omitempty"`  // hexad, app, vscode, ...
	Session     Session   `json:"session"`         // the hexad session that registered it, if any
	SessionURL  string    `json:"sessionUrl,omitempty"`
	UpdatedAt   time.Time `json:"updatedAt"` // last metadata change (zero = never set)

	// Connection state, observed by the router's probe of the reverse port.
	Connected      bool      `json:"connected"`
	ConnectedSince time.Time `json:"connectedSince"` // start of the current provider connection; zero when not connected
	Reconnects     int       `json:"reconnects"`     // connections after the first one
	everConnected  bool
}

const (
	MaxDescription = 200
	MaxLabel       = 60
	MaxOwner       = 100
	MaxKind        = 24
)

var kindRe = regexp.MustCompile(`^[a-z0-9][a-z0-9._-]*$`)

// Session identifies the hexad session (the agent) behind a registration.
type Session struct {
	Name  string `json:"name,omitempty"`  // the agent's name, e.g. csharp-wasm-2
	ID    string `json:"id,omitempty"`    // e.g. s-20261005-083139-c037
	Hexad string `json:"hexad,omitempty"` // which hexad, e.g. "hexad project"
}

const (
	MaxSessionName = 60
	MaxSessionID   = 80
	MaxHexad       = 100
	MaxURL         = 300
)

// MetaPatch carries optional metadata. A nil field means "not given" (unchanged);
// an empty string clears the field.
type MetaPatch struct {
	Description *string `json:"description"`
	Label       *string `json:"label"`
	Owner       *string `json:"owner"`
	Source      *string `json:"source"` // alias of owner
	Kind        *string `json:"kind"`
	// Session, when present, replaces the whole session ({} clears it); absent or null leaves it unchanged.
	Session    *Session `json:"session"`
	SessionURL *string  `json:"sessionUrl"`
}

func cleanText(s string) string {
	s = strings.Map(func(r rune) rune {
		if r == '\n' || r == '\t' {
			return ' '
		}
		if unicode.IsControl(r) {
			return -1
		}
		return r
	}, s)
	return strings.TrimSpace(s)
}

// normalize validates a patch and returns it with cleaned values (control characters removed, trimmed).
// Length limits are in characters and are rejected, not truncated, so a client learns about them.
func (m MetaPatch) normalize() (MetaPatch, error) {
	if m.Owner == nil {
		m.Owner = m.Source
	}
	m.Source = nil
	chk := func(p **string, field string, max int) error {
		if *p == nil {
			return nil
		}
		v := cleanText(**p)
		if len([]rune(v)) > max {
			return fmt.Errorf("%s is too long (max %d characters)", field, max)
		}
		*p = &v
		return nil
	}
	if err := chk(&m.Description, "description", MaxDescription); err != nil {
		return m, err
	}
	if err := chk(&m.Label, "label", MaxLabel); err != nil {
		return m, err
	}
	if err := chk(&m.Owner, "owner", MaxOwner); err != nil {
		return m, err
	}
	if m.Session != nil {
		ss := Session{Name: cleanText(m.Session.Name), ID: cleanText(m.Session.ID), Hexad: cleanText(m.Session.Hexad)}
		for _, c := range []struct {
			v, f string
			max  int
		}{{ss.Name, "session.name", MaxSessionName}, {ss.ID, "session.id", MaxSessionID}, {ss.Hexad, "session.hexad", MaxHexad}} {
			if len([]rune(c.v)) > c.max {
				return m, fmt.Errorf("%s is too long (max %d characters)", c.f, c.max)
			}
		}
		m.Session = &ss
	}
	if m.SessionURL != nil {
		u := cleanText(*m.SessionURL)
		if u != "" {
			pu, err := url.Parse(u)
			if err != nil || (pu.Scheme != "https" && pu.Scheme != "http") || pu.Host == "" || len(u) > MaxURL {
				return m, fmt.Errorf("sessionUrl must be an http(s) URL of at most %d characters", MaxURL)
			}
		}
		m.SessionURL = &u
	}
	if m.Kind != nil {
		k := strings.ToLower(cleanText(*m.Kind))
		if k != "" && (len(k) > MaxKind || !kindRe.MatchString(k)) {
			return m, fmt.Errorf("kind must match %s, max %d characters", kindRe, MaxKind)
		}
		m.Kind = &k
	}
	return m, nil
}

func (e *Entry) apply(m MetaPatch) bool {
	changed := false
	set := func(dst *string, v *string) {
		if v != nil && *dst != *v {
			*dst = *v
			changed = true
		}
	}
	set(&e.Description, m.Description)
	set(&e.Label, m.Label)
	set(&e.Owner, m.Owner)
	set(&e.Kind, m.Kind)
	set(&e.SessionURL, m.SessionURL)
	if m.Session != nil && e.Session != *m.Session {
		e.Session = *m.Session
		changed = true
	}
	if changed {
		e.UpdatedAt = time.Now().UTC()
	}
	return changed
}

// Registry holds live registrations in memory and mirrors them to chisel's
// authfile. There is no persistence: providers re-register after a restart.
// Credentials are an HMAC of the name, so a re-registration after a restart
// hands out the same credentials a still-running chisel client already has.
type Registry struct {
	mu       sync.Mutex
	secret   []byte
	sentinel string
	byName   map[string]*Entry
	minPort  int
	maxPort  int
	authfile string
	// statePath, when set, mirrors names, ports, metadata and times to a JSON file (see LoadState).
	statePath string
	lastSave  time.Time
}

func NewRegistry(secret string, minPort, maxPort int, authfile string) (*Registry, error) {
	if secret == "" {
		return nil, errors.New("empty secret")
	}
	buf := make([]byte, 24)
	if _, err := rand.Read(buf); err != nil {
		return nil, err
	}
	r := &Registry{secret: []byte(secret), sentinel: hex.EncodeToString(buf), byName: map[string]*Entry{},
		minPort: minPort, maxPort: maxPort, authfile: authfile}
	return r, r.writeAuthfile()
}

func (r *Registry) derive(label string) string {
	m := hmac.New(sha256.New, r.secret)
	m.Write([]byte(label))
	return hex.EncodeToString(m.Sum(nil))[:32]
}

// Register is idempotent: an existing name keeps its port and credentials.
func (r *Registry) Register(name string) (Entry, bool, error) {
	return r.RegisterWith(name, MetaPatch{})
}

// RegisterWith registers a name and applies the metadata that was given. Re-registering an existing
// name updates only the fields present in the patch, so an old client (no metadata) never wipes them.
func (r *Registry) RegisterWith(name string, patch MetaPatch) (Entry, bool, error) {
	patch, err := patch.normalize()
	if err != nil {
		return Entry{}, false, err
	}
	if len(name) > maxNameLen || !NameRe.MatchString(name) {
		return Entry{}, false, fmt.Errorf("invalid name %q (want %s, max %d chars)", name, NameRe, maxNameLen)
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	if e, ok := r.byName[name]; ok {
		if e.apply(patch) {
			r.saveState()
		}
		return *e, false, nil
	}
	used := map[int]bool{}
	for _, e := range r.byName {
		used[e.Port] = true
	}
	port := 0
	for p := r.minPort; p <= r.maxPort; p++ {
		if !used[p] {
			port = p
			break
		}
	}
	if port == 0 {
		return Entry{}, false, errors.New("no free ports")
	}
	e := &Entry{Name: name, Port: port, User: "p-" + name, Password: r.derive("provider:" + name),
		ConsumerUser: "c-" + name, ConsumerPassword: r.derive("consumer:" + name)}
	e.RegisteredAt = time.Now().UTC()
	e.apply(patch)
	r.byName[name] = e
	if err := r.writeAuthfile(); err != nil {
		delete(r.byName, name)
		return Entry{}, false, err
	}
	r.saveState()
	return *e, true, nil
}

// Update changes metadata without re-registering. found is false when the name is not registered.
func (r *Registry) Update(name string, patch MetaPatch) (Entry, bool, error) {
	patch, err := patch.normalize()
	if err != nil {
		return Entry{}, true, err
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	e, ok := r.byName[name]
	if !ok {
		return Entry{}, false, nil
	}
	if e.apply(patch) {
		r.saveState()
	}
	return *e, true, nil
}

// persisted is the on-disk form: no credentials (they are derived from the admin token).
type persisted struct {
	Name         string    `json:"name"`
	Port         int       `json:"port"`
	RegisteredAt time.Time `json:"registeredAt"`
	LastSeen     time.Time `json:"lastSeen"`
	Description  string    `json:"description,omitempty"`
	Label        string    `json:"label,omitempty"`
	Owner        string    `json:"owner,omitempty"`
	Kind         string    `json:"kind,omitempty"`
	UpdatedAt    time.Time `json:"updatedAt"`
	Reconnects   int       `json:"reconnects"`
	Session      Session   `json:"session"`
	SessionURL   string    `json:"sessionUrl,omitempty"`
}

// LoadState enables persistence to path and restores entries from it (a missing file is fine).
// Restored providers start as not connected: connectedSince is runtime state of a live connection.
// The file lives on the container's disk, so it survives a router process restart but NOT a container
// restart or redeploy (the disk is ephemeral); then providers re-register and send their metadata again.
func (r *Registry) LoadState(path string) error {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.statePath = path
	b, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	if err != nil {
		return err
	}
	var list []persisted
	if err := json.Unmarshal(b, &list); err != nil {
		return fmt.Errorf("state file %s: %w", path, err)
	}
	for _, p := range list {
		if !NameRe.MatchString(p.Name) || p.Port < r.minPort || p.Port > r.maxPort {
			continue
		}
		r.byName[p.Name] = &Entry{Name: p.Name, Port: p.Port, User: "p-" + p.Name, Password: r.derive("provider:" + p.Name),
			ConsumerUser: "c-" + p.Name, ConsumerPassword: r.derive("consumer:" + p.Name),
			RegisteredAt: p.RegisteredAt, LastSeen: p.LastSeen, Description: p.Description, Label: p.Label,
			Owner: p.Owner, Kind: p.Kind, UpdatedAt: p.UpdatedAt, Reconnects: p.Reconnects, everConnected: p.Reconnects > 0, Session: p.Session, SessionURL: p.SessionURL}
	}
	return r.writeAuthfile()
}

// saveState must be called with r.mu held.
func (r *Registry) saveState() {
	if r.statePath == "" {
		return
	}
	list := make([]persisted, 0, len(r.byName))
	for _, e := range r.byName {
		list = append(list, persisted{e.Name, e.Port, e.RegisteredAt, e.LastSeen, e.Description, e.Label, e.Owner, e.Kind, e.UpdatedAt, e.Reconnects, e.Session, e.SessionURL})
	}
	sort.Slice(list, func(i, j int) bool { return list[i].Name < list[j].Name })
	b, _ := json.MarshalIndent(list, "", "  ")
	tmp := filepath.Join(filepath.Dir(r.statePath), ".bus-state.tmp")
	if os.WriteFile(tmp, b, 0o600) == nil {
		_ = os.Rename(tmp, r.statePath)
	}
}

func (r *Registry) Remove(name string) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	if _, ok := r.byName[name]; !ok {
		return false
	}
	delete(r.byName, name)
	_ = r.writeAuthfile()
	r.saveState()
	return true
}

func (r *Registry) Get(name string) (Entry, bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	e, ok := r.byName[name]
	if !ok {
		return Entry{}, false
	}
	return *e, true
}

func (r *Registry) List() []Entry {
	r.mu.Lock()
	defer r.mu.Unlock()
	out := make([]Entry, 0, len(r.byName))
	for _, e := range r.byName {
		out = append(out, *e)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Name < out[j].Name })
	return out
}

// AuthfileJSON builds chisel's authfile: {"user:pass": ["regex", ...]}.
// A sentinel user that matches nothing is always present: chisel turns
// authentication off when the user list is empty.
func (r *Registry) AuthfileJSON() []byte {
	m := map[string][]string{"_bus:" + r.sentinel: {"^$"}}
	for _, e := range r.byName {
		m[e.User+":"+e.Password] = []string{fmt.Sprintf(`^R:(0\.0\.0\.0|127\.0\.0\.1):%d$`, e.Port)}
		m[e.ConsumerUser+":"+e.ConsumerPassword] = []string{fmt.Sprintf(`^(127\.0\.0\.1|localhost):%d$`, e.Port)}
	}
	b, _ := json.MarshalIndent(m, "", "  ")
	return b
}

// writeAuthfile rewrites the file IN PLACE (truncate + one write). chisel
// watches the file with fsnotify and reloads on Write events only; an
// atomic rename would replace the inode and silently end the watch.
func (r *Registry) writeAuthfile() error {
	if r.authfile == "" {
		return nil
	}
	return os.WriteFile(r.authfile, r.AuthfileJSON(), 0o600)
}

// Touch records that a provider's reverse port was seen open.
func (r *Registry) Touch(name string, t time.Time) { r.Observe(name, true, t) }

// Observe records one probe of a provider's reverse port. A not-connected -> connected transition sets
// ConnectedSince (reset on every reconnect) and counts a reconnect after the first connection;
// a connected -> not-connected transition clears it. LastSeen advances while it is up.
func (r *Registry) Observe(name string, up bool, t time.Time) {
	r.mu.Lock()
	defer r.mu.Unlock()
	e, ok := r.byName[name]
	if !ok {
		return
	}
	t = t.UTC()
	changed := false
	if up {
		e.LastSeen = t
		if !e.Connected {
			e.Connected, e.ConnectedSince = true, t
			if e.everConnected {
				e.Reconnects++
				changed = true
			}
			e.everConnected = true
		}
	} else if e.Connected {
		e.Connected, e.ConnectedSince = false, time.Time{}
		changed = true
	}
	// LastSeen is written to disk only on a notable change or at most once a minute.
	if changed || (up && t.Sub(r.lastSave) > time.Minute) {
		r.lastSave = t
		r.saveState()
	}
}
