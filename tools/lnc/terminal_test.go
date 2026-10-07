package main

import (
	"bytes"
	"context"
	"encoding/hex"
	"errors"
	"fmt"
	"net"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/btcsuite/btcd/btcec/v2"
	"github.com/lightninglabs/lightning-node-connect/mailbox"
	"github.com/lightningnetwork/lnd/keychain"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"
	"google.golang.org/protobuf/encoding/protowire"
)

// wasmClientOnAuthData is the auth data check of the stock LNC WASM client that
// Lightning Terminal runs (lightning-node-connect cmd/wasm-client/main.go): it
// fails the transport with "authdata does not contain a macaroon" unless the
// auth data is exactly "Macaroon: <hex binary macaroon>".
func wasmClientOnAuthData(want []byte) func([]byte) error {
	return func(data []byte) error {
		parts := strings.Split(string(data), ": ")
		if len(parts) != 2 || parts[0] != "Macaroon" {
			return errors.New("authdata does not contain a macaroon")
		}
		got, err := hex.DecodeString(parts[1])
		if err != nil {
			return err
		}
		if !bytes.Equal(got, want) {
			return errors.New("wrong macaroon")
		}
		return nil
	}
}

// singleConnListener hands out one connection, then blocks until closed.
type singleConnListener struct {
	conns  chan net.Conn
	closed chan struct{}
	once   sync.Once
}

func newSingleConnListener(c net.Conn) *singleConnListener {
	l := &singleConnListener{conns: make(chan net.Conn, 1), closed: make(chan struct{})}
	l.conns <- c
	return l
}
func (l *singleConnListener) Accept() (net.Conn, error) {
	select {
	case c := <-l.conns:
		return c, nil
	case <-l.closed:
		return nil, net.ErrClosed
	}
}
func (l *singleConnListener) Close() error   { l.once.Do(func() { close(l.closed) }); return nil }
func (l *singleConnListener) Addr() net.Addr { return &net.UnixAddr{Name: "lnc", Net: "pipe"} }

