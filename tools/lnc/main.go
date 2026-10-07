package main

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"os"
	"os/signal"
	"path/filepath"
	"sync"
	"syscall"
	"time"

	"github.com/btcsuite/btcd/btcec/v2"
	"github.com/gofrs/flock"
	"github.com/lightninglabs/lightning-node-connect/mailbox"
	"github.com/lightningnetwork/lnd/keychain"
	"github.com/lightningnetwork/lnd/lnrpc"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials"
	"google.golang.org/grpc/metadata"
	"gopkg.in/macaroon.v2"
)

func main() {
	if len(os.Args) < 2 {
		fmt.Fprintln(os.Stderr, "usage: nltg-lnc create|list|revoke|serve [flags]")
		os.Exit(1)
	}
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	if e := run(ctx, os.Args[1], os.Args[2:]); e != nil {
		fmt.Fprintln(os.Stderr, e)
		os.Exit(1)
	}
}
func run(ctx context.Context, command string, args []string) error {
	if command != "create" && command != "list" && command != "revoke" && command != "serve" {
		return errors.New("unknown command; use create, list, revoke, or serve")
	}
	c, e := parseConfig(command, args)
	if e != nil {
		return e
	}
	store, e := openStore(c.StateDir)
	if e != nil {
		return e
	}
	if command == "list" {
		sessions, e := store.list()
		if e != nil {
			return e
		}
		rows := []map[string]any{}
		for _, s := range sessions {
			rows = append(rows, map[string]any{"id": s.ID, "name": s.Name, "expires_at": s.ExpiresAt, "revoked": s.Revoked, "root_key_deleted": s.RootKeyDeleted, "paired": s.RemoteKey != "", "permissions": s.AllowedMethods})
		}
		return json.NewEncoder(os.Stdout).Encode(rows)
	}
	if command == "revoke" {
		if e = store.update(c.ID, func(s *Session) error { s.Revoked = true; return nil }); e != nil {
			return e
		}
	}
	dialCtx, dialCancel := context.WithTimeout(ctx, 20*time.Second)
	defer dialCancel()
	backend, e := DialBackend(dialCtx, c.Backend, c.TLSCert, c.TLSServerName)
	if e != nil {
		if command == "revoke" {
			return fmt.Errorf("session locally disabled; backend root deletion unavailable (retry revoke): %w", e)
		}
		return e
	}
	defer backend.Close()
	if command == "serve" {
		return serve(ctx, c, store, backend)
	}
	admin, e := os.ReadFile(c.AdminMacaroon)
	if e != nil {
		return e
	}
	var m macaroon.Macaroon
	if e = m.UnmarshalBinary(admin); e != nil {
		return errors.New("admin macaroon must be binary LND macaroon file")
	}
	adminCtx := metadata.AppendToOutgoingContext(ctx, "macaroon", hex.EncodeToString(admin))
	adminCtx, cancel := context.WithTimeout(adminCtx, 20*time.Second)
	defer cancel()
	client := lnrpc.NewLightningClient(backend)
	if command == "revoke" {
		session, e := store.load(c.ID)
		if e != nil {
			return e
		}
		if _, e = client.DeleteMacaroonID(adminCtx, &lnrpc.DeleteMacaroonIDRequest{RootKeyId: session.RootKeyID}); e != nil {
			return fmt.Errorf("session locally disabled; backend root deletion failed (retry revoke): %w", e)
		}
		return store.update(c.ID, func(s *Session) error {
			s.RootKeyDeleted = true
			s.Macaroon = ""
			s.PrivateKey = ""
			s.Entropy = ""
			return nil
		})
	}
	methods, _ := profileMethods(c.Profile)
	session, phrase, e := newSession(c.Name, time.Now().Add(c.TTL), methods)
	if e != nil {
		return e
	}
	permissions := make([]*lnrpc.MacaroonPermission, len(methods))
	for i, method := range methods {
		permissions[i] = &lnrpc.MacaroonPermission{Entity: "uri", Action: method}
	}
	response, e := client.BakeMacaroon(adminCtx, &lnrpc.BakeMacaroonRequest{RootKeyId: session.RootKeyID, Permissions: permissions})
	if e != nil {
		return e
	}
	persisted := false
	defer func() {
		if !persisted {
			_, _ = client.DeleteMacaroonID(adminCtx, &lnrpc.DeleteMacaroonIDRequest{RootKeyId: session.RootKeyID})
		}
	}()
	raw, e := hex.DecodeString(response.Macaroon)
	if e != nil {
		return e
	}
	if e = m.UnmarshalBinary(raw); e != nil {
		return e
	}
	if e = m.AddFirstPartyCaveat([]byte("time-before " + session.ExpiresAt.Format(time.RFC3339))); e != nil {
		return e
	}
	raw, e = m.MarshalBinary()
	if e != nil {
		return e
	}
	session.Macaroon = hex.EncodeToString(raw)
	if e = store.save(session); e != nil {
		return e
	}
	persisted = true
	return json.NewEncoder(os.Stdout).Encode(map[string]any{"id": session.ID, "pairing_phrase": phrase, "relay": c.Relay, "expires_at": session.ExpiresAt, "permissions": methods})
}

