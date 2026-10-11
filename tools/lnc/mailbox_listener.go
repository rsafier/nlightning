// Copyright (c) 2021 Lightning Labs
// Derived from lightning-node-connect/mailbox/server.go under the MIT license.
// See UPSTREAM_LICENSE for the complete license notice.

package main

import (
	"context"
	"net"
	"sync"

	"github.com/btcsuite/btclog/v2"
	"github.com/lightninglabs/lightning-node-connect/hashmailrpc"
	"github.com/lightninglabs/lightning-node-connect/mailbox"
	"google.golang.org/grpc"
)

// mailboxListener owns the relay channel as well as upstream mailbox connections.
// Upstream mailbox.Server implements this acceptance sequence but does not close
// its private relay ClientConn. Keeping ownership here releases that connection
// when a session expires or is revoked, without replacing the upstream protocol.
// Acceptance/SID rotation follows mailbox/server.go (MIT, Lightning Labs).
type mailboxListener struct {
	host      string
	data      *mailbox.ConnData
	relay     *grpc.ClientConn
	client    hashmailrpc.HashMailClient
	status    func(mailbox.ServerStatus)
	log       btclog.Logger
	ctx       context.Context
	cancel    context.CancelFunc
	acceptMu  sync.Mutex
	addrMu    sync.RWMutex
	sid       [64]byte
	conn      *mailbox.ServerConn // Protected by acceptMu, including cleanup.
	closeOnce sync.Once
	closeErr  error
}

func newMailboxListener(ctx context.Context, host string, data *mailbox.ConnData,
	status func(mailbox.ServerStatus), logger btclog.Logger, opts ...grpc.DialOption) (net.Listener, error) {
	if logger == nil {
		logger = btclog.Disabled
	}
	sid, err := data.SID()
	if err != nil {
		return nil, err
	}
	relay, err := grpc.DialContext(ctx, host, opts...)
	if err != nil {
		return nil, err
	}
	lifecycle, cancel := context.WithCancel(ctx)
	return &mailboxListener{host: host, data: data, relay: relay, client: hashmailrpc.NewHashMailClient(relay), status: status, log: logger, ctx: lifecycle, cancel: cancel, sid: sid}, nil
}

func (l *mailboxListener) Accept() (net.Conn, error) {
	l.acceptMu.Lock()
	defer l.acceptMu.Unlock()
	if l.ctx.Err() != nil {
		return nil, net.ErrClosed
	}
	if l.conn != nil {
		select {
		case <-l.ctx.Done():
			return nil, net.ErrClosed
		case <-l.conn.Done():
		}
	}
	sid, err := l.data.SID()
	if err != nil {
		return nil, err
	}
	l.addrMu.RLock()
	oldSID := l.sid
	l.addrMu.RUnlock()
	if oldSID != sid && l.conn != nil {
		_ = l.conn.Stop()
		l.conn = nil
	}
	l.addrMu.Lock()
	l.sid = sid
	l.addrMu.Unlock()
	var next *mailbox.ServerConn
	if l.conn == nil {
		next, err = mailbox.NewServerConn(l.ctx, l.host, l.client, sid, l.log, l.status)
	} else {
		next, err = mailbox.RefreshServerConn(l.conn)
	}
	if err != nil {
		if l.ctx.Err() != nil {
			return nil, net.ErrClosed
		}
		return nil, temporaryMailboxError{err}
	}
	l.conn = next
	if l.ctx.Err() != nil {
		return nil, net.ErrClosed
	}
	return next, nil
}

type temporaryMailboxError struct{ error }

func (temporaryMailboxError) Temporary() bool { return true }

func (l *mailboxListener) Close() error {
	l.closeOnce.Do(func() {
		// Cancel first so a stalled mailbox handshake/Accept cannot block cleanup.
		// Close the relay channel before waiting for Accept to finish: even an RPC
		// awaiting relay response is released when no mailbox peer is available.
		l.cancel()
		l.closeErr = l.relay.Close()
		l.acceptMu.Lock()
		defer l.acceptMu.Unlock()
		if l.conn != nil {
			_ = l.conn.Stop()
			l.conn = nil
		}
	})
	return l.closeErr
}
func (l *mailboxListener) Addr() net.Addr {
	l.addrMu.RLock()
	defer l.addrMu.RUnlock()
	return &mailbox.Addr{SID: l.sid, Server: l.host}
}
