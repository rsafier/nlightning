package main

import (
	"context"
	"errors"
	"net"
	"testing"
	"time"

	"github.com/btcsuite/btcd/btcec/v2"
	"github.com/lightninglabs/lightning-node-connect/mailbox"
	"github.com/lightningnetwork/lnd/keychain"
	"google.golang.org/grpc"
	"google.golang.org/grpc/connectivity"
	"google.golang.org/grpc/credentials/insecure"
)

func listenerForTest(t *testing.T) *mailboxListener {
	t.Helper()
	key, err := btcec.NewPrivateKey()
	if err != nil {
		t.Fatal(err)
	}
	data := mailbox.NewConnData(&keychain.PrivKeyECDH{PrivKey: key}, nil, make([]byte, mailbox.NumPassphraseEntropyBytes), nil, nil, nil)
	listener, err := newMailboxListener(context.Background(), "127.0.0.1:1", data, nil, nil, grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = listener.Close() })
	return listener.(*mailboxListener)
}
func TestMailboxListenerCloseIsIdempotentAndClosesRelay(t *testing.T) {
	listener := listenerForTest(t)
	if err := listener.Close(); err != nil {
		t.Fatal(err)
	}
	if err := listener.Close(); err != nil {
		t.Fatal(err)
	}
	if listener.relay.GetState() != connectivity.Shutdown {
		t.Fatal("relay channel still running")
	}
	if _, err := listener.Accept(); !errors.Is(err, net.ErrClosed) {
		t.Fatalf("Accept after Close: %v", err)
	}
}
func TestMailboxListenerCloseCancelsAcceptWithoutPeer(t *testing.T) {
	listener := listenerForTest(t)
	done := make(chan error, 1)
	go func() { _, err := listener.Accept(); done <- err }()
	deadline := time.After(time.Second)
	tick := time.NewTicker(time.Millisecond)
	defer tick.Stop()
	for listener.acceptMu.TryLock() {
		listener.acceptMu.Unlock()
		select {
		case <-tick.C:
		case <-deadline:
			t.Fatal("Accept never started")
		}
	}
	if err := listener.Close(); err != nil {
		t.Fatal(err)
	}
	select {
	case err := <-done:
		if !errors.Is(err, net.ErrClosed) {
			t.Fatalf("Accept: %v", err)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("Close left Accept blocked")
	}
}