type onceListener struct {
	net.Listener
	once sync.Once
	err  error
}

func (l *onceListener) Close() error { l.once.Do(func() { l.err = l.Listener.Close() }); return l.err }

type runningSession struct {
	server   *grpc.Server
	listener *onceListener
	once     sync.Once
}

func (r *runningSession) stop() { r.once.Do(func() { r.server.Stop(); _ = r.listener.Close() }) }
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
	running := map[string]*runningSession{}
	failure := make(chan error, 1)
	defer func() {
		for _, r := range running {
			r.stop()
		}
	}()
	tick := time.NewTicker(time.Second)
	defer tick.Stop()
	for {
		sessions, e := store.list()
		if e != nil {
			return e
		}
		active := map[string]bool{}
		for _, s := range sessions {
			if s.Revoked || !time.Now().Before(s.ExpiresAt) {
				continue
			}
			active[s.ID] = true
			if running[s.ID] != nil {
				continue
			}
			keyBytes, e := hex.DecodeString(s.PrivateKey)
			if e != nil || len(keyBytes) != 32 {
				return errors.New("invalid stored transport key")
			}
			key, _ := btcec.PrivKeyFromBytes(keyBytes)
			entropy, e := hex.DecodeString(s.Entropy)
			if e != nil {
				return e
			}
			var remote *btcec.PublicKey
			if s.RemoteKey != "" {
				remote, e = btcec.ParsePubKey(mustDecode(s.RemoteKey))
				if e != nil {
					return e
				}
			} else if len(entropy) != mailbox.NumPassphraseEntropyBytes {
				return errors.New("invalid stored pairing entropy")
			}
			id := s.ID
			connData := mailbox.NewConnData(&keychain.PrivKeyECDH{PrivKey: key}, remote, entropy, []byte("macaroon: "+s.Macaroon), func(pub *btcec.PublicKey) error { return store.bindRemote(id, pub) }, nil)
			listener, e := newMailboxListener(ctx, c.Relay, connData, nil, grpc.WithTransportCredentials(relayCreds.Clone()))
			if e != nil {
				return e
			}
			noise := serverNoiseCredentials(connData)
			server, e := NewSessionProxy(backend, s.Macaroon, grpc.Creds(noise))
			if e != nil {
				_ = listener.Close()
				return e
			}
			r := &runningSession{server: server, listener: &onceListener{Listener: listener}}
			running[id] = r
			go func(id string, r *runningSession) {
				if e := r.server.Serve(r.listener); e != nil && ctx.Err() == nil {
					select {
					case failure <- fmt.Errorf("session %s transport stopped (restart serve): %w", id, e):
					default:
					}
				}
			}(id, r)
			fmt.Fprintf(os.Stderr, "session %s listening via %s\n", id, c.Relay)
		}
		for id, r := range running {
			if !active[id] {
				r.stop()
				delete(running, id)
			}
		}
		select {
		case <-ctx.Done():
			return nil
		case err := <-failure:
			return err
		case <-tick.C:
		}
	}
}
func mustDecode(s string) []byte { b, _ := hex.DecodeString(s); return b }
