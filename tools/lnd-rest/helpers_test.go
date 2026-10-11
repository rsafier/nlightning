package main

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"sync"
	"testing"
	"time"

	"github.com/btcsuite/btclog/v2"
	"github.com/lightningnetwork/lnd/lnrpc"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"
)

// call is one RPC the fake backend received.
type call struct {
	method string
	md     metadata.MD
}

// fakeBackend is a TLS gRPC server standing in for NLightning. It records
// every call (method and metadata) and answers a few Lightning methods;
// everything else answers UNIMPLEMENTED, as NLightning does.
type fakeBackend struct {
	lnrpc.UnimplementedLightningServer

	addr     string
	certPath string
	server   *grpc.Server

	mu       sync.Mutex
	calls    []call
	invoices []*lnrpc.Invoice
	lastAdd  *lnrpc.Invoice
	lastList *lnrpc.ListInvoiceRequest
	// subscribed is signalled when SubscribeInvoices starts.
	subscribed chan struct{}
}

func (f *fakeBackend) record(ctx context.Context, method string) {
	md, _ := metadata.FromIncomingContext(ctx)
	f.mu.Lock()
	defer f.mu.Unlock()
	f.calls = append(f.calls, call{method: method, md: md.Copy()})
}

func (f *fakeBackend) recorded() []call {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]call(nil), f.calls...)
}

func (f *fakeBackend) lastCall(t *testing.T, method string) call {
	t.Helper()
	calls := f.recorded()
	for i := len(calls) - 1; i >= 0; i-- {
		if calls[i].method == method {
			return calls[i]
		}
	}
	t.Fatalf("backend never received %s (got %d calls)", method, len(calls))
	return call{}
}

func (f *fakeBackend) GetInfo(context.Context, *lnrpc.GetInfoRequest) (*lnrpc.GetInfoResponse, error) {
	return &lnrpc.GetInfoResponse{
		Version:             "0.21.4-beta nlightning-test",
		IdentityPubkey:      "02aa",
		Alias:               "fake",
		BlockHeight:         321,
		BestHeaderTimestamp: 1700000000,
		Chains:              []*lnrpc.Chain{{Chain: "bitcoin", Network: "signet"}},
	}, nil
}

func (f *fakeBackend) AddInvoice(_ context.Context, in *lnrpc.Invoice) (*lnrpc.AddInvoiceResponse, error) {
	f.mu.Lock()
	f.lastAdd = in
	f.mu.Unlock()
	return &lnrpc.AddInvoiceResponse{RHash: []byte{0xfb, 0xff, 0x01}, PaymentRequest: "lntbs1fake", AddIndex: 7}, nil
}

func (f *fakeBackend) ListInvoices(_ context.Context, in *lnrpc.ListInvoiceRequest) (*lnrpc.ListInvoiceResponse, error) {
	f.mu.Lock()
	f.lastList = in
	f.mu.Unlock()
	return &lnrpc.ListInvoiceResponse{}, nil
}

func (f *fakeBackend) ListChannels(context.Context, *lnrpc.ListChannelsRequest) (*lnrpc.ListChannelsResponse, error) {
	return &lnrpc.ListChannelsResponse{Channels: []*lnrpc.Channel{{
		ChanId: 18446744073709551615, CommitmentType: lnrpc.CommitmentType_ANCHORS, Active: true,
	}}}, nil
}

func (f *fakeBackend) SubscribeInvoices(_ *lnrpc.InvoiceSubscription, stream lnrpc.Lightning_SubscribeInvoicesServer) error {
	f.mu.Lock()
	invoices := append([]*lnrpc.Invoice(nil), f.invoices...)
	f.mu.Unlock()
	select {
	case f.subscribed <- struct{}{}:
	default:
	}
	for _, inv := range invoices {
		if err := stream.Send(inv); err != nil {
			return err
		}
	}
	<-stream.Context().Done()
	return stream.Context().Err()
}

