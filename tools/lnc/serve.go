package main

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"path/filepath"
	"sort"
	"sync"
	"sync/atomic"
	"time"

	"github.com/btcsuite/btcd/btcec/v2"
	"github.com/btcsuite/btclog/v2"
	"github.com/gofrs/flock"
	"github.com/lightninglabs/lightning-node-connect/mailbox"
	"github.com/lightningnetwork/lnd/keychain"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials"
)

type onceListener struct {
	net.Listener
	once sync.Once
	err  error
}

func (l *onceListener) Close() error { l.once.Do(func() { l.err = l.Listener.Close() }); return l.err }

// runner serves one session on one mailbox stream: either the stream of a bound
// client identity (key = its hex public key, Noise KK) or, while the pairing
// phrase is still valid, the phrase's stream (key "" until a client pairs, then
// that client's key, as the upstream server does).
type runner struct {
	sessionID string
	pairing   bool
	server    *grpc.Server
	listener  *onceListener
	once      sync.Once
	mu        sync.Mutex
	key       string
	confirmed atomic.Bool
}

func (r *runner) currentKey() string { r.mu.Lock(); defer r.mu.Unlock(); return r.key }
func (r *runner) setKey(key string)  { r.mu.Lock(); r.key = key; r.mu.Unlock() }
func (r *runner) stop() {
	r.once.Do(func() {
		if r.server != nil {
			r.server.Stop()
		}
		if r.listener != nil {
			_ = r.listener.Close()
		}
	})
}

// desiredKeys is the set of streams a session must be served on: the bound
// identity's, plus the pairing phrase's ("") while the phrase may still pair.
func desiredKeys(s *Session, now time.Time) map[string]bool {
	keys := map[string]bool{}
	if s.Revoked || !now.Before(s.ExpiresAt) {
		return keys
	}
	if s.RemoteKey != "" {
		keys[s.RemoteKey] = true
	}
	if s.Entropy != "" && (s.RemoteKey == "" || s.canRepair()) {
		keys[""] = true
	}
	return keys
}

type startSpec struct {
	session *Session
	key     string
}

// reconcile compares the running runners with the stored sessions: runners for
// streams no longer wanted (revoked, expired, re-paired, confirmed) stop, one
// runner per wanted stream is kept (a pairing runner that turned into the bound
// identity's runner holds its live connection, so it wins a duplicate) and
// missing streams start.
func reconcile(sessions []*Session, runners []*runner, now time.Time) (stop []*runner, start []startSpec) {
	desired := map[string]map[string]bool{}
	for _, s := range sessions {
		desired[s.ID] = desiredKeys(s, now)
	}
	ordered := append([]*runner(nil), runners...)
	sort.SliceStable(ordered, func(i, j int) bool { return ordered[i].pairing && !ordered[j].pairing })
	have := map[string]map[string]bool{}
	for _, r := range ordered {
		key := r.currentKey()
		if !desired[r.sessionID][key] || have[r.sessionID][key] {
			stop = append(stop, r)
			continue
		}
		if have[r.sessionID] == nil {
			have[r.sessionID] = map[string]bool{}
		}
		have[r.sessionID][key] = true
	}
	for _, s := range sessions {
		keys := make([]string, 0, len(desired[s.ID]))
		for key := range desired[s.ID] {
			keys = append(keys, key)
		}
		sort.Strings(keys)
		for _, key := range keys {
			if !have[s.ID][key] {
				start = append(start, startSpec{session: s, key: key})
			}
		}
	}
	return stop, start
}

func relayCredentials(c Config) (credentials.TransportCredentials, error) {
	cfg := &tls.Config{MinVersion: tls.VersionTLS12}
	if c.RelayTLSCert != "" {
		data, e := os.ReadFile(c.RelayTLSCert)
		if e != nil {
			return nil, e
		}
		roots := x509.NewCertPool()
		if !roots.AppendCertsFromPEM(data) {
			return nil, errors.New("invalid relay TLS PEM")
		}
		cfg.RootCAs = roots
	}
	return credentials.NewTLS(cfg), nil
}

// bridge is the serve process's shared state.
type bridge struct {
	ctx        context.Context
	config     Config
	store      *Store
	backend    grpc.ClientConnInterface
	relayCreds credentials.TransportCredentials
	mailboxLog btclog.Logger
	logOut     io.Writer
	failure    chan error
	// mu orders binding changes (the handshake callback) against the serve
	// loop's reconcile, so a runner's key and the stored binding move together.
	mu sync.Mutex
}

func (b *bridge) logf(format string, args ...any) {
	fmt.Fprintf(b.logOut, time.Now().UTC().Format(time.RFC3339)+" "+format+"\n", args...)
}

func newMailboxLogger(w io.Writer) btclog.Logger {
	logger := btclog.NewSLogger(btclog.NewDefaultHandler(w)).SubSystem(mailbox.Subsystem)
	logger.SetLevel(btclog.LevelDebug)
	return logger
}