// The whole path Terminal uses after pairing, minus the relay: the stock client
// accepts the session auth data, sends it back as per-RPC metadata, the bridge
// forwards node calls with the session credential and answers litrpc.Status.
func TestStockClientPairsAndCallsThroughBridge(t *testing.T) {
	credential := []byte{1, 2, 3}
	serverKey, _ := btcec.NewPrivateKey()
	clientKey, _ := btcec.NewPrivateKey()
	_, entropy, err := mailbox.NewPassphraseEntropy()
	if err != nil {
		t.Fatal(err)
	}
	serverData := mailbox.NewConnData(&keychain.PrivKeyECDH{PrivKey: serverKey}, nil, entropy[:],
		sessionAuthData(hex.EncodeToString(credential)), func(*btcec.PublicKey) error { return nil }, nil)
	clientData := mailbox.NewConnData(&keychain.PrivKeyECDH{PrivKey: clientKey}, nil, entropy[:], nil,
		func(*btcec.PublicKey) error { return nil }, wasmClientOnAuthData(credential))

	backendSeen := make(chan string, 1)
	backend := localConn(t, grpc.NewServer(grpc.ForceServerCodec(opaqueCodec{}), grpc.UnknownServiceHandler(
		func(_ interface{}, stream grpc.ServerStream) error {
			md, _ := metadata.FromIncomingContext(stream.Context())
			backendSeen <- strings.Join(md.Get("macaroon"), ",")
			var frame []byte
			if err := stream.RecvMsg(&frame); err != nil {
				return err
			}
			return stream.SendMsg(&frame)
		})))
	var logMu sync.Mutex
	var logged []string
	authenticated := 0
	bridge, err := NewSessionProxyWith(backend, hex.EncodeToString(credential), ProxyConfig{
		Local:           localMethodsFor([]string{litSubServerStatus}),
		OnAuthenticated: func() { logMu.Lock(); authenticated++; logMu.Unlock() },
		LogRPC: func(method string, code codes.Code, _ time.Duration) {
			logMu.Lock()
			logged = append(logged, method+" "+code.String())
			logMu.Unlock()
		},
	}, grpc.Creds(serverNoiseCredentials(serverData)))
	if err != nil {
		t.Fatal(err)
	}
	left, right := net.Pipe()
	listener := newSingleConnListener(pipeProxyConn{left})
	go func() { _ = bridge.Serve(listener) }()
	t.Cleanup(func() { bridge.Stop(); _ = listener.Close(); _ = right.Close() })

	clientNoise := mailbox.NewNoiseGrpcConn(clientData)
	used := false
	conn, err := grpc.DialContext(context.Background(), "passthrough:///lnc",
		grpc.WithContextDialer(func(context.Context, string) (net.Conn, error) {
			if used {
				return nil, errors.New("one connection only")
			}
			used = true
			return pipeProxyConn{right}, nil
		}),
		grpc.WithTransportCredentials(clientNoise), grpc.WithPerRPCCredentials(clientNoise))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = conn.Close() })
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()

	request, reply := []byte{0x0a, 0x01, 0x41}, []byte(nil)
	if err = conn.Invoke(ctx, "/lnrpc.Lightning/GetInfo", &request, &reply, grpc.ForceCodec(opaqueCodec{})); err != nil {
		t.Fatal("node call through the bridge", err)
	}
	if !bytes.Equal(reply, request) {
		t.Fatal("backend reply not forwarded")
	}
	if got := <-backendSeen; got != hex.EncodeToString(credential) {
		t.Fatal("backend did not receive exactly the session credential", got)
	}
	var statusReply []byte
	empty := []byte{}
	if err = conn.Invoke(ctx, litSubServerStatus, &empty, &statusReply, grpc.ForceCodec(opaqueCodec{})); err != nil {
		t.Fatal("litrpc status", err)
	}
	statuses := decodeSubServerStatus(t, statusReply)
	if !statuses["lnd"].running || statuses["loop"].running || !statuses["loop"].disabled || statuses["taproot-assets"].running {
		t.Fatalf("unexpected sub-server status %+v", statuses)
	}
	logMu.Lock()
	defer logMu.Unlock()
	if authenticated != 2 || len(logged) != 2 || logged[0] != "/lnrpc.Lightning/GetInfo OK" || logged[1] != litSubServerStatus+" OK" {
		t.Fatalf("authenticated=%d log=%v", authenticated, logged)
	}
}

// The bridge before this fix sent "macaroon: <hex>"; the stock client refuses
// that, after the bridge has already completed (and bound) its side.
func TestStockClientRejectsLowercaseAuthDataHeader(t *testing.T) {
	if err := wasmClientOnAuthData([]byte{1})([]byte("macaroon: 01")); err == nil {
		t.Fatal("lowercase header accepted")
	}
	if err := wasmClientOnAuthData([]byte{1})(sessionAuthData("01")); err != nil {
		t.Fatal(err)
	}
}

type subServer struct{ disabled, running bool }

func decodeSubServerStatus(t *testing.T, b []byte) map[string]subServer {
	t.Helper()
	out := map[string]subServer{}
	for len(b) > 0 {
		num, typ, n := protowire.ConsumeTag(b)
		if n < 0 || num != 1 || typ != protowire.BytesType {
			t.Fatal("bad map field")
		}
		b = b[n:]
		entry, n := protowire.ConsumeBytes(b)
		if n < 0 {
			t.Fatal("bad entry")
		}
		b = b[n:]
		var name string
		var value subServer
		for len(entry) > 0 {
			num, typ, n := protowire.ConsumeTag(entry)
			entry = entry[n:]
			field, m := protowire.ConsumeBytes(entry)
			if typ != protowire.BytesType || m < 0 {
				t.Fatal("bad entry field")
			}
			entry = entry[m:]
			if num == 1 {
				name = string(field)
				continue
			}
			for len(field) > 0 {
				fnum, _, k := protowire.ConsumeTag(field)
				field = field[k:]
				v, k := protowire.ConsumeVarint(field)
				field = field[k:]
				switch fnum {
				case 1:
					value.disabled = v == 1
				case 2:
					value.running = v == 1
				}
			}
		}
		out[name] = value
	}
	return out
}

