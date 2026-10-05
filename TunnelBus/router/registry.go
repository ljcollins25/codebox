package main

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"regexp"
	"sort"
	"sync"
	"time"
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
	if len(name) > maxNameLen || !NameRe.MatchString(name) {
		return Entry{}, false, fmt.Errorf("invalid name %q (want %s, max %d chars)", name, NameRe, maxNameLen)
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	if e, ok := r.byName[name]; ok {
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
	r.byName[name] = e
	if err := r.writeAuthfile(); err != nil {
		delete(r.byName, name)
		return Entry{}, false, err
	}
	return *e, true, nil
}

func (r *Registry) Remove(name string) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	if _, ok := r.byName[name]; !ok {
		return false
	}
	delete(r.byName, name)
	_ = r.writeAuthfile()
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
func (r *Registry) Touch(name string, t time.Time) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if e, ok := r.byName[name]; ok {
		e.LastSeen = t.UTC()
	}
}