func serve(ctx context.Context, c Config, store *Store, backend *grpc.ClientConn) error {
	lock := flock.New(filepath.Join(store.Dir, "serve.lock"))
	ok, e := lock.TryLock()
	if e != nil {
		return e
	}
	if !ok {
		return errors.New("another serve process is using this state directory")
	}
	defer lock.Unlock()
	relayCreds, e := relayCredentials(c)
	if e != nil {
		return e
	}
	b := &bridge{ctx: ctx, config: c, store: store, backend: backend, relayCreds: relayCreds, logOut: os.Stderr, failure: make(chan error, 1)}
	if c.LogMailbox {
		// Debug output names stream IDs, statuses and public keys only; the
		// library logs no keys' private parts, auth data or RPC payloads.
		b.mailboxLog = newMailboxLogger(os.Stderr)
		mailbox.UseLogger(b.mailboxLog)
	}
	runners := map[*runner]bool{}
	defer func() {
		for r := range runners {
			r.stop()
		}
	}()
	tick := time.NewTicker(time.Second)
	defer tick.Stop()
	for {
		b.mu.Lock()
		sessions, e := store.list()
		if e != nil {
			b.mu.Unlock()
			return e
		}
		current := make([]*runner, 0, len(runners))
		for r := range runners {
			current = append(current, r)
		}
		stop, start := reconcile(sessions, current, time.Now())
		for _, r := range stop {
			delete(runners, r)
		}
		var startErr error
		for _, spec := range start {
			r, e := b.startRunner(spec)
			if e != nil {
				startErr = e
				break
			}
			runners[r] = true
		}
		b.mu.Unlock()
		// Stop outside the lock: grpc's Stop waits for connections whose
		// handshake callback may be waiting for it.
		for _, r := range stop {
			r.stop()
		}
		if startErr != nil {
			return startErr
		}
		select {
		case <-ctx.Done():
			return nil
		case err := <-b.failure:
			return err
		case <-tick.C:
		}
	}
}

// startRunner starts serving spec's stream. Called with b.mu held.
func (b *bridge) startRunner(spec startSpec) (*runner, error) {
	s := spec.session
	keyBytes, e := hex.DecodeString(s.PrivateKey)
	if e != nil || len(keyBytes) != 32 {
		return nil, errors.New("invalid stored transport key")
	}
	key, _ := btcec.PrivKeyFromBytes(keyBytes)
	var remote *btcec.PublicKey
	var entropy []byte
	if spec.key != "" {
		if remote, e = btcec.ParsePubKey(mustDecode(spec.key)); e != nil {
			return nil, e
		}
	} else {
		if entropy, e = hex.DecodeString(s.Entropy); e != nil || len(entropy) != mailbox.NumPassphraseEntropyBytes {
			return nil, errors.New("invalid stored pairing entropy")
		}
	}
	id := s.ID
	r := &runner{sessionID: id, pairing: spec.key == "", key: spec.key}
	onRemote := func(pub *btcec.PublicKey) error {
		b.mu.Lock()
		defer b.mu.Unlock()
		if err := b.store.bindRemote(id, pub); err != nil {
			return err
		}
		r.setKey(hex.EncodeToString(pub.SerializeCompressed()))
		return nil
	}
	connData := mailbox.NewConnData(&keychain.PrivKeyECDH{PrivKey: key}, remote, entropy, sessionAuthData(s.Macaroon), onRemote, nil)
	listener, e := newMailboxListener(b.ctx, b.config.Relay, connData, nil, b.mailboxLog, grpc.WithTransportCredentials(b.relayCreds.Clone()))
	if e != nil {
		return nil, e
	}
	var creds credentials.TransportCredentials = serverNoiseCredentials(connData)
	proxy := ProxyConfig{Local: localMethodsFor(s.AllowedMethods), OnAuthenticated: func() { b.confirm(r) }}
	if b.config.LogRPC {
		creds = &handshakeLogCredentials{TransportCredentials: creds, log: func(err error) {
			if err != nil {
				b.logf("handshake session=%s failed: %v", id, err)
			} else {
				b.logf("handshake session=%s ok", id)
			}
		}}
		proxy.LogRPC = func(method string, code codes.Code, elapsed time.Duration) {
			b.logf("rpc session=%s method=%s code=%s duration=%s", id, method, code, elapsed.Round(time.Millisecond))
		}
	}
	server, e := NewSessionProxyWith(b.backend, s.Macaroon, proxy, grpc.Creds(creds))
	if e != nil {
		_ = listener.Close()
		return nil, e
	}
	r.server, r.listener = server, &onceListener{Listener: listener}
	go func() {
		if e := r.server.Serve(r.listener); e != nil && b.ctx.Err() == nil {
			select {
			case b.failure <- fmt.Errorf("session %s transport stopped (restart serve): %w", id, e):
			default:
			}
		}
	}()
	mode := "paired client"
	if r.pairing {
		mode = "pairing phrase"
	}
	fmt.Fprintf(os.Stderr, "session %s listening via %s (%s)\n", id, b.config.Relay, mode)
	return r, nil
}

// confirm records, once per runner, that an authenticated RPC arrived over the
// identity it serves, which ends re-pairing with the phrase.
func (b *bridge) confirm(r *runner) {
	if r.confirmed.Load() {
		return
	}
	key := r.currentKey()
	if key == "" || !r.confirmed.CompareAndSwap(false, true) {
		return
	}
	b.mu.Lock()
	err := b.store.confirm(r.sessionID, key)
	b.mu.Unlock()
	if err != nil && !errors.Is(err, errStaleConfirmation) {
		r.confirmed.Store(false)
		b.logf("session %s: recording the confirmed pairing failed: %v", r.sessionID, err)
	}
}

// handshakeLogCredentials reports each Noise server handshake's outcome. Error
// texts name the failed step only; they carry no key material or auth data.
type handshakeLogCredentials struct {
	credentials.TransportCredentials
	log func(error)
}

func (h *handshakeLogCredentials) ServerHandshake(conn net.Conn) (net.Conn, credentials.AuthInfo, error) {
	c, info, err := h.TransportCredentials.ServerHandshake(conn)
	h.log(err)
	return c, info, err
}

func (h *handshakeLogCredentials) Clone() credentials.TransportCredentials {
	return &handshakeLogCredentials{TransportCredentials: h.TransportCredentials.Clone(), log: h.log}
}

func mustDecode(s string) []byte { b, _ := hex.DecodeString(s); return b }
