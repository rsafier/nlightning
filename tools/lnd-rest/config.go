package main

import (
	"errors"
	"flag"
	"fmt"
	"io"
	"net"
	"strings"
	"time"
)

// Config holds the sidecar's settings. Every flag has a safe default except
// the backend TLS certificate, which must be given so the backend is verified.
type Config struct {
	// Listen is the REST listener's host:port.
	Listen string
	// TLSDir holds the self-signed listener certificate (tls.cert, tls.key)
	// generated on the first start.
	TLSDir string
	// RestTLSCert and RestTLSKey name a provided listener certificate and key
	// (PEM) instead of the generated one; both or neither.
	RestTLSCert, RestTLSKey string
	// TLSExtraIPs and TLSExtraDomains are added to a generated certificate.
	TLSExtraIPs, TLSExtraDomains []string
	// NoTLS serves plain HTTP; refused unless Listen is a loopback address.
	NoTLS bool

	// Backend is NLightning's LND-compatible gRPC host:port.
	Backend string
	// BackendTLSCert is the backend's trusted certificate (PEM).
	BackendTLSCert string
	// BackendTLSServerName overrides the name checked in the backend
	// certificate.
	BackendTLSServerName string

	// CORSOrigins are the allowed browser origins ("*" for any); empty turns
	// CORS off, as LND's restcors.
	CORSOrigins []string

	// MaxBodyBytes bounds a REST request body.
	MaxBodyBytes int64
	// MaxMsgBytes bounds a gRPC message in either direction.
	MaxMsgBytes int
	// MaxConns bounds the open client connections (0 = no bound).
	MaxConns int
	// ReadHeaderTimeout bounds reading a request's headers.
	ReadHeaderTimeout time.Duration
	// BodyReadTimeout bounds reading a request's body.
	BodyReadTimeout time.Duration
	// IdleTimeout closes idle keep-alive connections.
	IdleTimeout time.Duration
	// WSPingInterval and WSPongWait are the WebSocket keep-alive (LND's
	// ws-ping-interval and ws-pong-wait); 0 turns pings off.
	WSPingInterval, WSPongWait time.Duration
	// ShutdownTimeout bounds the graceful shutdown.
	ShutdownTimeout time.Duration

	// LogRequests logs each request's method, path, status and duration
	// (never headers, bodies or credentials).
	LogRequests bool
	// Version prints the version and exits.
	Version bool
}

type stringList []string

func (s *stringList) String() string { return strings.Join(*s, ",") }
func (s *stringList) Set(v string) error {
	for _, part := range strings.Split(v, ",") {
		if part = strings.TrimSpace(part); part != "" {
			*s = append(*s, part)
		}
	}
	return nil
}