// startFakeBackend starts the fake on a random loopback port with a fresh
// self-signed certificate.
func startFakeBackend(t *testing.T) *fakeBackend {
	t.Helper()
	dir := t.TempDir()
	if _, err := ensureSelfSignedCert(dir, nil, nil, nil, time.Now()); err != nil {
		t.Fatal(err)
	}
	pair, err := tls.LoadX509KeyPair(filepath.Join(dir, certFileName), filepath.Join(dir, keyFileName))
	if err != nil {
		t.Fatal(err)
	}
	f := &fakeBackend{certPath: filepath.Join(dir, certFileName), subscribed: make(chan struct{}, 1)}
	f.server = grpc.NewServer(
		grpc.Creds(credentials.NewTLS(&tls.Config{Certificates: []tls.Certificate{pair}, MinVersion: tls.VersionTLS12})),
		grpc.UnaryInterceptor(func(ctx context.Context, req any, info *grpc.UnaryServerInfo, h grpc.UnaryHandler) (any, error) {
			f.record(ctx, info.FullMethod)
			return h(ctx, req)
		}),
		grpc.StreamInterceptor(func(srv any, ss grpc.ServerStream, info *grpc.StreamServerInfo, h grpc.StreamHandler) error {
			f.record(ss.Context(), info.FullMethod)
			return h(srv, ss)
		}),
		// Services NLightning does not register answer like grpc-go does.
		grpc.UnknownServiceHandler(func(_ any, ss grpc.ServerStream) error {
			method, _ := grpc.MethodFromServerStream(ss)
			return status.Errorf(codes.Unimplemented, "unknown method %s", method)
		}),
	)
	lnrpc.RegisterLightningServer(f.server, f)
	lis, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	f.addr = lis.Addr().String()
	go func() { _ = f.server.Serve(lis) }()
	t.Cleanup(f.server.Stop)
	return f
}

// testConfig is a sidecar configuration for a fake backend, listening on a
// random loopback port.
func testConfig(t *testing.T, backend *fakeBackend) Config {
	c, err := parseConfig([]string{
		"--listen", "127.0.0.1:0",
		"--tls-dir", filepath.Join(t.TempDir(), "rest-tls"),
		"--backend", backend.addr,
		"--tls-cert", backend.certPath,
		"--ws-ping-interval", "0",
		"--shutdown-timeout", "2s",
	}, io.Discard)
	if err != nil {
		t.Fatal(err)
	}
	return c
}

// logSink collects the sidecar's log lines.
type logSink struct {
	mu    sync.Mutex
	lines []string
}

func (l *logSink) logf(format string, args ...any) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.lines = append(l.lines, fmt.Sprintf(format, args...))
}

func (l *logSink) all() []string {
	l.mu.Lock()
	defer l.mu.Unlock()
	return append([]string(nil), l.lines...)
}

// startSidecar starts the sidecar and returns it with an HTTPS client that
// trusts its generated certificate.
func startSidecar(t *testing.T, c Config) (*Server, *http.Client, *logSink) {
	t.Helper()
	sink := &logSink{}
	logger := btclog.NewSLogger(btclog.NewDefaultHandler(io.Discard))
	s, err := startWithLogger(c, sink.logf, logger)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = s.Shutdown(ctx)
	})
	client := &http.Client{Timeout: 10 * time.Second}
	if !c.NoTLS {
		client.Transport = &http.Transport{TLSClientConfig: &tls.Config{RootCAs: certPool(t, s.CertPath)}}
	}
	return s, client, sink
}

func certPool(t *testing.T, path string) *x509.CertPool {
	t.Helper()
	pemBytes, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	pool := x509.NewCertPool()
	if !pool.AppendCertsFromPEM(pemBytes) {
		t.Fatal("no certificate in", path)
	}
	return pool
}

func baseURL(s *Server) string {
	if s.cfg.NoTLS {
		return "http://" + s.Addr().String()
	}
	return "https://" + s.Addr().String()
}
