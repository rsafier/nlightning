package main

import (
	"crypto/tls"
	"encoding/json"
	"net/http"
	"strings"
	"testing"
	"time"

	"github.com/gorilla/websocket"
	"github.com/lightningnetwork/lnd/lnrpc"
)

func dialWS(t *testing.T, s *Server, path string, header http.Header) *websocket.Conn {
	t.Helper()
	dialer := websocket.Dialer{
		TLSClientConfig:  &tls.Config{RootCAs: certPool(t, s.CertPath)},
		HandshakeTimeout: 5 * time.Second,
	}
	url := "wss://" + s.Addr().String() + path
	conn, resp, err := dialer.Dial(url, header)
	if err != nil {
		status := 0
		if resp != nil {
			status = resp.StatusCode
		}
		t.Fatalf("dial %s: %v (status %d)", path, err, status)
	}
	t.Cleanup(func() { conn.Close() })
	return conn
}

func readInvoiceMemos(t *testing.T, conn *websocket.Conn, n int) []string {
	t.Helper()
	var memos []string
	for len(memos) < n {
		_ = conn.SetReadDeadline(time.Now().Add(5 * time.Second))
		_, data, err := conn.ReadMessage()
		if err != nil {
			t.Fatalf("read: %v (after %v)", err, memos)
		}
		var msg struct {
			Result *struct {
				Memo string `json:"memo"`
			} `json:"result"`
			Error any `json:"error"`
		}
		if err := json.Unmarshal(data, &msg); err != nil || msg.Result == nil {
			t.Fatalf("message %s (%v)", data, err)
		}
		memos = append(memos, msg.Result.Memo)
	}
	return memos
}

// LND's WebSocket API: a GET upgrade with ?method= naming the REST method,
// the macaroon in the Sec-Websocket-Protocol field ("Grpc-Metadata-Macaroon+
// <hex>", browsers cannot set headers), one request message, then a stream
// of {"result": ...} messages.
func TestWebSocketStreamWithMacaroonInProtocolField(t *testing.T) {
	backend := startFakeBackend(t)
	backend.invoices = []*lnrpc.Invoice{{Memo: "first"}, {Memo: "second"}}
	s, _, _ := startSidecar(t, testConfig(t, backend))
	const mac = "0201abcdef"

	conn := dialWS(t, s, "/v1/invoices/subscribe?method=GET", http.Header{
		"Sec-Websocket-Protocol": {"Grpc-Metadata-Macaroon+" + mac},
	})
	if err := conn.WriteMessage(websocket.TextMessage, []byte(`{"add_index":"0"}`)); err != nil {
		t.Fatal(err)
	}

	memos := readInvoiceMemos(t, conn, 2)
	if strings.Join(memos, ",") != "first,second" {
		t.Fatalf("memos %v", memos)
	}
	got := backend.lastCall(t, "/lnrpc.Lightning/SubscribeInvoices").md
	if v := got.Get("macaroon"); len(v) != 1 || v[0] != mac {
		t.Fatalf("macaroon metadata %q", v)
	}
}

func TestWebSocketStreamWithMacaroonHeader(t *testing.T) {
	backend := startFakeBackend(t)
	backend.invoices = []*lnrpc.Invoice{{Memo: "only"}}
	s, _, _ := startSidecar(t, testConfig(t, backend))
	const mac = "0201feed"

	conn := dialWS(t, s, "/v1/invoices/subscribe?method=GET", http.Header{"Grpc-Metadata-Macaroon": {mac}})
	if err := conn.WriteMessage(websocket.TextMessage, []byte(`{}`)); err != nil {
		t.Fatal(err)
	}

	if memos := readInvoiceMemos(t, conn, 1); memos[0] != "only" {
		t.Fatalf("memos %v", memos)
	}
	if v := backend.lastCall(t, "/lnrpc.Lightning/SubscribeInvoices").md.Get("macaroon"); len(v) != 1 || v[0] != mac {
		t.Fatalf("macaroon metadata %q", v)
	}
}

// A backend error on a stream reaches the client as {"error": ...}.
func TestWebSocketStreamError(t *testing.T) {
	backend := startFakeBackend(t)
	s, _, _ := startSidecar(t, testConfig(t, backend))

	conn := dialWS(t, s, "/v1/channels/subscribe?method=GET", nil)
	if err := conn.WriteMessage(websocket.TextMessage, []byte(`{}`)); err != nil {
		t.Fatal(err)
	}

	_ = conn.SetReadDeadline(time.Now().Add(5 * time.Second))
	_, data, err := conn.ReadMessage()
	if err != nil {
		t.Fatal(err)
	}
	var msg struct {
		Error struct {
			Code int `json:"code"`
		} `json:"error"`
	}
	if err := json.Unmarshal(data, &msg); err != nil || msg.Error.Code != 12 {
		t.Fatalf("message %s (%v)", data, err)
	}
}

// Shutdown also ends WebSocket sessions, which the HTTP server no longer
// tracks after the upgrade.
func TestShutdownEndsWebSocketSessions(t *testing.T) {
	backend := startFakeBackend(t)
	s, _, _ := startSidecar(t, testConfig(t, backend))
	conn := dialWS(t, s, "/v1/invoices/subscribe?method=GET", nil)
	if err := conn.WriteMessage(websocket.TextMessage, []byte(`{}`)); err != nil {
		t.Fatal(err)
	}
	select {
	case <-backend.subscribed:
	case <-time.After(5 * time.Second):
		t.Fatal("stream never reached the backend")
	}

	_ = s.Shutdown(t.Context())

	_ = conn.SetReadDeadline(time.Now().Add(5 * time.Second))
	for {
		if _, _, err := conn.ReadMessage(); err != nil {
			if ne, ok := err.(interface{ Timeout() bool }); ok && ne.Timeout() {
				t.Fatal("the WebSocket stayed open after shutdown")
			}
			return
		}
	}
}
