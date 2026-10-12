package main

import "github.com/lightninglabs/lightning-node-connect/mailbox"

// authDataMacaroonHeader is the header litd puts in the Noise handshake's auth
// data (lightning-terminal session_rpcserver.go: "%s: %s" with HeaderMacaroon =
// "Macaroon"). The stock LNC WASM client (lnc-web, cmd/wasm-client/main.go)
// splits the auth data on ": " and refuses anything whose first part is not
// exactly "Macaroon", failing the transport with "authdata does not contain a
// macaroon". Its gRPC transport then sends the same pair as per-RPC metadata,
// which grpc-go lowercases to "macaroon".
const authDataMacaroonHeader = "Macaroon"

// sessionAuthData is the handshake auth data for one session: its scoped,
// expiring backend macaroon in hex, exactly as litd sends a session macaroon.
// The client reads its permissions (lnc.hasPerms) and expiry from it.
func sessionAuthData(macaroonHex string) []byte {
	return []byte(authDataMacaroonHeader + ": " + macaroonHex)
}

func serverNoiseCredentials(data *mailbox.ConnData) *mailbox.NoiseGrpcConn {
	// Default clients offer version 0 in Act1. The responder advertises version 2
	// in Act2 and requires that version in Act3 before persisting the client key.
	return mailbox.NewNoiseGrpcConn(data, mailbox.WithMaxHandshakeVersion(mailbox.HandshakeVersion2))
}