func parseConfig(args []string, output io.Writer) (Config, error) {
	var c Config
	var extraIPs, extraDomains, cors stringList
	f := flag.NewFlagSet("nltg-lnd-rest", flag.ContinueOnError)
	f.SetOutput(output)
	f.StringVar(&c.Listen, "listen", "127.0.0.1:8080", "REST listener host:port")
	f.StringVar(&c.TLSDir, "tls-dir", "./lnd-rest-tls", "directory of the generated listener certificate (tls.cert, tls.key)")
	f.StringVar(&c.RestTLSCert, "rest-tls-cert", "", "provided listener certificate PEM (with --rest-tls-key; no certificate is generated)")
	f.StringVar(&c.RestTLSKey, "rest-tls-key", "", "provided listener private key PEM (with --rest-tls-cert)")
	f.Var(&extraIPs, "tls-extra-ip", "extra IP address for the generated certificate (repeatable or comma separated)")
	f.Var(&extraDomains, "tls-extra-domain", "extra DNS name for the generated certificate (repeatable or comma separated)")
	f.BoolVar(&c.NoTLS, "no-tls", false, "serve plain HTTP (loopback listen addresses only)")
	f.StringVar(&c.Backend, "backend", "127.0.0.1:10009", "NLightning LND-compatible gRPC host:port")
	f.StringVar(&c.BackendTLSCert, "tls-cert", "", "trusted backend TLS certificate PEM (required)")
	f.StringVar(&c.BackendTLSServerName, "tls-server-name", "", "backend TLS server name override")
	f.Var(&cors, "cors-origin", "allowed CORS origin, or * for any (repeatable; default: CORS off)")
	f.Int64Var(&c.MaxBodyBytes, "max-body-bytes", 32<<20, "largest REST request body in bytes")
	f.IntVar(&c.MaxMsgBytes, "max-msg-bytes", 200<<20, "largest gRPC message in bytes (LND's limit is 200 MiB)")
	f.IntVar(&c.MaxConns, "max-conns", 512, "most open client connections (0 = unbounded)")
	f.DurationVar(&c.ReadHeaderTimeout, "read-header-timeout", 10*time.Second, "time allowed to read a request's headers")
	f.DurationVar(&c.BodyReadTimeout, "body-read-timeout", 30*time.Second, "time allowed to read a request's body")
	f.DurationVar(&c.IdleTimeout, "idle-timeout", 2*time.Minute, "idle keep-alive connection timeout")
	f.DurationVar(&c.WSPingInterval, "ws-ping-interval", 30*time.Second, "WebSocket ping interval (0 = no pings)")
	f.DurationVar(&c.WSPongWait, "ws-pong-wait", 5*time.Second, "WebSocket pong wait before the connection is closed")
	f.DurationVar(&c.ShutdownTimeout, "shutdown-timeout", 10*time.Second, "graceful shutdown limit")
	f.BoolVar(&c.LogRequests, "log-requests", false, "log each request's method, path, status and duration (never headers or bodies)")
	f.BoolVar(&c.Version, "version", false, "print the version and exit")
	if err := f.Parse(args); err != nil {
		return c, err
	}
	if f.NArg() != 0 {
		return c, errors.New("unexpected positional arguments")
	}
	c.TLSExtraIPs, c.TLSExtraDomains, c.CORSOrigins = extraIPs, extraDomains, cors
	if c.Version {
		return c, nil
	}
	return c, c.validate()
}

func (c Config) validate() error {
	if c.BackendTLSCert == "" {
		return errors.New("--tls-cert (the backend's TLS certificate) is required")
	}
	if c.Backend == "" {
		return errors.New("--backend is required")
	}
	host, port, err := net.SplitHostPort(c.Listen)
	if err != nil {
		return fmt.Errorf("--listen: %w", err)
	}
	if port == "" {
		return errors.New("--listen needs a port")
	}
	if c.NoTLS && !isLoopbackHost(host) {
		return fmt.Errorf("--no-tls is refused on the non-loopback listen address %q: keep TLS on", c.Listen)
	}
	if c.NoTLS && (c.RestTLSCert != "" || c.RestTLSKey != "") {
		return errors.New("--no-tls cannot be combined with --rest-tls-cert/--rest-tls-key")
	}
	if (c.RestTLSCert == "") != (c.RestTLSKey == "") {
		return errors.New("--rest-tls-cert and --rest-tls-key go together")
	}
	if c.RestTLSCert == "" && !c.NoTLS && c.TLSDir == "" {
		return errors.New("--tls-dir is required for the generated certificate")
	}
	for _, ip := range c.TLSExtraIPs {
		if net.ParseIP(ip) == nil {
			return fmt.Errorf("--tls-extra-ip %q is not an IP address", ip)
		}
	}
	if c.MaxBodyBytes <= 0 || c.MaxMsgBytes <= 0 || c.MaxConns < 0 {
		return errors.New("--max-body-bytes and --max-msg-bytes must be positive, --max-conns not negative")
	}
	if c.ReadHeaderTimeout <= 0 || c.BodyReadTimeout <= 0 || c.IdleTimeout <= 0 || c.ShutdownTimeout <= 0 {
		return errors.New("timeouts must be positive")
	}
	if c.WSPingInterval < 0 || c.WSPongWait < 0 {
		return errors.New("--ws-ping-interval and --ws-pong-wait must not be negative")
	}
	return nil
}

// isLoopbackHost reports whether a listen host binds only loopback
// interfaces. An empty host or an unspecified address binds all interfaces.
func isLoopbackHost(host string) bool {
	if strings.EqualFold(host, "localhost") {
		return true
	}
	ip := net.ParseIP(host)
	return ip != nil && ip.IsLoopback()
}
