package main

import (
	"bufio"
	"context"
	"encoding/json"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
	"time"

	"github.com/lightningnetwork/lnd/lnrpc"
)

func doRequest(t *testing.T, client *http.Client, method, url, body string, header map[string]string) (*http.Response, []byte) {
	t.Helper()
	var r io.Reader
	if body != "" {
		r = strings.NewReader(body)
	}
	req, err := http.NewRequest(method, url, r)
	if err != nil {
		t.Fatal(err)
	}
	for k, v := range header {
		req.Header.Set(k, v)
	}
	resp, err := client.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	data, err := io.ReadAll(resp.Body)
	if err != nil {
		t.Fatal(err)
	}
	return resp, data
}

// The JSON follows LND's REST marshaler: proto field names, every field
// emitted, 64-bit integers as strings, bytes as base64, enums by name.
func TestGetInfoUsesLNDJSONShape(t *testing.T) {
	backend := startFakeBackend(t)
	s, client, _ := startSidecar(t, testConfig(t, backend))

	resp, body := doRequest(t, client, "GET", baseURL(s)+"/v1/getinfo", "", nil)

	if resp.StatusCode != 200 {
		t.Fatalf("status %d: %s", resp.StatusCode, body)
	}
	// protojson varies its whitespace on purpose, so compare parsed values.
	var got map[string]any
	if err := json.Unmarshal(body, &got); err != nil {
		t.Fatal(err)
	}
	want := map[string]any{
		"identity_pubkey":       "02aa",
		"block_height":          float64(321),
		"best_header_timestamp": "1700000000",
		"num_active_channels":   float64(0),
		"synced_to_chain":       false,
		"block_hash":            "",
		"graph_cache_status":    "GRAPH_CACHE_STATUS_DISABLED",
	}
	for key, value := range want {
		if got[key] != value {
			t.Errorf("%s = %#v, want %#v", key, got[key], value)
		}
	}
	if uris, ok := got["uris"].([]any); !ok || len(uris) != 0 {
		t.Errorf("uris = %#v, want []", got["uris"])
	}
	if features, ok := got["features"].(map[string]any); !ok || len(features) != 0 {
		t.Errorf("features = %#v, want {}", got["features"])
	}
	if chains, ok := got["chains"].([]any); !ok || len(chains) != 1 || chains[0].(map[string]any)["network"] != "signet" {
		t.Errorf("chains = %#v", got["chains"])
	}
	backend.lastCall(t, "/lnrpc.Lightning/GetInfo")
}

func TestBodyBytesAndEnumsFollowLND(t *testing.T) {
	backend := startFakeBackend(t)
	s, client, _ := startSidecar(t, testConfig(t, backend))

	resp, body := doRequest(t, client, "POST", baseURL(s)+"/v1/invoices", `{"memo":"coffee","value":"1000","r_preimage":"AQID"}`, nil)
	if resp.StatusCode != 200 {
		t.Fatalf("status %d: %s", resp.StatusCode, body)
	}
	// r_hash 0xfbff01 is base64 "+/8B" (standard alphabet, as protojson).
	var added map[string]any
	if err := json.Unmarshal(body, &added); err != nil || added["r_hash"] != "+/8B" || added["add_index"] != "7" {
		t.Fatalf("unexpected AddInvoice JSON: %s", body)
	}
	backend.mu.Lock()
	got := backend.lastAdd
	backend.mu.Unlock()
	if got.Memo != "coffee" || got.Value != 1000 || string(got.RPreimage) != "\x01\x02\x03" {
		t.Fatalf("backend got memo %q value %d preimage %x", got.Memo, got.Value, got.RPreimage)
	}

	resp, body = doRequest(t, client, "GET", baseURL(s)+"/v1/channels", "", nil)
	if resp.StatusCode != 200 {
		t.Fatalf("status %d: %s", resp.StatusCode, body)
	}
	var channels struct {
		Channels []map[string]any `json:"channels"`
	}
	if err := json.Unmarshal(body, &channels); err != nil || len(channels.Channels) != 1 ||
		channels.Channels[0]["chan_id"] != "18446744073709551615" || channels.Channels[0]["commitment_type"] != "ANCHORS" {
		t.Fatalf("unexpected ListChannels JSON: %s", body)
	}
}

