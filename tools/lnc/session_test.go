package main

import (
	"bytes"
	"context"
	"encoding/hex"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/btcsuite/btcd/btcec/v2"
)

func TestPersistentSessionPinsOneClientAndErasesPhrase(t *testing.T) {
	store, e := openStore(filepath.Join(t.TempDir(), "state"))
	if e != nil {
		t.Fatal(e)
	}
	s, phrase, e := newSession("wallet", time.Now().Add(time.Hour), []string{"/lnrpc.Lightning/GetInfo"})
	if e != nil {
		t.Fatal(e)
	}
	if phrase == "" || s.Entropy == "" || s.RootKeyID == 0 {
		t.Fatal("missing fresh pairing state")
	}
	if e = store.save(s); e != nil {
		t.Fatal(e)
	}
	first, _ := btcec.NewPrivateKey()
	second, _ := btcec.NewPrivateKey()
	firstHex := hex.EncodeToString(first.PubKey().SerializeCompressed())
	if e = store.bindRemote(s.ID, first.PubKey()); e != nil {
		t.Fatal(e)
	}
	reopened, _ := openStore(store.Dir)
	saved, e := reopened.load(s.ID)
	if e != nil {
		t.Fatal(e)
	}
	if saved.RemoteKey != firstHex || saved.Confirmed || saved.Entropy == "" {
		t.Fatal("binding not durable or phrase erased before an authenticated RPC")
	}
	if saved.PrivateKey != s.PrivateKey {
		t.Fatal("local identity changed")
	}
	if e = reopened.confirm(s.ID, hex.EncodeToString(second.PubKey().SerializeCompressed())); !errors.Is(e, errStaleConfirmation) {
		t.Fatal("confirmed an identity that is not bound", e)
	}
	if e = reopened.confirm(s.ID, firstHex); e != nil {
		t.Fatal(e)
	}
	saved, _ = reopened.load(s.ID)
	if !saved.Confirmed || saved.Entropy != "" || saved.RemoteKey != firstHex {
		t.Fatal("confirmation did not pin the client and erase the phrase")
	}
	if e = reopened.bindRemote(s.ID, first.PubKey()); e != nil {
		t.Fatal(e)
	}
	if e = reopened.bindRemote(s.ID, second.PubKey()); e == nil {
		t.Fatal("accepted second client")
	}
	info, _ := os.Stat(filepath.Join(store.Dir, s.ID+".json"))
	if info.Mode().Perm() != 0600 {
		t.Fatal(info.Mode())
	}
}

// A client whose handshake completed on the bridge but failed on its own side
// (it rejected the auth data, closed the tab, timed out) never keeps its keys
// and comes back with the phrase: until an authenticated RPC confirms the
// binding, the phrase pairs again and replaces the unproven identity.
func TestUnconfirmedPairingCanBeRepairedWithThePhrase(t *testing.T) {
	store, _ := openStore(filepath.Join(t.TempDir(), "state"))
	s, _, _ := newSession("", time.Now().Add(time.Hour), nil)
	_ = store.save(s)
	first, _ := btcec.NewPrivateKey()
	second, _ := btcec.NewPrivateKey()
	secondHex := hex.EncodeToString(second.PubKey().SerializeCompressed())
	if e := store.bindRemote(s.ID, first.PubKey()); e != nil {
		t.Fatal(e)
	}
	if e := store.bindRemote(s.ID, second.PubKey()); e != nil {
		t.Fatal("unconfirmed pairing not replaceable", e)
	}
	saved, _ := store.load(s.ID)
	if saved.RemoteKey != secondHex || saved.Confirmed {
		t.Fatal("re-pairing did not bind the new identity")
	}
	if e := store.confirm(s.ID, secondHex); e != nil {
		t.Fatal(e)
	}
	if e := store.bindRemote(s.ID, first.PubKey()); e == nil {
		t.Fatal("confirmed pairing replaced")
	}
	// A session paired before confirmation existed has no phrase left.
	legacy, _, _ := newSession("", time.Now().Add(time.Hour), nil)
	legacy.RemoteKey, legacy.Entropy = hex.EncodeToString(first.PubKey().SerializeCompressed()), ""
	_ = store.save(legacy)
	if e := store.bindRemote(legacy.ID, second.PubKey()); e == nil {
		t.Fatal("legacy pairing replaced")
	}
}

