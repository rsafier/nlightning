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

// NewSessionProxy binds a bridge to exactly one session credential. Noise's
// AuthData gives that credential to the paired client; accepting arbitrary
// caller macaroons here would let a compromised client escape its session.
func NewSessionProxy(backend grpc.ClientConnInterface, macaroonHex string,
	options ...grpc.ServerOption) (*grpc.Server, error) {
	credential, err := hex.DecodeString(macaroonHex)
	if err != nil || len(credential) == 0 {
		return nil, errors.New("session macaroon must be nonempty hexadecimal")
	}
	handler := func(_ interface{}, inbound grpc.ServerStream) error {
		method, ok := grpc.MethodFromServerStream(inbound)
		if !ok {
			return status.Error(codes.Internal, "missing RPC method")
		}
		md, _ := metadata.FromIncomingContext(inbound.Context())
		values := md.Get("macaroon")
		if len(values) != 1 {
			return status.Error(codes.Unauthenticated, "session credential required")
		}
		provided, err := hex.DecodeString(values[0])
		if err != nil || subtle.ConstantTimeCompare(provided, credential) != 1 {
			return status.Error(codes.Unauthenticated, "invalid session credential")
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
	options = append(options, grpc.ForceServerCodec(opaqueCodec{}), grpc.UnknownServiceHandler(handler),
		grpc.MaxRecvMsgSize(maxProxyMessageBytes), grpc.MaxSendMsgSize(maxProxyMessageBytes))
	return grpc.NewServer(options...), nil
}