func TestQueryParametersBindToTheRequest(t *testing.T) {
	backend := startFakeBackend(t)
	s, client, _ := startSidecar(t, testConfig(t, backend))

	resp, body := doRequest(t, client, "GET", baseURL(s)+"/v1/invoices?pending_only=true&num_max_invoices=5&reversed=true", "", nil)

	if resp.StatusCode != 200 {
		t.Fatalf("status %d: %s", resp.StatusCode, body)
	}
	backend.mu.Lock()
	got := backend.lastList
	backend.mu.Unlock()
	if !got.PendingOnly || got.NumMaxInvoices != 5 || !got.Reversed {
		t.Fatalf("backend got %+v", got)
	}
}

func TestUnimplementedAnswersLikeLND(t *testing.T) {
	backend := startFakeBackend(t)
	s, client, _ := startSidecar(t, testConfig(t, backend))

	resp, body := doRequest(t, client, "GET", baseURL(s)+"/v1/graph/info", "", nil)

	if resp.StatusCode != http.StatusNotImplemented {
		t.Fatalf("status %d: %s", resp.StatusCode, body)
	}
	var e struct {
		Code    int    `json:"code"`
		Message string `json:"message"`
		Details []any  `json:"details"`
	}
	if err := json.Unmarshal(body, &e); err != nil || e.Code != 12 || e.Details == nil {
		t.Fatalf("error body %s (%v)", body, err)
	}
}

func TestUnknownPathAndWrongMethodNeverReachTheBackend(t *testing.T) {
	backend := startFakeBackend(t)
	s, client, _ := startSidecar(t, testConfig(t, backend))

	resp, _ := doRequest(t, client, "GET", baseURL(s)+"/v1/nosuchroute", "", nil)
	if resp.StatusCode != http.StatusNotFound {
		t.Fatalf("unknown path: status %d", resp.StatusCode)
	}
	resp, _ = doRequest(t, client, "DELETE", baseURL(s)+"/v1/getinfo", "", nil)
	if resp.StatusCode == 200 {
		t.Fatal("DELETE /v1/getinfo succeeded")
	}
	if calls := backend.recorded(); len(calls) != 0 {
		t.Fatalf("backend got %d calls", len(calls))
	}
}

// restRule is one REST binding of LND's grpc-gateway service yaml.
type restRule struct {
	method, verb, path string
}

var (
	selectorLine = regexp.MustCompile(`^\s*- selector: ([\w.]+)\s*$`)
	bindingLine  = regexp.MustCompile(`^\s*(?:- )?(get|post|delete|put|patch): "([^"]+)"`)
	pathParam    = regexp.MustCompile(`\{[^}]+\}`)
)

// lndRESTRules reads every REST binding from the pinned LND module's service
// yaml files (the input LND generates its *.pb.gw.go from).
func lndRESTRules(t *testing.T) []restRule {
	t.Helper()
	out, err := exec.Command("go", "list", "-m", "-f", "{{.Dir}}", "github.com/lightningnetwork/lnd").Output()
	if err != nil {
		t.Skipf("cannot locate the lnd module: %v", err)
	}
	dir := strings.TrimSpace(string(out))
	files := []string{
		"lightning.yaml", "stateservice.yaml", "walletunlocker.yaml",
		"routerrpc/router.yaml", "invoicesrpc/invoices.yaml", "walletrpc/walletkit.yaml",
		"signrpc/signer.yaml", "chainrpc/chainnotifier.yaml", "chainrpc/chainkit.yaml",
		"verrpc/verrpc.yaml", "peersrpc/peers.yaml", "autopilotrpc/autopilot.yaml",
		"wtclientrpc/wtclient.yaml", "watchtowerrpc/watchtower.yaml", "neutrinorpc/neutrino.yaml",
	}
	var rules []restRule
	for _, name := range files {
		f, err := os.Open(filepath.Join(dir, "lnrpc", name))
		if err != nil {
			t.Fatal(err)
		}
		selector := ""
		sc := bufio.NewScanner(f)
		for sc.Scan() {
			line := sc.Text()
			if m := selectorLine.FindStringSubmatch(line); m != nil {
				i := strings.LastIndex(m[1], ".")
				selector = "/" + m[1][:i] + "/" + m[1][i+1:]
				continue
			}
			if m := bindingLine.FindStringSubmatch(line); m != nil && selector != "" {
				rules = append(rules, restRule{method: selector, verb: strings.ToUpper(m[1]), path: m[2]})
			}
		}
		f.Close()
	}
	return rules
}