func TestConcurrentPairingChoosesOneIdentity(t *testing.T) {
	store, _ := openStore(filepath.Join(t.TempDir(), "state"))
	s, _, _ := newSession("", time.Now().Add(time.Hour), nil)
	_ = store.save(s)
	one, _ := btcec.NewPrivateKey()
	two, _ := btcec.NewPrivateKey()
	errs := make(chan error, 2)
	var wg sync.WaitGroup
	for _, key := range []*btcec.PrivateKey{one, two} {
		wg.Add(1)
		go func(key *btcec.PrivateKey) { defer wg.Done(); errs <- store.bindRemote(s.ID, key.PubKey()) }(key)
	}
	wg.Wait()
	close(errs)
	for e := range errs {
		if e != nil {
			t.Fatal(e)
		}
	}
	// Bindings are serialized: exactly one identity is stored, and only it can
	// be confirmed, after which the other is refused.
	saved, _ := store.load(s.ID)
	keys := map[string]*btcec.PrivateKey{
		hex.EncodeToString(one.PubKey().SerializeCompressed()): two,
		hex.EncodeToString(two.PubKey().SerializeCompressed()): one,
	}
	loser, ok := keys[saved.RemoteKey]
	if !ok {
		t.Fatal("stored identity is neither client")
	}
	if e := store.confirm(s.ID, saved.RemoteKey); e != nil {
		t.Fatal(e)
	}
	if e := store.bindRemote(s.ID, loser.PubKey()); e == nil {
		t.Fatal("second identity bound after confirmation")
	}
}
func TestRevokedAndExpiredSessionsCannotPair(t *testing.T) {
	store, _ := openStore(filepath.Join(t.TempDir(), "state"))
	key, _ := btcec.NewPrivateKey()
	for _, expiry := range []time.Time{time.Now().Add(-time.Hour), time.Now().Add(time.Hour)} {
		s, _, _ := newSession("", expiry, nil)
		s.Revoked = expiry.After(time.Now())
		_ = store.save(s)
		if e := store.bindRemote(s.ID, key.PubKey()); e == nil {
			t.Fatal("inactive session paired")
		}
	}
}
func TestStateRejectsTraversalAndUnsafeFiles(t *testing.T) {
	store, _ := openStore(filepath.Join(t.TempDir(), "state"))
	if _, e := store.load("../../admin"); e == nil {
		t.Fatal("accepted traversal")
	}
	s, _, _ := newSession("", time.Now().Add(time.Hour), nil)
	_ = store.save(s)
	path := filepath.Join(store.Dir, s.ID+".json")
	_ = os.Chmod(path, 0644)
	if _, e := store.load(s.ID); e == nil {
		t.Fatal("accepted public secret file")
	}
	_ = os.Chmod(store.Dir, 0755)
	if _, e := openStore(store.Dir); e == nil {
		t.Fatal("accepted public secret directory")
	}
}
func TestProfilesNeverPermitMacaroonAdministration(t *testing.T) {
	for _, profile := range []string{"readonly", "wallet", "admin"} {
		methods, e := profileMethods(profile)
		if e != nil {
			t.Fatal(e)
		}
		for _, method := range methods {
			// Macaroon administration, signrpc, raw wallet signing and spends,
			// and the node's hooks stay out of every profile, admin included.
			for _, banned := range []string{"Macaroon", "/signrpc.", "Psbt", "LeaseOutput", "ReleaseOutput",
				"SignOutputRaw", "SendOutputs", "PublishTransaction", "DeriveKey", "ChannelAcceptor", "HtlcInterceptor",
				"AbandonChannel", "StopDaemon", "RestoreChannelBackups"} {
				if bytes.Contains([]byte(method), []byte(banned)) {
					t.Fatal(profile, "grants", method)
				}
			}
		}
	}
	if _, e := profileMethods("root"); e == nil {
		t.Fatal("unknown profile accepted")
	}
	if _, e := parseConfig("create", []string{"--tls-cert", "x", "--admin-macaroon", "a", "--ttl", "-1s"}); e == nil {
		t.Fatal("negative expiry accepted")
	}
}

func TestRevokeDisablesLocalSessionBeforeBackendDial(t *testing.T) {
	dir := filepath.Join(t.TempDir(), "state")
	store, e := openStore(dir)
	if e != nil {
		t.Fatal(e)
	}
	session, _, e := newSession("wallet", time.Now().Add(time.Hour), nil)
	if e != nil {
		t.Fatal(e)
	}
	session.Macaroon = "cafe"
	if e = store.save(session); e != nil {
		t.Fatal(e)
	}
	err := run(context.Background(), "revoke", []string{"--state-dir", dir, "--id", session.ID, "--tls-cert", filepath.Join(dir, "missing-cert.pem"), "--admin-macaroon", filepath.Join(dir, "missing-admin.macaroon")})
	if err == nil || !strings.Contains(err.Error(), "locally disabled") {
		t.Fatalf("expected actionable backend deletion failure, got %v", err)
	}
	saved, e := store.load(session.ID)
	if e != nil {
		t.Fatal(e)
	}
	if !saved.Revoked || saved.RootKeyDeleted {
		t.Fatal("offline revocation did not retain retry state")
	}
	if saved.PrivateKey != session.PrivateKey || saved.Macaroon != session.Macaroon {
		t.Fatal("credentials discarded before root deletion succeeded")
	}
}
