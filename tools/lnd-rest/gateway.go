package main

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"errors"
	"fmt"
	"net/http"
	"os"

	"github.com/btcsuite/btclog/v2"
	"github.com/grpc-ecosystem/grpc-gateway/v2/runtime"
	"github.com/lightningnetwork/lnd/lnrpc"
	"github.com/lightningnetwork/lnd/lnrpc/autopilotrpc"
	"github.com/lightningnetwork/lnd/lnrpc/chainrpc"
	"github.com/lightningnetwork/lnd/lnrpc/invoicesrpc"
	"github.com/lightningnetwork/lnd/lnrpc/neutrinorpc"
	"github.com/lightningnetwork/lnd/lnrpc/peersrpc"
	"github.com/lightningnetwork/lnd/lnrpc/routerrpc"
	"github.com/lightningnetwork/lnd/lnrpc/signrpc"
	"github.com/lightningnetwork/lnd/lnrpc/verrpc"
	"github.com/lightningnetwork/lnd/lnrpc/walletrpc"
	"github.com/lightningnetwork/lnd/lnrpc/watchtowerrpc"
	"github.com/lightningnetwork/lnd/lnrpc/wtclientrpc"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials"
)

// registrar registers one gRPC service's REST routes on the gateway mux.
type registrar struct {
	service  string
	register func(context.Context, *runtime.ServeMux, *grpc.ClientConn) error
}

// restServices are the services whose REST routes LND's release build
// registers (build tags autopilotrpc, chainrpc, invoicesrpc, neutrinorpc,
// peersrpc, signrpc, walletrpc, watchtowerrpc; routerrpc and verrpc are
// always in), plus the Lightning, State and WalletUnlocker services. A service
// NLightning does not serve answers UNIMPLEMENTED (HTTP 501) as it does over
// gRPC. Each handler is LND's own generated grpc-gateway code, so paths,
// methods and body bindings are exactly LND v0.21.4-beta's.
var restServices = []registrar{
	{"lnrpc.Lightning", lnrpc.RegisterLightningHandler},
	{"lnrpc.State", lnrpc.RegisterStateHandler},
	{"lnrpc.WalletUnlocker", lnrpc.RegisterWalletUnlockerHandler},
	{"routerrpc.Router", routerrpc.RegisterRouterHandler},
	{"invoicesrpc.Invoices", invoicesrpc.RegisterInvoicesHandler},
	{"walletrpc.WalletKit", walletrpc.RegisterWalletKitHandler},
	{"signrpc.Signer", signrpc.RegisterSignerHandler},
	{"chainrpc.ChainNotifier", chainrpc.RegisterChainNotifierHandler},
	{"chainrpc.ChainKit", chainrpc.RegisterChainKitHandler},
	{"verrpc.Versioner", verrpc.RegisterVersionerHandler},
	{"peersrpc.Peers", peersrpc.RegisterPeersHandler},
	{"autopilotrpc.Autopilot", autopilotrpc.RegisterAutopilotHandler},
	{"wtclientrpc.WatchtowerClient", wtclientrpc.RegisterWatchtowerClientHandler},
	{"watchtowerrpc.Watchtower", watchtowerrpc.RegisterWatchtowerHandler},
	{"neutrinorpc.NeutrinoKit", neutrinorpc.RegisterNeutrinoKitHandler},
}

// newGatewayMux builds the grpc-gateway mux exactly as LND's restProxy does:
// proto field names, every field emitted (64-bit integers as strings, bytes
// as base64, enums by name) and no HTTP method fallback. Request headers are
// forwarded with grpc-gateway's default matcher, as in LND: the client's
// Grpc-Metadata-Macaroon header becomes the "macaroon" metadata unchanged.
func newGatewayMux(ctx context.Context, conn *grpc.ClientConn) (*runtime.ServeMux, error) {
	mux := runtime.NewServeMux(
		runtime.WithMarshalerOption(runtime.MIMEWildcard, &runtime.JSONPb{
			MarshalOptions:   *lnrpc.RESTJsonMarshalOpts,
			UnmarshalOptions: *lnrpc.RESTJsonUnmarshalOpts,
		}),
		runtime.WithDisablePathLengthFallback(),
	)
	for _, s := range restServices {
		if err := s.register(ctx, mux, conn); err != nil {
			return nil, fmt.Errorf("register %s REST routes: %w", s.service, err)
		}
	}
	return mux, nil
}

// dialBackend opens a lazy gRPC client connection to NLightning over verified
// TLS. It carries no credential of its own: each call carries the REST
// client's macaroon.
func dialBackend(target, certPath, serverName string, maxMsgBytes int) (*grpc.ClientConn, error) {
	pemBytes, err := os.ReadFile(certPath)
	if err != nil {
		return nil, fmt.Errorf("read backend TLS certificate: %w", err)
	}
	roots := x509.NewCertPool()
	if !roots.AppendCertsFromPEM(pemBytes) {
		return nil, errors.New("backend TLS certificate contains no certificates")
	}
	return grpc.NewClient(target,
		grpc.WithTransportCredentials(credentials.NewTLS(&tls.Config{
			RootCAs: roots, ServerName: serverName, MinVersion: tls.VersionTLS12,
		})),
		grpc.WithDefaultCallOptions(
			grpc.MaxCallRecvMsgSize(maxMsgBytes),
			grpc.MaxCallSendMsgSize(maxMsgBytes),
		),
	)
}

// newRESTHandler builds the request chain LND's REST listener uses:
// CORS -> WebSocket proxy -> grpc-gateway, behind this sidecar's body limit
// and optional request log.
func newRESTHandler(mux http.Handler, c Config, logger btclog.Logger, logf func(string, ...any)) http.Handler {
	h := lnrpc.NewWebSocketProxy(mux, logger, c.WSPingInterval, c.WSPongWait, lnrpc.LndClientStreamingURIs)
	h = limitBody(h, c.MaxBodyBytes, c.BodyReadTimeout)
	h = allowCORS(h, c.CORSOrigins)
	if c.LogRequests {
		h = logRequests(h, logf)
	}
	return h
}