func TestSubServerStatusNamesLitdSubServers(t *testing.T) {
	statuses := decodeSubServerStatus(t, subServerStatusResponse())
	want := map[string]subServer{
		"lit": {running: true}, "lnd": {running: true}, "loop": {disabled: true}, "pool": {disabled: true},
		"faraday": {disabled: true}, "taproot-assets": {disabled: true},
	}
	if fmt.Sprint(statuses) != fmt.Sprint(want) {
		t.Fatalf("got %v", statuses)
	}
}

func TestProxyAnswersLocalMethodsOnlyWhenGrantedAndLogsCodes(t *testing.T) {
	backendCalls := make(chan string, 4)
	backend := localConn(t, grpc.NewServer(grpc.ForceServerCodec(opaqueCodec{}), grpc.UnknownServiceHandler(
		func(_ interface{}, stream grpc.ServerStream) error {
			method, _ := grpc.MethodFromServerStream(stream)
			backendCalls <- method
			return status.Error(codes.Unimplemented, "unknown service")
		})))
	var mu sync.Mutex
	var logged []string
	bridge, err := NewSessionProxyWith(backend, "010203", ProxyConfig{
		Local: localMethodsFor([]string{litListAutopilotSessions}),
		LogRPC: func(method string, code codes.Code, _ time.Duration) {
			mu.Lock()
			logged = append(logged, method+" "+code.String())
			mu.Unlock()
		},
	})
	if err != nil {
		t.Fatal(err)
	}
	conn := localConn(t, bridge)
	ctx := metadata.AppendToOutgoingContext(context.Background(), "macaroon", "010203")
	empty, reply := []byte{}, []byte(nil)
	if err = conn.Invoke(ctx, litListAutopilotSessions, &empty, &reply, grpc.ForceCodec(opaqueCodec{})); err != nil || len(reply) != 0 {
		t.Fatal("granted local method", err)
	}
	if err = conn.Invoke(ctx, litSubServerStatus, &empty, &reply, grpc.ForceCodec(opaqueCodec{})); status.Code(err) != codes.Unimplemented {
		t.Fatal("ungranted local method was not forwarded", err)
	}
	if got := <-backendCalls; got != litSubServerStatus {
		t.Fatal(got)
	}
	bad := metadata.AppendToOutgoingContext(context.Background(), "macaroon", "0102")
	if err = conn.Invoke(bad, litListAutopilotSessions, &empty, &reply, grpc.ForceCodec(opaqueCodec{})); status.Code(err) != codes.Unauthenticated {
		t.Fatal("local method answered without the session credential", err)
	}
	mu.Lock()
	defer mu.Unlock()
	want := []string{litListAutopilotSessions + " OK", litSubServerStatus + " Unimplemented", litListAutopilotSessions + " Unauthenticated"}
	if fmt.Sprint(logged) != fmt.Sprint(want) {
		t.Fatalf("log %v", logged)
	}
	for _, line := range logged {
		if strings.Contains(line, "010203") {
			t.Fatal("credential logged")
		}
	}
}

func TestEveryProfileGrantsTerminalLitrpcReads(t *testing.T) {
	for _, profile := range []string{"readonly", "wallet"} {
		methods, _ := profileMethods(profile)
		if len(localMethodsFor(methods)) != len(litLocalMethods) {
			t.Fatal(profile, "lacks the litrpc reads Terminal requires")
		}
	}
}

