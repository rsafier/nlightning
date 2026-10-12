package main

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"io"
	"math/big"
	"net"
	"os"
	"path/filepath"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"
	"google.golang.org/grpc/test/bufconn"
	"google.golang.org/protobuf/types/known/wrapperspb"
)

func localConn(t *testing.T, server *grpc.Server) *grpc.ClientConn {
	t.Helper()
	listener := bufconn.Listen(1 << 20)
	go func() { _ = server.Serve(listener) }()
	t.Cleanup(func() { server.Stop(); _ = listener.Close() })
	conn, err := grpc.DialContext(context.Background(), "passthrough:///test",
		grpc.WithContextDialer(func(context.Context, string) (net.Conn, error) { return listener.Dial() }),
		grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = conn.Close() })
	return conn
}

func proxyConn(t *testing.T, handler grpc.StreamHandler) *grpc.ClientConn {
	t.Helper()
	backend := localConn(t, grpc.NewServer(grpc.ForceServerCodec(opaqueCodec{}), grpc.UnknownServiceHandler(handler)))
	bridge, err := NewSessionProxy(backend, "010203")
	if err != nil {
		t.Fatal(err)
	}
	return localConn(t, bridge)
}

func rawStream(ctx context.Context, conn *grpc.ClientConn, method string) (grpc.ClientStream, error) {
	return conn.NewStream(ctx, &grpc.StreamDesc{ServerStreams: true, ClientStreams: true}, method,
		grpc.ForceCodec(opaqueCodec{}))
}

func TestProxyPreservesOpaqueFramesMetadataAndBackendStatus(t *testing.T) {
	conn := proxyConn(t, func(_ interface{}, stream grpc.ServerStream) error {
		md, _ := metadata.FromIncomingContext(stream.Context())
		if got := md.Get("macaroon"); len(got) != 1 || got[0] != "010203" {
			t.Errorf("credential = %v", got)
		}
		if got := md.Get("request-id"); len(got) != 1 || got[0] != "test" {
			t.Errorf("metadata = %v", got)
		}
		if err := stream.SendHeader(metadata.Pairs("backend-header", "hello")); err != nil {
			return err
		}
		stream.SetTrailer(metadata.Pairs("backend-trailer", "done"))
		for {
			var frame []byte
			if err := stream.RecvMsg(&frame); err != nil {
				if err == io.EOF {
					return status.Error(codes.FailedPrecondition, "backend rejection")
				}
				return err
			}
			if err := stream.SendMsg(&frame); err != nil {
				return err
			}
		}
	})
	ctx, cancel := context.WithTimeout(metadata.NewOutgoingContext(context.Background(),
		metadata.Pairs("macaroon", "010203", "request-id", "test")), 5*time.Second)
	defer cancel()
	stream, err := rawStream(ctx, conn, "/unknown.Service/Bidi")
	if err != nil {
		t.Fatal(err)
	}
	for _, frame := range [][]byte{{0, 255, 1}, {}, bytes.Repeat([]byte{17}, 65536)} {
		if err := stream.SendMsg(&frame); err != nil {
			t.Fatal(err)
		}
		var received []byte
		if err := stream.RecvMsg(&received); err != nil {
			t.Fatal(err)
		}
		if !bytes.Equal(frame, received) {
			t.Fatal("frame changed")
		}
	}
	if err := stream.CloseSend(); err != nil {
		t.Fatal(err)
	}
	var frame []byte
	if err := stream.RecvMsg(&frame); status.Code(err) != codes.FailedPrecondition || status.Convert(err).Message() != "backend rejection" {
		t.Fatalf("status = %v", err)
	}
	headers, err := stream.Header()
	if err != nil || headers.Get("backend-header")[0] != "hello" {
		t.Fatalf("headers = %v, %v", headers, err)
	}
	if stream.Trailer().Get("backend-trailer")[0] != "done" {
		t.Fatalf("trailers = %v", stream.Trailer())
	}
}

func TestProxyRejectsCredentialSubstitutionBeforeBackendCall(t *testing.T) {
	var calls atomic.Int32
	conn := proxyConn(t, func(_ interface{}, stream grpc.ServerStream) error { calls.Add(1); return nil })
	for name, md := range map[string]metadata.MD{
		"missing": {}, "other session": metadata.Pairs("macaroon", "aabbcc"),
		"malformed": metadata.Pairs("macaroon", "admin"),
		"duplicate": metadata.Pairs("macaroon", "010203", "macaroon", "010203"),
	} {
		t.Run(name, func(t *testing.T) {
			ctx, cancel := context.WithTimeout(metadata.NewOutgoingContext(context.Background(), md), time.Second)
			defer cancel()
			stream, err := rawStream(ctx, conn, "/test.Service/Call")
			if err != nil {
				t.Fatal(err)
			}
			_ = stream.CloseSend()
			var frame []byte
			if err := stream.RecvMsg(&frame); status.Code(err) != codes.Unauthenticated {
				t.Fatalf("status = %v", err)
			}
		})
	}
	if calls.Load() != 0 {
		t.Fatalf("backend calls = %d", calls.Load())
	}
}