// Every REST route LND registers reaches the matching gRPC method of the
// backend: the generated handlers of all services are in place.
func TestEveryLNDRESTRouteReachesItsMethod(t *testing.T) {
	backend := startFakeBackend(t)
	s, client, _ := startSidecar(t, testConfig(t, backend))
	client.Timeout = 3 * time.Second
	rules := lndRESTRules(t)
	if len(rules) < 150 {
		t.Fatalf("only %d REST rules found", len(rules))
	}
	services := map[string]bool{}
	for _, rule := range rules {
		services[strings.Split(rule.method, "/")[1]] = true
		reached := false
		// Path parameters are numbers, strings or base64 bytes; try value
		// combinations until the gateway accepts the path.
		params := pathParam.FindAllString(rule.path, -1)
		for combo := 0; combo < 1<<len(params) && !reached; combo++ {
			i := 0
			path := pathParam.ReplaceAllStringFunc(rule.path, func(string) string {
				value := "1"
				if combo&(1<<i) != 0 {
					value = "AAAA"
				}
				i++
				return value
			})
			before := len(backend.recorded())
			body := ""
			if rule.verb != "GET" && rule.verb != "DELETE" {
				body = "{}"
			}
			req, err := http.NewRequest(rule.verb, baseURL(s)+path, strings.NewReader(body))
			if err != nil {
				t.Fatal(err)
			}
			ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
			resp, err := client.Do(req.WithContext(ctx))
			if err == nil {
				// Streams of a fake that answers UNIMPLEMENTED end at once.
				_, _ = io.Copy(io.Discard, resp.Body)
				resp.Body.Close()
			}
			cancel()
			for _, c := range backend.recorded()[before:] {
				if c.method == rule.method {
					reached = true
				}
			}
		}
		if !reached {
			t.Errorf("%s %s did not reach %s", rule.verb, rule.path, rule.method)
		}
	}
	t.Logf("%d REST routes of %d services reached their methods", len(rules), len(services))
	if len(services) != len(restServices) {
		t.Errorf("rules cover %d services, the sidecar registers %d", len(services), len(restServices))
	}
}

// The client's macaroon header reaches the backend unchanged as the
// "macaroon" metadata; the sidecar adds no credential of its own.
func TestMacaroonHeaderPassesThroughUnchanged(t *testing.T) {
	backend := startFakeBackend(t)
	s, client, _ := startSidecar(t, testConfig(t, backend))
	const mac = "0201036c6e6402f801030a10"

	doRequest(t, client, "GET", baseURL(s)+"/v1/getinfo", "", map[string]string{"Grpc-Metadata-macaroon": mac})
	got := backend.lastCall(t, "/lnrpc.Lightning/GetInfo").md
	if v := got.Get("macaroon"); len(v) != 1 || v[0] != mac {
		t.Fatalf("macaroon metadata %q", v)
	}

	doRequest(t, client, "GET", baseURL(s)+"/v1/getinfo", "", nil)
	got = backend.lastCall(t, "/lnrpc.Lightning/GetInfo").md
	if v := got.Get("macaroon"); len(v) != 0 {
		t.Fatalf("a call without the header carried macaroon %q", v)
	}
	if v := got.Get("authorization"); len(v) != 0 {
		t.Fatalf("the sidecar added authorization %q", v)
	}
}

// Server streams are newline-delimited JSON objects {"result": ...} over
// plain HTTP, as LND's REST proxy sends them.
func TestServerStreamOverHTTPIsNDJSON(t *testing.T) {
	backend := startFakeBackend(t)
	backend.invoices = []*lnrpc.Invoice{{Memo: "one", AddIndex: 1}, {Memo: "two", AddIndex: 2}}
	s, client, _ := startSidecar(t, testConfig(t, backend))

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	req, _ := http.NewRequestWithContext(ctx, "GET", baseURL(s)+"/v1/invoices/subscribe", nil)
	resp, err := client.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	sc := bufio.NewScanner(resp.Body)
	for _, memo := range []string{"one", "two"} {
		if !sc.Scan() {
			t.Fatalf("stream ended: %v", sc.Err())
		}
		var msg struct {
			Result struct {
				Memo     string `json:"memo"`
				AddIndex string `json:"add_index"`
			} `json:"result"`
		}
		if err := json.Unmarshal(sc.Bytes(), &msg); err != nil || msg.Result.Memo != memo {
			t.Fatalf("line %s (%v)", sc.Bytes(), err)
		}
	}
}

func TestCORSIsOffByDefault(t *testing.T) {
	backend := startFakeBackend(t)
	s, client, _ := startSidecar(t, testConfig(t, backend))

	resp, _ := doRequest(t, client, "GET", baseURL(s)+"/v1/getinfo", "", map[string]string{"Origin": "https://evil.example"})

	if v := resp.Header.Get("Access-Control-Allow-Origin"); v != "" {
		t.Fatalf("CORS header %q without --cors-origin", v)
	}
}

