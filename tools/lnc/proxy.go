package main

import (
	"context"
	"crypto/subtle"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"os"
	"strings"
	"time"

	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"
)

// opaqueCodec forwards protobuf messages without interpreting their schema.
// Each receive owns its buffer: a concurrent stream cannot overwrite it.
type opaqueCodec struct{}

// Graph snapshots can exceed gRPC's default 4 MiB. Keep an explicit bound on
// both bridge directions rather than permitting unbounded frame allocation.
const maxProxyMessageBytes = 32 * 1024 * 1024

func (opaqueCodec) Name() string { return "proto" }
func (opaqueCodec) Marshal(value interface{}) ([]byte, error) {
	frame, ok := value.(*[]byte)
	if !ok {
		return nil, fmt.Errorf("opaque codec requires *[]byte, got %T", value)
	}
	return *frame, nil
}
func (opaqueCodec) Unmarshal(data []byte, value interface{}) error {
	frame, ok := value.(*[]byte)
	if !ok {
		return fmt.Errorf("opaque codec requires *[]byte, got %T", value)
	}
	*frame = append((*frame)[:0], data...)
	return nil
}

// DialBackend trusts only the configured certificate bundle and verifies the
// endpoint hostname (or explicit TLS server name). It adds no credentials;
// administrative CLI calls and paired calls must supply their own macaroon.
func DialBackend(ctx context.Context, target, tlsCertPath, serverName string) (*grpc.ClientConn, error) {
	pem, err := os.ReadFile(tlsCertPath)
	if err != nil {
		return nil, fmt.Errorf("read backend TLS certificate: %w", err)
	}
	roots := x509.NewCertPool()
	if !roots.AppendCertsFromPEM(pem) {
		return nil, errors.New("backend TLS certificate contains no certificates")
	}
	return grpc.DialContext(ctx, target,
		grpc.WithTransportCredentials(credentials.NewTLS(&tls.Config{
			RootCAs: roots, ServerName: serverName, MinVersion: tls.VersionTLS12,
		})), grpc.WithDefaultCallOptions(grpc.MaxCallRecvMsgSize(maxProxyMessageBytes),
			grpc.MaxCallSendMsgSize(maxProxyMessageBytes)), grpc.WithBlock())
}

// CallInfo describes an authenticated call.
type CallInfo struct {
	// Meta is the litd firewall meta information the caller attached to its
	// credential as a caveat (autopilot sessions only; nil otherwise).
	Meta *MetaInfo
}

// LocalHandler answers one unary call in the bridge: it gets the encoded
// request and returns the encoded response.
type LocalHandler func(ctx context.Context, call CallInfo, request []byte) ([]byte, error)

// ProxyConfig holds a session proxy's optional behavior.
type ProxyConfig struct {
	// Local maps full method names the bridge answers itself (the litrpc
	// shims, and every call of an autopilot session) to their handler. Every
	// other method is forwarded to the backend unless LocalOnly is set.
	Local map[string]LocalHandler
	// LocalOnly refuses every method that is not in Local: nothing is
	// forwarded as is (autopilot sessions).
	LocalOnly bool
	// Authenticate checks the credential the client presented. Nil means it
	// must equal the session credential byte for byte.
	Authenticate func(presented []byte) (CallInfo, error)
	// OnAuthenticated runs on every call that presented the session credential,
	// before it is answered or forwarded.
	OnAuthenticated func()
	// LogRPC, when set, receives each call's method, final status code and
	// duration. It never receives payloads or metadata.
	LogRPC func(method string, code codes.Code, elapsed time.Duration)
}

// NewSessionProxy binds a bridge to exactly one session credential. Noise's
// AuthData gives that credential to the paired client; accepting arbitrary
// caller macaroons here would let a compromised client escape its session.
func NewSessionProxy(backend grpc.ClientConnInterface, macaroonHex string,
	options ...grpc.ServerOption) (*grpc.Server, error) {
	return NewSessionProxyWith(backend, macaroonHex, ProxyConfig{}, options...)
}

