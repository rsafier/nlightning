package main

import "github.com/lightninglabs/lightning-node-connect/mailbox"

func serverNoiseCredentials(data *mailbox.ConnData) *mailbox.NoiseGrpcConn {
	// Default clients offer version 0 in Act1. The responder advertises version 2
	// in Act2 and requires that version in Act3 before persisting the client key.
	return mailbox.NewNoiseGrpcConn(data, mailbox.WithMaxHandshakeVersion(mailbox.HandshakeVersion2))
}