func TestProxyCancelsBackendAndPreservesDeadline(t *testing.T) {
	canceled := make(chan struct{}, 1)
	conn := proxyConn(t, func(_ interface{}, stream grpc.ServerStream) error {
		if _, ok := stream.Context().Deadline(); !ok {
			t.Error("backend deadline missing")
		}
		<-stream.Context().Done()
		canceled <- struct{}{}
		return status.FromContextError(stream.Context().Err()).Err()
	})
	ctx, cancel := context.WithTimeout(metadata.NewOutgoingContext(context.Background(), metadata.Pairs("macaroon", "010203")), 500*time.Millisecond)
	defer cancel()
	stream, err := rawStream(ctx, conn, "/test.Service/Wait")
	if err != nil {
		t.Fatal(err)
	}
	var frame []byte
	if err := stream.RecvMsg(&frame); status.Code(err) != codes.DeadlineExceeded {
		t.Fatalf("status = %v", err)
	}
	select {
	case <-canceled:
	case <-time.After(time.Second):
		t.Fatal("backend not canceled")
	}
}

func TestProxyConcurrentStreamsDoNotShareFrames(t *testing.T) {
	conn := proxyConn(t, func(_ interface{}, stream grpc.ServerStream) error {
		for {
			var frame []byte
			if err := stream.RecvMsg(&frame); err != nil {
				if err == io.EOF {
					return nil
				}
				return err
			}
			if err := stream.SendMsg(&frame); err != nil {
				return err
			}
		}
	})
	var wg sync.WaitGroup
	for id := byte(1); id <= 12; id++ {
		wg.Add(1)
		go func(id byte) {
			defer wg.Done()
			ctx, cancel := context.WithTimeout(metadata.NewOutgoingContext(context.Background(), metadata.Pairs("macaroon", "010203")), 5*time.Second)
			defer cancel()
			stream, err := rawStream(ctx, conn, "/test.Service/Echo")
			if err != nil {
				t.Error(err)
				return
			}
			frame := bytes.Repeat([]byte{id}, 4096)
			for n := 0; n < 20; n++ {
				if err := stream.SendMsg(&frame); err != nil {
					t.Error(err)
					return
				}
				var received []byte
				if err := stream.RecvMsg(&received); err != nil {
					t.Error(err)
					return
				}
				if !bytes.Equal(frame, received) {
					t.Error("cross-stream corruption")
					return
				}
			}
			_ = stream.CloseSend()
			var received []byte
			if err := stream.RecvMsg(&received); err != io.EOF {
				t.Errorf("stream completion: %v", err)
			}
		}(id)
	}
	wg.Wait()
}

func TestProxyRejectsInvalidConfiguredCredential(t *testing.T) {
	for _, credential := range []string{"", "zz", "1"} {
		if _, err := NewSessionProxy(nil, credential); err == nil {
			t.Errorf("accepted %q", credential)
		}
	}
}

func TestProxyUnaryUsesNormalProtobufClient(t *testing.T) {
	conn := proxyConn(t, func(_ interface{}, stream grpc.ServerStream) error {
		var frame []byte
		if err := stream.RecvMsg(&frame); err != nil {
			return err
		}
		return stream.SendMsg(&frame)
	})
	ctx, cancel := context.WithTimeout(metadata.NewOutgoingContext(context.Background(), metadata.Pairs("macaroon", "010203")), time.Second)
	defer cancel()
	request := wrapperspb.String("protobuf through opaque bridge")
	response := new(wrapperspb.StringValue)
	if err := conn.Invoke(ctx, "/test.Service/Unary", request, response); err != nil {
		t.Fatal(err)
	}
	if response.Value != request.Value {
		t.Fatalf("response = %q", response.Value)
	}
}

func TestProxyEarlyBackendRejectionWithoutClientHalfClose(t *testing.T) {
	conn := proxyConn(t, func(_ interface{}, stream grpc.ServerStream) error {
		stream.SetTrailer(metadata.Pairs("rejection", "denied"))
		return status.Error(codes.PermissionDenied, "scope denied")
	})
	ctx, cancel := context.WithTimeout(metadata.NewOutgoingContext(context.Background(), metadata.Pairs("macaroon", "010203")), time.Second)
	defer cancel()
	stream, err := rawStream(ctx, conn, "/test.Service/Denied")
	if err != nil {
		t.Fatal(err)
	}
	// The caller leaves its send direction open. A trailers-only backend
	// response must still finish the call immediately with its actual status.
	var frame []byte
	if err := stream.RecvMsg(&frame); status.Code(err) != codes.PermissionDenied {
		t.Fatalf("status = %v", err)
	}
	if values := stream.Trailer().Get("rejection"); len(values) != 1 || values[0] != "denied" {
		t.Fatalf("trailers = %v", stream.Trailer())
	}
}