func TestCORSOptIn(t *testing.T) {
	backend := startFakeBackend(t)
	c := testConfig(t, backend)
	c.CORSOrigins = []string{"https://rtl.example"}
	s, client, _ := startSidecar(t, c)

	resp, _ := doRequest(t, client, "OPTIONS", baseURL(s)+"/v1/getinfo", "", map[string]string{"Origin": "https://rtl.example"})
	if resp.StatusCode != 200 || resp.Header.Get("Access-Control-Allow-Origin") != "https://rtl.example" ||
		!strings.Contains(resp.Header.Get("Access-Control-Allow-Headers"), "Grpc-Metadata-Macaroon") {
		t.Fatalf("preflight %d %v", resp.StatusCode, resp.Header)
	}
	resp, _ = doRequest(t, client, "GET", baseURL(s)+"/v1/getinfo", "", map[string]string{"Origin": "https://other.example"})
	if v := resp.Header.Get("Access-Control-Allow-Origin"); v != "" {
		t.Fatalf("origin not in the list got %q", v)
	}
	if len(backend.recorded()) != 1 {
		t.Fatalf("the preflight reached the backend: %d calls", len(backend.recorded()))
	}
}

func TestOversizedBodyIsRefused(t *testing.T) {
	backend := startFakeBackend(t)
	c := testConfig(t, backend)
	c.MaxBodyBytes = 1024
	s, client, _ := startSidecar(t, c)

	resp, _ := doRequest(t, client, "POST", baseURL(s)+"/v1/invoices", `{"memo":"`+strings.Repeat("x", 2048)+`"}`, nil)

	if resp.StatusCode != http.StatusRequestEntityTooLarge {
		t.Fatalf("status %d", resp.StatusCode)
	}
	if len(backend.recorded()) != 0 {
		t.Fatal("an oversized body reached the backend")
	}
}

func TestRequestLogNeverHoldsCredentialsOrQueries(t *testing.T) {
	backend := startFakeBackend(t)
	c := testConfig(t, backend)
	c.LogRequests = true
	s, client, sink := startSidecar(t, c)
	const mac = "0201secretmacaroonhex"

	doRequest(t, client, "GET", baseURL(s)+"/v1/invoices?pending_only=true", "", map[string]string{"Grpc-Metadata-macaroon": mac})

	lines := strings.Join(sink.all(), "\n")
	if !strings.Contains(lines, "GET /v1/invoices -> 200") {
		t.Fatalf("no request line: %s", lines)
	}
	if strings.Contains(lines, mac) || strings.Contains(lines, "pending_only") {
		t.Fatalf("log leaks: %s", lines)
	}
}

func TestBackendDownAnswersUnavailable(t *testing.T) {
	backend := startFakeBackend(t)
	c := testConfig(t, backend)
	backend.server.Stop()
	s, client, _ := startSidecar(t, c)

	resp, body := doRequest(t, client, "GET", baseURL(s)+"/v1/getinfo", "", nil)

	if resp.StatusCode != http.StatusServiceUnavailable {
		t.Fatalf("status %d: %s", resp.StatusCode, body)
	}
}

func TestShutdownEndsOpenStreams(t *testing.T) {
	backend := startFakeBackend(t)
	// The gateway sends the response headers with the first message.
	backend.invoices = []*lnrpc.Invoice{{Memo: "open"}}
	s, client, _ := startSidecar(t, testConfig(t, backend))

	req, _ := http.NewRequest("GET", baseURL(s)+"/v1/invoices/subscribe", nil)
	client.Timeout = 0
	resp, err := client.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	select {
	case <-backend.subscribed:
	case <-time.After(5 * time.Second):
		t.Fatal("stream never reached the backend")
	}

	ctx, cancel := context.WithTimeout(context.Background(), 300*time.Millisecond)
	defer cancel()
	start := time.Now()
	_ = s.Shutdown(ctx)
	if err := <-s.Done(); err != nil {
		t.Fatalf("serve error %v", err)
	}
	_, _ = io.Copy(io.Discard, resp.Body)
	if elapsed := time.Since(start); elapsed > 3*time.Second {
		t.Fatalf("shutdown took %s", elapsed)
	}
	if _, err := client.Get(baseURL(s) + "/v1/getinfo"); err == nil {
		t.Fatal("served after shutdown")
	}
}

func TestVersionFlag(t *testing.T) {
	var out strings.Builder
	if err := run(context.Background(), []string{"--version"}, &out, io.Discard); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), lndAPIVersion) {
		t.Fatalf("version output %q", out.String())
	}
}
