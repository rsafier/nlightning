package main

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"errors"
	"fmt"
	"net"
	"os"
	"strings"
	"time"

	ap "github.com/rsafier/nlightning/tools/lnc/internal/autopilotserverrpc"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials"
	"google.golang.org/grpc/credentials/insecure"
)

// Lightning Labs' autopilot servers, as litd chooses them (lightning-terminal
// terminal.go MainnetServer/TestnetServer). litd has no default for signet or
// regtest: it refuses to start with autopilot enabled there unless an address
// is configured. Lightning Labs runs no signet server.
const (
	autopilotMainnetServer = "autopilot.lightning.finance:12010"
	autopilotTestnetServer = "test.autopilot.lightning.finance:12010"
)

// The litd version the bridge reports to the autopilot server (litd master's
// version when this emulation was written; the server requires 0.8.0 or
// newer) and the LND API version NLightning's backend implements.
var (
	emulatedLitVersion = &ap.Version{Major: 0, Minor: 17, Patch: 6}
	emulatedLndVersion = &ap.Version{Major: 0, Minor: 21, Patch: 4}
)

// autopilotServer is the part of Lightning Labs' autopilot server API
// (autopilotserverrpc) the bridge uses, behind an interface for tests.
type autopilotServer interface {
	Terms(ctx context.Context) (*ap.TermsResponse, error)
	ListFeatures(ctx context.Context) (*ap.ListFeaturesResponse, error)
	RegisterSession(ctx context.Context, req *ap.RegisterSessionRequest) (*ap.RegisterSessionResponse, error)
	ActivateSession(ctx context.Context, responder []byte) error
	RevokeSession(ctx context.Context, responder []byte) error
	Address() string
}

// resolveAutopilotServer expands the --autopilot-server aliases.
func resolveAutopilotServer(value string) string {
	switch strings.ToLower(value) {
	case "mainnet":
		return autopilotMainnetServer
	case "testnet":
		return autopilotTestnetServer
	}
	return value
}

type grpcAutopilotServer struct {
	address string
	dial    []grpc.DialOption
}

// newAutopilotServerClient builds a client for the server at address, with
// TLS against the system roots, or against tlsCert when given. Plain text is
// allowed only to a loopback address (tests and local development servers).
func newAutopilotServerClient(address, tlsCert string, insecureLoopback bool) (autopilotServer, error) {
	host, _, err := net.SplitHostPort(address)
	if err != nil {
		return nil, fmt.Errorf("autopilot server address: %w", err)
	}
	var creds credentials.TransportCredentials
	switch {
	case insecureLoopback:
		ip := net.ParseIP(host)
		if host != "localhost" && (ip == nil || !ip.IsLoopback()) {
			return nil, errors.New("plain-text autopilot server connections are allowed only to loopback addresses")
		}
		creds = insecure.NewCredentials()
	case tlsCert != "":
		pem, err := os.ReadFile(tlsCert)
		if err != nil {
			return nil, err
		}
		roots := x509.NewCertPool()
		if !roots.AppendCertsFromPEM(pem) {
			return nil, errors.New("autopilot server TLS certificate contains no certificates")
		}
		creds = credentials.NewTLS(&tls.Config{RootCAs: roots, MinVersion: tls.VersionTLS12})
	default:
		creds = credentials.NewTLS(&tls.Config{MinVersion: tls.VersionTLS12})
	}
	return &grpcAutopilotServer{address: address, dial: []grpc.DialOption{grpc.WithTransportCredentials(creds)}}, nil
}

func (c *grpcAutopilotServer) Address() string { return c.address }

// invoke dials, calls one method and closes, as litd's client does for every
// call (autopilotserver/client.go getClientConn).
func (c *grpcAutopilotServer) invoke(ctx context.Context, method string, req, resp any) error {
	ctx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()
	conn, err := grpc.DialContext(ctx, c.address, c.dial...)
	if err != nil {
		return fmt.Errorf("unable to connect to the autopilot server: %w", err)
	}
	defer conn.Close()
	return conn.Invoke(ctx, "/autopilotserverrpc.Autopilot/"+method, req, resp)
}

func (c *grpcAutopilotServer) Terms(ctx context.Context) (*ap.TermsResponse, error) {
	resp := &ap.TermsResponse{}
	return resp, c.invoke(ctx, "Terms", &ap.TermsRequest{}, resp)
}

func (c *grpcAutopilotServer) ListFeatures(ctx context.Context) (*ap.ListFeaturesResponse, error) {
	resp := &ap.ListFeaturesResponse{}
	return resp, c.invoke(ctx, "ListFeatures", &ap.ListFeaturesRequest{}, resp)
}

func (c *grpcAutopilotServer) RegisterSession(ctx context.Context, req *ap.RegisterSessionRequest) (*ap.RegisterSessionResponse, error) {
	resp := &ap.RegisterSessionResponse{}
	return resp, c.invoke(ctx, "RegisterSession", req, resp)
}

func (c *grpcAutopilotServer) ActivateSession(ctx context.Context, responder []byte) error {
	return c.invoke(ctx, "ActivateSession", &ap.ActivateSessionRequest{ResponderPubKey: responder}, &ap.ActivateSessionResponse{})
}

func (c *grpcAutopilotServer) RevokeSession(ctx context.Context, responder []byte) error {
	return c.invoke(ctx, "RevokeSession", &ap.RevokeSessionRequest{ResponderPubKey: responder}, &ap.RevokeSessionResponse{})
}

// versionAtLeast reports whether v >= min.
func versionAtLeast(v, min *ap.Version) bool {
	if v.Major != min.Major {
		return v.Major > min.Major
	}
	if v.Minor != min.Minor {
		return v.Minor > min.Minor
	}
	return v.Patch >= min.Patch
}

// checkTerms refuses a server that requires a newer litd than the bridge
// emulates, as litd's client does at start.
func checkTerms(ctx context.Context, s autopilotServer) error {
	terms, err := s.Terms(ctx)
	if err != nil {
		return fmt.Errorf("autopilot server terms: %w", err)
	}
	if min := terms.GetMinRequiredVersion(); min != nil && !versionAtLeast(emulatedLitVersion, min) {
		return fmt.Errorf("the autopilot server requires litd v%d.%d.%d; the bridge emulates v%d.%d.%d",
			min.Major, min.Minor, min.Patch, emulatedLitVersion.Major, emulatedLitVersion.Minor, emulatedLitVersion.Patch)
	}
	return nil
}
