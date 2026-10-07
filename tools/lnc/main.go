package main

import (
	"context"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/lightningnetwork/lnd/lnrpc"
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
			rows = append(rows, map[string]any{"id": s.ID, "name": s.Name, "expires_at": s.ExpiresAt, "revoked": s.Revoked, "root_key_deleted": s.RootKeyDeleted, "paired": s.RemoteKey != "", "confirmed": s.RemoteKey != "" && !s.canRepair(), "permissions": s.AllowedMethods})
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
	// The litrpc URIs are not node methods, so the backend accepts them only as
	// external permissions; it never serves them (the bridge does, lit.go).
	response, e := client.BakeMacaroon(adminCtx, &lnrpc.BakeMacaroonRequest{RootKeyId: session.RootKeyID, Permissions: permissions, AllowExternalPermissions: true})
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
