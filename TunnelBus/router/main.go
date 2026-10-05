package main

import (
	"log"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

func env(k, def string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return def
}

func main() {
	token := os.Getenv("ADMIN_TOKEN")
	if token == "" {
		log.Fatal("ADMIN_TOKEN is required")
	}
	minP, _ := strconv.Atoi(env("PORT_MIN", "20000"))
	maxP, _ := strconv.Atoi(env("PORT_MAX", "20999"))
	authfile := env("AUTHFILE", filepath.Join(os.TempDir(), "chisel-users.json"))
	reg, err := NewRegistry(token, minP, maxP, authfile)
	if err != nil {
		log.Fatal(err)
	}
	rt := &Router{Reg: reg, AdminToken: token, started: time.Now(),
		BaseDomain: strings.ToLower(strings.Trim(os.Getenv("BUS_BASE_DOMAIN"), ". ")), ControlHost: strings.ToLower(os.Getenv("BUS_CONTROL_HOST"))}
	if bin := env("CHISEL_BIN", "chisel"); bin != "none" {
		cp := env("CHISEL_PORT", "8081")
		rt.ChiselAddr = "127.0.0.1:" + cp
		go superviseChisel(bin, cp, authfile)
	}
	addr := env("LISTEN", ":8080")
	log.Printf("tunnel bus router on %s (ports %d-%d)", addr, minP, maxP)
	srv := &http.Server{Addr: addr, Handler: rt, ReadHeaderTimeout: 15 * time.Second}
	log.Fatal(srv.ListenAndServe())
}

// superviseChisel runs the stock chisel server and restarts it if it exits.
func superviseChisel(bin, port, authfile string) {
	for {
		cmd := exec.Command(bin, "server", "--reverse", "--host", "127.0.0.1", "--port", port,
			"--authfile", authfile, "--keepalive", "25s")
		cmd.Stdout, cmd.Stderr = os.Stdout, os.Stderr
		log.Printf("starting chisel: %v", cmd.Args)
		err := cmd.Run()
		log.Printf("chisel exited: %v; restarting in 2s", err)
		time.Sleep(2 * time.Second)
	}
}