func TestProxyForwardsLargeServerMessageBeforeFirstClientRequest(t *testing.T) {
	payload := bytes.Repeat([]byte{91}, 6*1024*1024)
	conn := proxyConn(t, func(_ interface{}, stream grpc.ServerStream) error {
		if err := stream.SendHeader(metadata.Pairs("ready", "yes")); err != nil {
			return err
		}
		return stream.SendMsg(&payload)
	})
	ctx, cancel := context.WithTimeout(metadata.NewOutgoingContext(context.Background(), metadata.Pairs("macaroon", "010203")), 5*time.Second)
	defer cancel()
	stream, err := conn.NewStream(ctx, &grpc.StreamDesc{ServerStreams: true, ClientStreams: true}, "/test.Service/Subscribe",
		grpc.ForceCodec(opaqueCodec{}), grpc.MaxCallRecvMsgSize(maxProxyMessageBytes))
	if err != nil {
		t.Fatal(err)
	}
	var received []byte
	if err := stream.RecvMsg(&received); err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(received, payload) {
		t.Fatal("large response changed")
	}
	if err := stream.RecvMsg(&received); err != io.EOF {
		t.Fatalf("completion = %v", err)
	}
}

func TestProxyOversizedClientFrameRetainsResourceExhausted(t *testing.T) {
	conn := proxyConn(t, func(_ interface{}, stream grpc.ServerStream) error {
		<-stream.Context().Done()
		return status.FromContextError(stream.Context().Err()).Err()
	})
	ctx, cancel := context.WithTimeout(metadata.NewOutgoingContext(context.Background(), metadata.Pairs("macaroon", "010203")), 5*time.Second)
	defer cancel()
	stream, err := conn.NewStream(ctx, &grpc.StreamDesc{ServerStreams: true, ClientStreams: true}, "/test.Service/Send",
		grpc.ForceCodec(opaqueCodec{}), grpc.MaxCallSendMsgSize(maxProxyMessageBytes+1))
	if err != nil {
		t.Fatal(err)
	}
	payload := make([]byte, maxProxyMessageBytes+1)
	_ = stream.SendMsg(&payload)
	var received []byte
	if err := stream.RecvMsg(&received); status.Code(err) != codes.ResourceExhausted {
		t.Fatalf("oversized frame status = %v", err)
	}
}

func backendCertificate(t *testing.T, hostname string) (tls.Certificate, string) {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	template := &x509.Certificate{SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: hostname},
		DNSNames: []string{hostname}, NotBefore: time.Now().Add(-time.Hour), NotAfter: time.Now().Add(time.Hour),
		KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}}
	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	keyDER, err := x509.MarshalPKCS8PrivateKey(key)
	if err != nil {
		t.Fatal(err)
	}
	certPEM := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})
	cert, err := tls.X509KeyPair(certPEM, pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: keyDER}))
	if err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(t.TempDir(), "tls.cert")
	if err := os.WriteFile(path, certPEM, 0600); err != nil {
		t.Fatal(err)
	}
	return cert, path
}

func TestDialBackendRequiresTrustedCertificateAndHostname(t *testing.T) {
	certificate, certPath := backendCertificate(t, "node.local")
	_, unrelatedPath := backendCertificate(t, "node.local")
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	server := grpc.NewServer(grpc.Creds(credentials.NewTLS(&tls.Config{Certificates: []tls.Certificate{certificate}, MinVersion: tls.VersionTLS12})))
	go func() { _ = server.Serve(listener) }()
	t.Cleanup(func() { server.Stop(); _ = listener.Close() })
	for _, test := range []struct {
		name, path, hostname string
		allowed              bool
	}{
		{"trusted", certPath, "node.local", true},
		{"wrong hostname", certPath, "other.local", false},
		{"wrong certificate", unrelatedPath, "node.local", false},
	} {
		t.Run(test.name, func(t *testing.T) {
			timeout := 300 * time.Millisecond
			if test.allowed {
				timeout = 2 * time.Second
			}
			ctx, cancel := context.WithTimeout(context.Background(), timeout)
			defer cancel()
			conn, err := DialBackend(ctx, listener.Addr().String(), test.path, test.hostname)
			if test.allowed {
				if err != nil {
					t.Fatal(err)
				}
				_ = conn.Close()
			} else if err == nil {
				_ = conn.Close()
				t.Fatal("accepted invalid backend identity")
			}
		})
	}
}
