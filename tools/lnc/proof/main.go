// Command proof exercises the upstream LNC transport against a real regtest node.
// It is test tooling, not an alternative production client or public endpoint.
package main

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"net/http"
	"os"
	"strings"
	"time"

	"github.com/btcsuite/btcd/btcec/v2"
	"github.com/lightninglabs/lightning-node-connect/mailbox"
	"github.com/lightningnetwork/lnd/keychain"
	"github.com/lightningnetwork/lnd/lnrpc"
	"github.com/lightningnetwork/lnd/lnrpc/routerrpc"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials"
	"google.golang.org/grpc/status"
)

type identity struct {
	Private string `json:"private"`
	Remote  string `json:"remote"`
	Phrase  string `json:"phrase"`
}

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
func run() error {
	relay := flag.String("relay", "", "local relay address")
	cert := flag.String("relay-cert", "", "relay trust certificate")
	state := flag.String("state", "", "persistent test client identity")
	phrase := flag.String("phrase", "", "pairing phrase for first connection")
	mode := flag.String("mode", "info", "info, readonly, receive, pay, revoked")
	output := flag.String("invoice-output", "", "write invoice payment request after subscription is ready")
	payment := flag.String("payment-request", "", "invoice to pay")
	protocol := flag.String("transport", "grpc", "grpc or websocket")
	flag.Parse()
	id := identity{Phrase: *phrase}
	if data, err := os.ReadFile(*state); err == nil {
		if err = json.Unmarshal(data, &id); err != nil {
			return err
		}
	} else if !errors.Is(err, os.ErrNotExist) {
		return err
	}
	if id.Private == "" {
		key, err := btcec.NewPrivateKey()
		if err != nil {
			return err
		}
		id.Private = hex.EncodeToString(key.Serialize())
	}
	save := func() error {
		data, err := json.Marshal(id)
		if err != nil {
			return err
		}
		return os.WriteFile(*state, data, 0600)
	}
	if err := save(); err != nil {
		return err
	}
	keyBytes, err := hex.DecodeString(id.Private)
	if err != nil {
		return err
	}
	key, _ := btcec.PrivKeyFromBytes(keyBytes)
	var remote *btcec.PublicKey
	if id.Remote != "" {
		raw, err := hex.DecodeString(id.Remote)
		if err != nil {
			return err
		}
		remote, err = btcec.ParsePubKey(raw)
		if err != nil {
			return err
		}
	}
	words := strings.Fields(id.Phrase)
	if len(words) != mailbox.NumPassphraseWords {
		return fmt.Errorf("invalid pairing phrase length")
	}
	var mnemonic [mailbox.NumPassphraseWords]string
	copy(mnemonic[:], words)
	entropy := mailbox.PassphraseMnemonicToEntropy(mnemonic)
	connData := mailbox.NewConnData(&keychain.PrivKeyECDH{PrivKey: key}, remote, entropy[:], nil, func(pub *btcec.PublicKey) error {
		id.Remote = hex.EncodeToString(pub.SerializeCompressed())
		return save()
	}, func(data []byte) error {
		// The stock WASM client (Lightning Terminal) accepts only litd's form.
		if parts := strings.Split(string(data), ": "); len(parts) != 2 || parts[0] != "Macaroon" {
			return fmt.Errorf("authdata does not contain a macaroon")
		}
		return nil
	})
	roots := x509.NewCertPool()
	pem, err := os.ReadFile(*cert)
	if err != nil {
		return err
	}
	if !roots.AppendCertsFromPEM(pem) {
		return fmt.Errorf("invalid relay certificate")
	}
	duration := 90 * time.Second
	if *mode == "revoked" {
		duration = 8 * time.Second
	}
	ctx, cancel := context.WithTimeout(context.Background(), duration)
	defer cancel()
	var transport *mailbox.Client
	tlsConfig := &tls.Config{RootCAs: roots, MinVersion: tls.VersionTLS12}
	switch *protocol {
	case "grpc":
		transport, err = mailbox.NewGrpcClient(ctx, *relay, connData, grpc.WithTransportCredentials(credentials.NewTLS(tlsConfig)))
	case "websocket":
		http.DefaultTransport = &http.Transport{TLSClientConfig: tlsConfig}
		transport, err = mailbox.NewWebsocketsClient(ctx, *relay, connData)
	default:
		return fmt.Errorf("unknown transport")
	}

	if err != nil {
		return err
	}
	noise := mailbox.NewNoiseGrpcConn(connData)
	conn, err := grpc.DialContext(ctx, *relay, grpc.WithContextDialer(transport.Dial), grpc.WithTransportCredentials(noise), grpc.WithPerRPCCredentials(noise), grpc.WithBlock())
	if *mode == "revoked" {
		if err == nil {
			defer conn.Close()
			_, err = lnrpc.NewLightningClient(conn).GetInfo(ctx, &lnrpc.GetInfoRequest{})
		}
		if err == nil {
			return fmt.Errorf("revoked session unexpectedly answered")
		}
		fmt.Println("LNC_REVOKED_OK")
		return nil
	}
	if err != nil {
		return err
	}
	defer conn.Close()
	client := lnrpc.NewLightningClient(conn)
	switch *mode {
	case "info":
		info, err := client.GetInfo(ctx, &lnrpc.GetInfoRequest{})
		if err != nil {
			return err
		}
		if len(info.IdentityPubkey) != 66 {
			return fmt.Errorf("invalid node identity")
		}
		fmt.Println("LNC_INFO_OK " + info.IdentityPubkey)
	case "readonly":
		if _, err := client.GetInfo(ctx, &lnrpc.GetInfoRequest{}); err != nil {
			return err
		}
		_, err := client.AddInvoice(ctx, &lnrpc.Invoice{Value: 1000})
		if status.Code(err) != codes.PermissionDenied {
			return fmt.Errorf("read-only write status: %v", err)
		}
		fmt.Println("LNC_READONLY_OK")
	case "receive":
		// Explicit add-index replay establishes stream readiness without assuming
		// the backend sends headers before its first invoice event.
		if _, err := client.AddInvoice(ctx, &lnrpc.Invoice{Value: 1, Memo: "LNC stream cursor"}); err != nil {
			return err
		}
		invoice, err := client.AddInvoice(ctx, &lnrpc.Invoice{Value: 1000, Memo: "LNC real regtest"})
		if err != nil {
			return err
		}
		stream, err := client.SubscribeInvoices(ctx, &lnrpc.InvoiceSubscription{AddIndex: invoice.AddIndex - 1})
		if err != nil {
			return err
		}
		first, err := stream.Recv()
		if err != nil {
			return err
		}
		if hex.EncodeToString(first.RHash) != hex.EncodeToString(invoice.RHash) {
			return fmt.Errorf("invoice replay mismatch")
		}
		if err = os.WriteFile(*output, []byte(invoice.PaymentRequest), 0600); err != nil {
			return err
		}
		for {
			update, err := stream.Recv()
			if err != nil {
				return err
			}
			if hex.EncodeToString(update.RHash) == hex.EncodeToString(invoice.RHash) && update.State == lnrpc.Invoice_SETTLED {
				fmt.Println("LNC_INVOICE_STREAM_OK")
				break
			}
		}

	case "watch":
		stream, err := routerrpc.NewRouterClient(conn).SubscribeHtlcEvents(ctx, &routerrpc.SubscribeHtlcEventsRequest{})
		if err != nil {
			return err
		}
		first, err := stream.Recv()
		if err != nil {
			return err
		}
		if first.GetSubscribedEvent() == nil {
			return fmt.Errorf("expected HTLC subscribed event")
		}
		if err = os.WriteFile(*output, []byte("ready"), 0600); err != nil {
			return err
		}
		_, err = stream.Recv()
		if err == nil || status.Code(err) == codes.DeadlineExceeded || ctx.Err() != nil {
			return fmt.Errorf("active revoked stream did not terminate promptly: %v", err)
		}
		if !errors.Is(err, io.EOF) && status.Code(err) != codes.Unavailable && status.Code(err) != codes.Canceled && status.Code(err) != codes.Unauthenticated && status.Code(err) != codes.PermissionDenied {
			return fmt.Errorf("unexpected revoked stream status: %v", err)
		}
		fmt.Println("LNC_ACTIVE_REVOKED_OK")
	case "pay":
		stream, err := routerrpc.NewRouterClient(conn).SendPaymentV2(ctx, &routerrpc.SendPaymentRequest{PaymentRequest: *payment, TimeoutSeconds: 30, FeeLimitSat: 100})
		if err != nil {
			return err
		}
		for {
			p, err := stream.Recv()
			if err != nil {
				return err
			}
			if p.Status == lnrpc.Payment_SUCCEEDED {
				fmt.Println("LNC_PAYMENT_STREAM_OK")
				break
			}
			if p.Status == lnrpc.Payment_FAILED {
				return fmt.Errorf("payment failed: %v", p.FailureReason)
			}
		}
	default:
		return fmt.Errorf("unknown mode")
	}
	return nil
}
