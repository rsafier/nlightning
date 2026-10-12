package main

import (
	"context"
	"errors"
	"net"
	"sync/atomic"
	"testing"
	"time"

	"github.com/btcsuite/btcd/btcec/v2"
	"github.com/lightninglabs/lightning-node-connect/mailbox"
	"github.com/lightningnetwork/lnd/keychain"
)

// The upstream default initiator starts Act1 at version 0. A responder accepting
// that offer still sends its maximum version (2) in Act2; the final Act3 must
// use that negotiated version, which persists both authenticated static keys.
// Rejecting version-0 Act1 breaks ordinary upstream clients before negotiation.
func TestDefaultClientNegotiatesVersion2AndPinsBothIdentities(t *testing.T) {
	serverKey, _ := btcec.NewPrivateKey()
	clientKey, _ := btcec.NewPrivateKey()
	_, entropy, err := mailbox.NewPassphraseEntropy()
	if err != nil {
		t.Fatal(err)
	}
	var serverPins, clientPins atomic.Int32
	serverData := mailbox.NewConnData(&keychain.PrivKeyECDH{PrivKey: serverKey}, nil, entropy[:], sessionAuthData("cafe"), func(key *btcec.PublicKey) error {
		if !key.IsEqual(clientKey.PubKey()) {
			t.Error("wrong client identity")
		}
		serverPins.Add(1)
		return nil
	}, nil)
	clientData := mailbox.NewConnData(&keychain.PrivKeyECDH{PrivKey: clientKey}, nil, entropy[:], nil, func(key *btcec.PublicKey) error {
		if !key.IsEqual(serverKey.PubKey()) {
			t.Error("wrong server identity")
		}
		clientPins.Add(1)
		return nil
	}, nil)
	originalSID, err := serverData.SID()
	if err != nil {
		t.Fatal(err)
	}
	server := serverNoiseCredentials(serverData)
	client := mailbox.NewNoiseGrpcConn(clientData)
	left, right := net.Pipe()
	defer left.Close()
	defer right.Close()
	_ = left.SetDeadline(time.Now().Add(15 * time.Second))
	_ = right.SetDeadline(time.Now().Add(15 * time.Second))
	result := make(chan error, 1)
	go func() { _, _, err := server.ServerHandshake(pipeProxyConn{left}); result <- err }()
	if _, _, err = client.ClientHandshake(context.Background(), "", pipeProxyConn{right}); err != nil {
		t.Fatal("default upstream client handshake", err)
	}
	if err = <-result; err != nil {
		t.Fatal("responder handshake", err)
	}
	if serverPins.Load() != 1 || clientPins.Load() != 1 {
		t.Fatalf("static key persistence not invoked: server=%d client=%d", serverPins.Load(), clientPins.Load())
	}
	serverSID, err := serverData.SID()
	if err != nil {
		t.Fatal(err)
	}
	clientSID, err := clientData.SID()
	if err != nil {
		t.Fatal(err)
	}
	if serverSID != clientSID || serverSID == originalSID {
		t.Fatal("session ID did not rotate to authenticated ECDH identities")
	}
	if string(clientData.AuthData()) != "Macaroon: cafe" {
		t.Fatal("encrypted credential not transferred")
	}
}

// Handshake uses net.Conn Read/Write; control messages are only used by actual
// mailbox connections after transport negotiation.
type pipeProxyConn struct{ net.Conn }

func (pipeProxyConn) ReceiveControlMsg(mailbox.ControlMsg) error {
	return errors.New("control message unexpected during handshake")
}
func (pipeProxyConn) SendControlMsg(mailbox.ControlMsg) error {
	return errors.New("control message unexpected during handshake")
}
func (pipeProxyConn) SetRecvTimeout(time.Duration) {}
func (pipeProxyConn) SetSendTimeout(time.Duration) {}
