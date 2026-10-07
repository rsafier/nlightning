package main

import (
	"context"
	"crypto/tls"
	"errors"
	"fmt"
	"log"
	"net"
	"net/http"
	"sync"

	"github.com/btcsuite/btclog/v2"
	"golang.org/x/net/netutil"
	"google.golang.org/grpc"
)

// Server is a running REST gateway.
type Server struct {
	cfg      Config
	listener net.Listener
	http     *http.Server
	conn     *grpc.ClientConn
	cancel   context.CancelFunc
	// CertPath is the listener certificate clients should trust (empty with
	// --no-tls).
	CertPath string
	logf     func(string, ...any)
	done     chan error
	stopOnce sync.Once
}

// newLogger returns the process logger and LND's btclog logger for its
// WebSocket proxy, both on stderr.
func newLogger() (func(string, ...any), btclog.Logger) {
	std := log.New(log.Writer(), "", log.LstdFlags)
	logger := btclog.NewSLogger(btclog.NewDefaultHandler(log.Writer()))
	logger.SetLevel(btclog.LevelInfo)
	return std.Printf, logger
}

// Start opens the backend connection and the REST listener and serves until
// Shutdown. The backend is dialed lazily: REST calls answer UNAVAILABLE
// (HTTP 503) while the node is down and work again once it is back.
func Start(c Config) (*Server, error) {
	logf, logger := newLogger()
	return startWithLogger(c, logf, logger)
}

func startWithLogger(c Config, logf func(string, ...any), logger btclog.Logger) (*Server, error) {
	conn, err := dialBackend(c.Backend, c.BackendTLSCert, c.BackendTLSServerName, c.MaxMsgBytes)
	if err != nil {
		return nil, err
	}
	baseCtx, cancel := context.WithCancel(context.Background())
	fail := func(err error) (*Server, error) {
		cancel()
		_ = conn.Close()
		return nil, err
	}
	mux, err := newGatewayMux(baseCtx, conn)
	if err != nil {
		return fail(err)
	}
	var tlsConfig *tls.Config
	certPath := ""
	if !c.NoTLS {
		if tlsConfig, certPath, err = listenerTLS(c, logf); err != nil {
			return fail(err)
		}
	}
	lis, err := net.Listen("tcp", c.Listen)
	if err != nil {
		return fail(fmt.Errorf("listen on %s: %w", c.Listen, err))
	}
	if c.MaxConns > 0 {
		lis = netutil.LimitListener(lis, c.MaxConns)
	}
	if tlsConfig != nil {
		lis = tls.NewListener(lis, tlsConfig)
	}
	s := &Server{
		cfg:      c,
		listener: lis,
		conn:     conn,
		cancel:   cancel,
		CertPath: certPath,
		logf:     logf,
		done:     make(chan error, 1),
		http: &http.Server{
			Handler:           newRESTHandler(mux, c, logger, logf),
			ReadHeaderTimeout: c.ReadHeaderTimeout,
			IdleTimeout:       c.IdleTimeout,
			MaxHeaderBytes:    256 << 10,
			// Request contexts derive from baseCtx, so cancelling it ends
			// every stream, WebSocket sessions (hijacked, untracked by
			// Shutdown) included.
			BaseContext: func(net.Listener) context.Context { return baseCtx },
			// HTTP/1.1 only, like LND's REST listener.
			TLSNextProto: map[string]func(*http.Server, *tls.Conn, http.Handler){},
			ErrorLog:     log.New(log.Writer(), "http: ", log.LstdFlags),
		},
	}
	go func() {
		err := s.http.Serve(lis)
		if errors.Is(err, http.ErrServerClosed) {
			err = nil
		}
		s.done <- err
	}()
	scheme := "https"
	if c.NoTLS {
		scheme = "http"
	}
	logf("serving LND REST on %s://%s, forwarding to %s", scheme, lis.Addr(), c.Backend)
	return s, nil
}

// Addr is the listener's address.
func (s *Server) Addr() net.Addr { return s.listener.Addr() }

// Done is closed with the serve error (nil after Shutdown).
func (s *Server) Done() <-chan error { return s.done }

// Shutdown stops accepting, lets unary calls finish within ctx, then ends
// the remaining streams and closes the backend connection.
func (s *Server) Shutdown(ctx context.Context) error {
	var err error
	s.stopOnce.Do(func() {
		err = s.http.Shutdown(ctx)
		if err != nil {
			s.logf("graceful shutdown incomplete, closing: %v", err)
			_ = s.http.Close()
		}
		s.cancel()
		_ = s.conn.Close()
	})
	return err
}
