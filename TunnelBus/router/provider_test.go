package main

import (
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

// The provider scripts have a dry-run mode that prints what they would send.
// Each script is tested when its interpreter exists on this machine.

func providerDir(t *testing.T) string {
	t.Helper()
	d, err := filepath.Abs(filepath.Join("..", "provider"))
	if err != nil {
		t.Fatal(err)
	}
	return d
}

func runScript(t *testing.T, cmd *exec.Cmd, withAccess bool) string {
	t.Helper()
	cmd.Env = append(os.Environ(), "CF_ACCESS_CLIENT_ID=", "CF_ACCESS_CLIENT_SECRET=")
	if withAccess {
		cmd.Env = append(cmd.Env, "CF_ACCESS_CLIENT_ID=abc.access", "CF_ACCESS_CLIENT_SECRET=s3cret")
	}
	out, err := cmd.CombinedOutput()
	if err != nil {
		t.Fatalf("%v: %v\n%s", cmd.Args, err, out)
	}
	return strings.ReplaceAll(string(out), "\r", "")
}

func checkDry(t *testing.T, run func(access bool) string) {
	t.Helper()
	with := run(true)
	for _, want := range []string{
		"REGISTER-HEADER: CF-Access-Client-Id: abc.access",
		"REGISTER-HEADER: CF-Access-Client-Secret: s3cret",
		"REGISTER-URL: https://b.example.com/_api/register",
		"--header CF-Access-Client-Id: abc.access --header CF-Access-Client-Secret: s3cret https://b.example.com/_chisel R:PORT:localhost:3000",
		"--keepalive 25s",
	} {
		if !strings.Contains(with, want) {
			t.Errorf("missing %q in:\n%s", want, with)
		}
	}
	without := run(false)
	if strings.Contains(without, "CF-Access") || !strings.Contains(without, "R:PORT:localhost:3000") {
		t.Errorf("without service token:\n%s", without)
	}
}

func TestProviderScriptBash(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("bash on Windows may be WSL, which cannot take Windows paths")
	}
	bash, err := exec.LookPath("bash")
	if err != nil {
		t.Skip("no bash")
	}
	script := filepath.Join(providerDir(t), "tunnel-bus-provider.sh")
	checkDry(t, func(access bool) string {
		return runScript(t, exec.Command(bash, script, "--bus", "https://b.example.com/", "--name", "x", "--port", "3000", "--token", "t", "--dry-run"), access)
	})
}

func TestProviderScriptPowerShell(t *testing.T) {
	ps, err := exec.LookPath("pwsh")
	if err != nil {
		if ps, err = exec.LookPath("powershell"); err != nil {
			t.Skip("no PowerShell")
		}
	}
	script := filepath.Join(providerDir(t), "tunnel-bus-provider.ps1")
	checkDry(t, func(access bool) string {
		return runScript(t, exec.Command(ps, "-NoProfile", "-File", script, "-Bus", "https://b.example.com/", "-Name", "x", "-Port", "3000", "-Token", "t", "-DryRun"), access)
	})
}
