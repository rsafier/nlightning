package main

import (
	"bytes"
	"crypto/tls"
	"io"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"
)

func TestGeneratedCertificateIsPrivateAndReused(t *testing.T) {
	backend := startFakeBackend(t)
	c := testConfig(t, backend)
	s, client, _ := startSidecar(t, c)

	resp, body := doRequest(t, client, "GET", baseURL(s)+"/v1/getinfo", "", nil)
	if resp.StatusCode != 200 {
		t.Fatalf("status %d: %s", resp.StatusCode, body)
	}
	if runtime.GOOS != "windows" {
		for path, want := range map[string]os.FileMode{
			c.TLSDir:                              0o700,
			filepath.Join(c.TLSDir, keyFileName):  0o600,
			filepath.Join(c.TLSDir, certFileName): 0o644,
		} {
			info, err := os.Stat(path)
			if err != nil {
				t.Fatal(err)
			}
			if info.Mode().Perm() != want {
				t.Errorf("%s mode %o, want %o", path, info.Mode().Perm(), want)
			}
		}
	}
	first, _ := os.ReadFile(filepath.Join(c.TLSDir, certFileName))
	ctx := t.Context()
	if err := s.Shutdown(ctx); err != nil {
		t.Fatal(err)
	}

	s2, _, _ := startSidecar(t, c)
	second, _ := os.ReadFile(filepath.Join(c.TLSDir, certFileName))
	if !bytes.Equal(first, second) || s2.CertPath != s.CertPath {
		t.Fatal("a restart replaced a valid certificate")
	}
}

func TestExpiredGeneratedCertificateIsReplaced(t *testing.T) {
	dir := filepath.Join(t.TempDir(), "tls")
	old := time.Now().Add(-2 * certValidity)
	if _, err := ensureSelfSignedCert(dir, nil, nil, nil, old); err != nil {
		t.Fatal(err)
	}
	before, _ := os.ReadFile(filepath.Join(dir, certFileName))

	created, err := ensureSelfSignedCert(dir, nil, nil, nil, time.Now())

	after, _ := os.ReadFile(filepath.Join(dir, certFileName))
	if err != nil || !created || bytes.Equal(before, after) {
		t.Fatalf("created=%v err=%v", created, err)
	}
}

func TestGeneratedCertificateCoversLoopbackAndExtras(t *testing.T) {
	dir := t.TempDir()
	if _, err := ensureSelfSignedCert(dir, net.ParseIP("192.0.2.10"), []string{"198.51.100.7"}, []string{"node.example"}, time.Now()); err != nil {
		t.Fatal(err)
	}
	data, _ := os.ReadFile(filepath.Join(dir, certFileName))
	cert, err := parseCertPEM(data)
	if err != nil {
		t.Fatal(err)
	}
	for _, host := range []string{"127.0.0.1", "::1", "localhost", "192.0.2.10", "198.51.100.7", "node.example"} {
		if err := cert.VerifyHostname(host); err != nil {
			t.Errorf("%s: %v", host, err)
		}
	}
}

func TestProvidedCertificateIsUsed(t *testing.T) {
	backend := startFakeBackend(t)
	provided := t.TempDir()
	if _, err := ensureSelfSignedCert(provided, nil, nil, nil, time.Now()); err != nil {
		t.Fatal(err)
	}
	c := testConfig(t, backend)
	c.RestTLSCert, c.RestTLSKey = filepath.Join(provided, certFileName), filepath.Join(provided, keyFileName)

	s, client, _ := startSidecar(t, c)

	if s.CertPath != c.RestTLSCert {
		t.Fatalf("cert path %s", s.CertPath)
	}
	if _, err := os.Stat(c.TLSDir); !os.IsNotExist(err) {
		t.Fatal("a certificate was generated although one was provided")
	}
	if resp, _ := doRequest(t, client, "GET", baseURL(s)+"/v1/getinfo", "", nil); resp.StatusCode != 200 {
		t.Fatalf("status %d", resp.StatusCode)
	}
}

func TestGroupReadableKeyIsRefused(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("POSIX permissions")
	}
	backend := startFakeBackend(t)
	provided := t.TempDir()
	if _, err := ensureSelfSignedCert(provided, nil, nil, nil, time.Now()); err != nil {
		t.Fatal(err)
	}
	if err := os.Chmod(filepath.Join(provided, keyFileName), 0o640); err != nil {
		t.Fatal(err)
	}
	c := testConfig(t, backend)
	c.RestTLSCert, c.RestTLSKey = filepath.Join(provided, certFileName), filepath.Join(provided, keyFileName)

	_, err := Start(c)

	if err == nil || !strings.Contains(err.Error(), "chmod 600") {
		t.Fatalf("err %v", err)
	}
}