func TestReconcileServesPhraseUntilConfirmed(t *testing.T) {
	now := time.Now()
	key := strings.Repeat("02", 33)
	fresh := &Session{ID: "a", Entropy: "00", ExpiresAt: now.Add(time.Hour)}
	if stop, start := reconcile([]*Session{fresh}, nil, now); len(stop) != 0 || len(start) != 1 || start[0].key != "" {
		t.Fatal("unpaired session not served on its phrase", start)
	}
	// The phrase runner paired a client: it now serves that identity and the
	// phrase gets a new runner while the binding is unconfirmed.
	pairing := &runner{sessionID: "a", pairing: true, key: key}
	bound := &Session{ID: "a", Entropy: "00", RemoteKey: key, ExpiresAt: now.Add(time.Hour)}
	stop, start := reconcile([]*Session{bound}, []*runner{pairing}, now)
	if len(stop) != 0 || len(start) != 1 || start[0].key != "" {
		t.Fatal("unconfirmed binding must keep the phrase served", stop, start)
	}
	phrase := &runner{sessionID: "a", pairing: true}
	// A duplicate runner for the bound identity loses to the pairing runner.
	stale := &runner{sessionID: "a", key: key}
	stop, start = reconcile([]*Session{bound}, []*runner{stale, phrase, pairing}, now)
	if len(start) != 0 || len(stop) != 1 || stop[0] != stale {
		t.Fatal("duplicate not resolved", stop, start)
	}
	// Confirmed: only the identity's stream remains.
	confirmed := &Session{ID: "a", RemoteKey: key, Confirmed: true, ExpiresAt: now.Add(time.Hour)}
	stop, start = reconcile([]*Session{confirmed}, []*runner{phrase, pairing}, now)
	if len(start) != 0 || len(stop) != 1 || stop[0] != phrase {
		t.Fatal("phrase still served after confirmation", stop, start)
	}
	// Re-paired to another identity: the old identity's runner stops.
	other := strings.Repeat("03", 33)
	repaired := &Session{ID: "a", Entropy: "00", RemoteKey: other, ExpiresAt: now.Add(time.Hour)}
	old := &runner{sessionID: "a", key: key}
	stop, start = reconcile([]*Session{repaired}, []*runner{old, phrase}, now)
	if len(stop) != 1 || stop[0] != old || len(start) != 1 || start[0].key != other {
		t.Fatal("re-pairing not followed", stop, start)
	}
	// Revoked, expired and deleted sessions stop everything.
	revoked := &Session{ID: "a", RemoteKey: key, Revoked: true, ExpiresAt: now.Add(time.Hour)}
	if stop, start = reconcile([]*Session{revoked}, []*runner{pairing}, now); len(stop) != 1 || len(start) != 0 {
		t.Fatal("revoked session still served")
	}
	expired := &Session{ID: "a", RemoteKey: key, ExpiresAt: now.Add(-time.Second)}
	if stop, _ = reconcile([]*Session{expired}, []*runner{pairing}, now); len(stop) != 1 {
		t.Fatal("expired session still served")
	}
	if stop, _ = reconcile(nil, []*runner{pairing}, now); len(stop) != 1 {
		t.Fatal("deleted session still served")
	}
}

func TestServeLoggingFlagsAndMailboxDebugLogger(t *testing.T) {
	c, err := parseConfig("serve", []string{"--tls-cert", "x", "--log-rpc", "--log-mailbox"})
	if err != nil || !c.LogRPC || !c.LogMailbox {
		t.Fatal("logging flags not parsed", err)
	}
	if c, _ = parseConfig("serve", []string{"--tls-cert", "x"}); c.LogRPC || c.LogMailbox {
		t.Fatal("logging must be opt-in")
	}
	var out bytes.Buffer
	newMailboxLogger(&out).Debugf("Receive mailbox created")
	if !strings.Contains(out.String(), "MBOX") || !strings.Contains(out.String(), "Receive mailbox created") {
		t.Fatal("mailbox debug logger silent", out.String())
	}
}

func TestHandshakeLogCredentialsReportsOutcome(t *testing.T) {
	var outcomes []error
	creds := &handshakeLogCredentials{TransportCredentials: failingCreds{}, log: func(err error) { outcomes = append(outcomes, err) }}
	if _, _, err := creds.ServerHandshake(nil); err == nil || len(outcomes) != 1 || outcomes[0] == nil {
		t.Fatal("failed handshake not reported")
	}
	if _, ok := creds.Clone().(*handshakeLogCredentials); !ok {
		t.Fatal("clone drops logging")
	}
}

type failingCreds struct {
	credentials.TransportCredentials
}

func (failingCreds) ServerHandshake(net.Conn) (net.Conn, credentials.AuthInfo, error) {
	return nil, nil, errors.New("authentication handshake failed")
}
func (failingCreds) Clone() credentials.TransportCredentials { return failingCreds{} }