// NewSessionProxyWith is NewSessionProxy with a ProxyConfig.
func NewSessionProxyWith(backend grpc.ClientConnInterface, macaroonHex string, config ProxyConfig,
	options ...grpc.ServerOption) (*grpc.Server, error) {
	credential, err := hex.DecodeString(macaroonHex)
	if err != nil || len(credential) == 0 {
		return nil, errors.New("session macaroon must be nonempty hexadecimal")
	}
	forward := func(method string, inbound grpc.ServerStream) error {
		md, _ := metadata.FromIncomingContext(inbound.Context())
		values := md.Get("macaroon")
		if len(values) != 1 {
			return status.Error(codes.Unauthenticated, "session credential required")
		}
		provided, err := hex.DecodeString(values[0])
		if err != nil {
			return status.Error(codes.Unauthenticated, "invalid session credential")
		}
		var call CallInfo
		if config.Authenticate != nil {
			if call, err = config.Authenticate(provided); err != nil {
				return status.Error(codes.Unauthenticated, "invalid session credential")
			}
		} else if subtle.ConstantTimeCompare(provided, credential) != 1 {
			return status.Error(codes.Unauthenticated, "invalid session credential")
		}
		if config.OnAuthenticated != nil {
			config.OnAuthenticated()
		}
		if local, ok := config.Local[method]; ok {
			var request []byte
			if err := inbound.RecvMsg(&request); err != nil {
				return err
			}
			response, err := local(inbound.Context(), call, request)
			if err != nil {
				if _, ok := status.FromError(err); ok {
					return err
				}
				return status.Error(codes.Unknown, err.Error())
			}
			return inbound.SendMsg(&response)
		}
		// litrpc calls are never forwarded: the node does not serve them, and a
		// session without the local grant must not reach them.
		if config.LocalOnly || strings.HasPrefix(method, "/litrpc.") {
			return status.Errorf(codes.PermissionDenied, "%s is not permitted for this session", method)
		}
		outgoing := md.Copy()
		outgoing.Set("macaroon", hex.EncodeToString(credential))
		ctx, cancel := context.WithCancel(metadata.NewOutgoingContext(inbound.Context(), outgoing))
		defer cancel()
		outbound, err := backend.NewStream(ctx,
			&grpc.StreamDesc{ServerStreams: true, ClientStreams: true}, method,
			grpc.ForceCodec(opaqueCodec{}), grpc.MaxCallRecvMsgSize(maxProxyMessageBytes),
			grpc.MaxCallSendMsgSize(maxProxyMessageBytes))
		if err != nil {
			return err
		}
		// Client half-close is independent of responses. Client receive errors
		// cancel the backend; backend send errors leave its receive direction
		// running so trailers and the real backend status remain available.
		clientFailure := make(chan error, 1)
		go func() {
			for {
				var frame []byte
				if err := inbound.RecvMsg(&frame); err != nil {
					if errors.Is(err, io.EOF) {
						_ = outbound.CloseSend()
					} else {
						clientFailure <- err
						cancel()
					}
					return
				}
				if outbound.SendMsg(&frame) != nil {
					return
				}
			}
		}()
		headers, err := outbound.Header()
		if err == nil && len(headers) != 0 {
			if err := inbound.SendHeader(headers); err != nil {
				return err
			}
		}
		// Header can fail when a backend sends trailers-only; RecvMsg retains
		// its final status and Trailer returns the complete trailer metadata.
		for {
			var frame []byte
			err := outbound.RecvMsg(&frame)
			if err != nil {
				inbound.SetTrailer(outbound.Trailer())
				select {
				case clientErr := <-clientFailure:
					return clientErr
				default:
				}
				if errors.Is(err, io.EOF) {
					return nil
				}
				return err
			}
			if err := inbound.SendMsg(&frame); err != nil {
				return err
			}
		}
	}
	handler := func(_ interface{}, inbound grpc.ServerStream) error {
		method, ok := grpc.MethodFromServerStream(inbound)
		if !ok {
			return status.Error(codes.Internal, "missing RPC method")
		}
		if config.LogRPC == nil {
			return forward(method, inbound)
		}
		start := time.Now()
		err := forward(method, inbound)
		config.LogRPC(method, status.Code(err), time.Since(start))
		return err
	}
	options = append(options, grpc.ForceServerCodec(opaqueCodec{}), grpc.UnknownServiceHandler(handler),
		grpc.MaxRecvMsgSize(maxProxyMessageBytes), grpc.MaxSendMsgSize(maxProxyMessageBytes))
	return grpc.NewServer(options...), nil
}