func TestListenerIsHTTPSOnlyAndHTTP1(t *testing.T) {
	backend := startFakeBackend(t)
	s, _, _ := startSidecar(t, testConfig(t, backend))

	resp, err := http.Get("http://" + s.Addr().String() + "/v1/getinfo")
	if err == nil {
		body, _ := io.ReadAll(resp.Body)
		resp.Body.Close()
		if resp.StatusCode != http.StatusBadRequest {
			t.Fatalf("plain HTTP got %d %s", resp.StatusCode, body)
		}
	}
	conn, err := tls.Dial("tcp", s.Addr().String(), &tls.Config{RootCAs: certPool(t, s.CertPath), NextProtos: []string{"h2", "http/1.1"}})
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	if p := conn.ConnectionState().NegotiatedProtocol; p == "h2" {
		t.Fatal("HTTP/2 negotiated")
	}
	if len(backend.recorded()) != 0 {
		t.Fatal("plain HTTP reached the backend")
	}
}

func TestBackendCertificateIsVerified(t *testing.T) {
	backend := startFakeBackend(t)
	other := t.TempDir()
	if _, err := ensureSelfSignedCert(other, nil, nil, nil, time.Now()); err != nil {
		t.Fatal(err)
	}
	c := testConfig(t, backend)
	c.BackendTLSCert = filepath.Join(other, certFileName)
	s, client, _ := startSidecar(t, c)

	resp, body := doRequest(t, client, "GET", baseURL(s)+"/v1/getinfo", "", nil)

	if resp.StatusCode != http.StatusServiceUnavailable {
		t.Fatalf("status %d: %s", resp.StatusCode, body)
	}
	if len(backend.recorded()) != 0 {
		t.Fatal("a call reached a backend whose certificate is not trusted")
	}
}

func TestConfigValidation(t *testing.T) {
	cases := []struct {
		name string
		args []string
		ok   bool
	}{
		{"defaults with backend cert", []string{"--tls-cert", "c.pem"}, true},
		{"backend cert required", nil, false},
		{"plain HTTP on loopback", []string{"--tls-cert", "c.pem", "--no-tls"}, true},
		{"plain HTTP on IPv6 loopback", []string{"--tls-cert", "c.pem", "--no-tls", "--listen", "[::1]:8080"}, true},
		{"plain HTTP on localhost", []string{"--tls-cert", "c.pem", "--no-tls", "--listen", "localhost:8080"}, true},
		{"plain HTTP on all interfaces", []string{"--tls-cert", "c.pem", "--no-tls", "--listen", "0.0.0.0:8080"}, false},
		{"plain HTTP with empty host", []string{"--tls-cert", "c.pem", "--no-tls", "--listen", ":8080"}, false},
		{"plain HTTP on a LAN address", []string{"--tls-cert", "c.pem", "--no-tls", "--listen", "192.168.1.5:8080"}, false},
		{"TLS on all interfaces", []string{"--tls-cert", "c.pem", "--listen", "0.0.0.0:8080"}, true},
		{"cert without key", []string{"--tls-cert", "c.pem", "--rest-tls-cert", "r.pem"}, false},
		{"bad extra ip", []string{"--tls-cert", "c.pem", "--tls-extra-ip", "nope"}, false},
		{"zero body limit", []string{"--tls-cert", "c.pem", "--max-body-bytes", "0"}, false},
		{"positional argument", []string{"--tls-cert", "c.pem", "extra"}, false},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			_, err := parseConfig(tc.args, io.Discard)
			if (err == nil) != tc.ok {
				t.Fatalf("err %v", err)
			}
		})
	}
}

func TestPlainHTTPOnLoopbackServes(t *testing.T) {
	backend := startFakeBackend(t)
	c := testConfig(t, backend)
	c.NoTLS = true
	s, client, _ := startSidecar(t, c)

	resp, body := doRequest(t, client, "GET", baseURL(s)+"/v1/getinfo", "", nil)

	if resp.StatusCode != 200 {
		t.Fatalf("status %d: %s", resp.StatusCode, body)
	}
	if _, err := os.Stat(c.TLSDir); !os.IsNotExist(err) {
		t.Fatal("--no-tls generated a certificate")
	}
}
