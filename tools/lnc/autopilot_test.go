package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"net"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/btcsuite/btcd/btcec/v2"
	"github.com/btcsuite/btcd/btcec/v2/ecdsa"
	"github.com/lightningnetwork/lnd/lnrpc"
	ap "github.com/rsafier/nlightning/tools/lnc/internal/autopilotserverrpc"
	"github.com/rsafier/nlightning/tools/lnc/internal/litrpc"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"
	"google.golang.org/protobuf/encoding/protowire"
	"google.golang.org/protobuf/proto"
	"gopkg.in/macaroon.v2"
)

// The AutoFees and AutoOpen features exactly as Lightning Labs' mainnet and
// testnet autopilot servers served them on 2026-10-07 (ListFeatures).
func liveFeatures() *ap.ListFeaturesResponse {
	perm := func(m, entity, action string) *ap.Permissions {
		return &ap.Permissions{Method: m, Operations: []*ap.Operation{{Entity: entity, Action: action}}}
	}
	rule := func(def, min, max string) *ap.Rule {
		return &ap.Rule{Default: []byte(def), MinValue: []byte(min), MaxValue: []byte(max)}
	}
	rateLimit := rule(`{"write_limit":{"iterations":1000,"num_hours":72},"read_limit":{"iterations":1000,"num_hours":72}}`,
		`{"write_limit":{"iterations":1,"num_hours":720},"read_limit":{"iterations":1,"num_hours":720}}`,
		`{"write_limit":{"iterations":1000,"num_hours":24},"read_limit":{"iterations":1000,"num_hours":24}}`)
	peer := rule(`{"peer_deny_list":null}`, `{"peer_deny_list":null}`, `{"peer_deny_list":null}`)
	return &ap.ListFeaturesResponse{Features: map[string]*ap.Feature{
		"AutoFees": {Name: "AutoFees", Description: "Auto select channel fees", DefaultConfig: []byte(`{"version":0}`),
			Rules: map[string]*ap.Rule{
				ruleHistoryLimit: rule(`{"start_date":"0001-01-01T00:00:00Z","duration":5184000000000000}`,
					`{"start_date":"0001-01-01T00:00:00Z","duration":3456000000000000}`,
					`{"start_date":"0001-01-01T00:00:00Z","duration":6912000000000000}`),
				ruleChannelRestrict: rule(`{"channel_deny_list":null}`, `{"channel_deny_list":null}`, `{"channel_deny_list":null}`),
				rulePeerRestrict:    peer,
				ruleRateLimit:       rateLimit,
				ruleChanPolicy: rule(`{"min_base_msat":0,"max_base_msat":10000,"min_rate_ppm":0,"max_rate_ppm":5000,"min_cltv_delta":60,"max_cltv_delta":120,"min_htlc_msat":1,"max_htlc_msat":100000000000}`,
					`{"min_base_msat":0,"max_base_msat":0,"min_rate_ppm":0,"max_rate_ppm":2000,"min_cltv_delta":40,"max_cltv_delta":100,"min_htlc_msat":1,"max_htlc_msat":10000000000}`,
					`{"min_base_msat":1000,"max_base_msat":10000,"min_rate_ppm":1000,"max_rate_ppm":10000,"min_cltv_delta":100,"max_cltv_delta":140,"min_htlc_msat":10000,"max_htlc_msat":100000000000}`),
			},
			PermissionsList: []*ap.Permissions{perm(mForwardingHistory, "offchain", "read"), perm(mUpdateChannelPolicy, "offchain", "write"),
				perm(mListChannels, "offchain", "read"), perm(mFeeReport, "offchain", "read")}},
		"AutoOpen": {Name: "AutoOpen", Description: "Automatically open channels", PrivacyFlags: 129,
			DefaultConfig: []byte(`{"version":0,"time_preference":"medium"}`),
			Rules: map[string]*ap.Rule{
				"channel-constraint": rule(`{}`, `{}`, `{}`), "on-chain-budget": rule(`{}`, `{}`, `{}`),
				rulePeerRestrict: peer, ruleRateLimit: rateLimit},
			PermissionsList: []*ap.Permissions{perm("/lnrpc.Lightning/GetInfo", "info", "read"),
				perm("/lnrpc.Lightning/BatchOpenChannel", "onchain", "write"), perm(mListChannels, "offchain", "read")}},
	}}
}

type fakeAutopilotServer struct {
	mu        sync.Mutex
	key       *btcec.PrivateKey
	registers []*ap.RegisterSessionRequest
	revoked   [][]byte
	activated [][]byte
	reject    bool
	minLit    *ap.Version
}

func (f *fakeAutopilotServer) Address() string { return "autopilot.test:12010" }
func (f *fakeAutopilotServer) Terms(context.Context) (*ap.TermsResponse, error) {
	min := f.minLit
	if min == nil {
		min = &ap.Version{Minor: 8}
	}
	return &ap.TermsResponse{MinRequiredVersion: min}, nil
}
func (f *fakeAutopilotServer) ListFeatures(context.Context) (*ap.ListFeaturesResponse, error) {
	return liveFeatures(), nil
}
func (f *fakeAutopilotServer) RegisterSession(_ context.Context, r *ap.RegisterSessionRequest) (*ap.RegisterSessionResponse, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.registers = append(f.registers, r)
	return &ap.RegisterSessionResponse{InitiatorPubKey: f.key.PubKey().SerializeCompressed()}, nil
}
func (f *fakeAutopilotServer) ActivateSession(_ context.Context, k []byte) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.activated = append(f.activated, k)
	if f.reject {
		return errors.New("rpc error: the client has been rejected")
	}
	return nil
}
func (f *fakeAutopilotServer) RevokeSession(_ context.Context, k []byte) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.revoked = append(f.revoked, k)
	return nil
}

func testCeiling(t *testing.T) *autopilotCeiling {
	t.Helper()
	m, err := macaroon.New([]byte("ceiling root key"), []byte("ceiling id"), "lnd", macaroon.LatestVersion)
	if err != nil {
		t.Fatal(err)
	}
	raw, _ := m.MarshalBinary()
	return &autopilotCeiling{RootKeyID: 42, Macaroon: hex.EncodeToString(raw), Methods: ceilingMethods()}
}

func testAutopilotService(t *testing.T, server autopilotServer, ceiling *autopilotCeiling) *autopilotService {
	t.Helper()
	store, err := openStore(filepath.Join(t.TempDir(), "state"))
	if err != nil {
		t.Fatal(err)
	}
	actions, err := openActionLog(store.Dir)
	if err != nil {
		t.Fatal(err)
	}
	var mu sync.Mutex
	return &autopilotService{store: store, server: server, ceiling: ceiling, relay: "mailbox.terminal.lightning.today:443",
		actions: actions, privacy: newPrivacyStore(store.Dir), now: time.Now,
		lock: func() func() { mu.Lock(); return mu.Unlock }}
}

func mustMarshal(t *testing.T, m proto.Message) []byte {
	t.Helper()
	b, err := proto.Marshal(m)
	if err != nil {
		t.Fatal(err)
	}
	return b
}

// What Terminal sends to enable AutoFees: its own policy bounds and a channel
// and a peer it keeps AutoFees off.
func terminalAutoFeesRequest(expiry time.Time, deniedChan uint64, deniedPeer string) *litrpc.AddAutopilotSessionRequest {
	return &litrpc.AddAutopilotSessionRequest{Label: "AutoFees - 2026-10-07", ExpiryTimestampSeconds: uint64(expiry.Unix()),
		MailboxServerAddr: "mailbox.terminal.lightning.today:443",
		Features: map[string]*litrpc.FeatureConfig{"AutoFees": {Config: []byte(`{"version":0}`), Rules: &litrpc.RulesMap{Rules: map[string]*litrpc.RuleValue{
			ruleChanPolicy: {Value: &litrpc.RuleValue_ChanPolicyBounds{ChanPolicyBounds: &litrpc.ChannelPolicyBounds{
				MinBaseMsat: 0, MaxBaseMsat: 5000, MinRatePpm: 1, MaxRatePpm: 2500, MinCltvDelta: 80, MaxCltvDelta: 120,
				MinHtlcMsat: 1, MaxHtlcMsat: 100000000000}}},
			ruleChannelRestrict: {Value: &litrpc.RuleValue_ChannelRestrict{ChannelRestrict: &litrpc.ChannelRestrict{ChannelIds: []uint64{deniedChan}}}},
			rulePeerRestrict:    {Value: &litrpc.RuleValue_PeerRestrict{PeerRestrict: &litrpc.PeerRestrict{PeerIds: []string{deniedPeer}}}},
		}}}}}
}

func addTestSession(t *testing.T, svc *autopilotService, req *litrpc.AddAutopilotSessionRequest) *litrpc.Session {
	t.Helper()
	out, err := svc.addSession(context.Background(), CallInfo{}, mustMarshal(t, req))
	if err != nil {
		t.Fatal(err)
	}
	resp := &litrpc.AddAutopilotSessionResponse{}
	if err := proto.Unmarshal(out, resp); err != nil {
		t.Fatal(err)
	}
	return resp.Session
}

const (
	peerA = "02aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
	peerB = "03bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
	txA   = "1111111111111111111111111111111111111111111111111111111111111111"
	txB   = "2222222222222222222222222222222222222222222222222222222222222222"
)

func TestServerRuleJSONParsesAndDefaultsAreSane(t *testing.T) {
	now := time.Now()
	for name, r := range liveFeatures().Features["AutoFees"].Rules {
		def, err := parseRuleJSON(name, r.Default)
		if err != nil {
			t.Fatal(name, err)
		}
		lo, _ := parseRuleJSON(name, r.MinValue)
		hi, _ := parseRuleJSON(name, r.MaxValue)
		if err := verifySane(def, lo, hi, now); err != nil {
			t.Fatal(name, "default not within bounds:", err)
		}
		if back, err := ruleFromProto(name, ruleToProto(def)); err != nil || ruleToProto(back) == nil {
			t.Fatal(name, "proto round trip", err)
		}
	}
	if h, _ := parseRuleJSON(ruleHistoryLimit, liveFeatures().Features["AutoFees"].Rules[ruleHistoryLimit].Default); h.(*historyLimit).Duration != 60*24*time.Hour {
		t.Fatal("history limit default is 60 days")
	}
	spec := liveFeatures().Features["AutoFees"].Rules[ruleChanPolicy]
	lo, _ := parseRuleJSON(ruleChanPolicy, spec.MinValue)
	hi, _ := parseRuleJSON(ruleChanPolicy, spec.MaxValue)
	tooHigh := &chanPolicyBounds{MaxBaseMsat: 5000, MaxRatePPM: 20000, MinCLTVDelta: 80, MaxCLTVDelta: 120, MinHtlcMsat: 1, MaxHtlcMsat: 100000000000}
	if verifySane(tooHigh, lo, hi, now) == nil {
		t.Fatal("max rate above the server maximum accepted")
	}
	if _, err := parseRuleJSON("channel-constraint", []byte(`{}`)); err == nil {
		t.Fatal("unknown rule accepted")
	}
	if _, err := ruleFromProto(rulePeerRestrict, &litrpc.RuleValue{Value: &litrpc.RuleValue_PeerRestrict{PeerRestrict: &litrpc.PeerRestrict{PeerIds: []string{"not a key"}}}}); err == nil {
		t.Fatal("malformed peer key accepted")
	}
}

func TestListFeaturesMarksWhatTheBridgeCannotRun(t *testing.T) {
	svc := testAutopilotService(t, &fakeAutopilotServer{key: mustKey(t)}, testCeiling(t))
	out, err := svc.listFeatures(context.Background(), CallInfo{}, nil)
	if err != nil {
		t.Fatal(err)
	}
	resp := &litrpc.ListAutopilotFeaturesResponse{}
	if err := proto.Unmarshal(out, resp); err != nil {
		t.Fatal(err)
	}
	fees, open := resp.Features["AutoFees"], resp.Features["AutoOpen"]
	if fees == nil || fees.RequiresUpgrade || len(fees.PermissionsList) != 4 || fees.DefaultConfig != `{"version":0}` {
		t.Fatalf("AutoFees %v", fees)
	}
	for name, r := range fees.Rules {
		if !r.Known || r.Defaults == nil || r.MinValue == nil || r.MaxValue == nil {
			t.Fatal("AutoFees rule", name, r)
		}
	}
	if open == nil || !open.RequiresUpgrade || open.Rules["channel-constraint"].Known || open.PrivacyFlags != 129 {
		t.Fatalf("AutoOpen %v", open)
	}
	// Without an autopilot server the list is empty, so the page loads.
	out, err = testAutopilotService(t, nil, nil).listFeatures(context.Background(), CallInfo{}, nil)
	if err != nil || len(out) != 0 {
		t.Fatal("no-server fallback", err, out)
	}
}

func mustKey(t *testing.T) *btcec.PrivateKey {
	k, err := btcec.NewPrivateKey()
	if err != nil {
		t.Fatal(err)
	}
	return k
}

func TestAddAutopilotSessionRegistersScopedSession(t *testing.T) {
	server := &fakeAutopilotServer{key: mustKey(t)}
	ceiling := testCeiling(t)
	svc := testAutopilotService(t, server, ceiling)
	expiry := time.Now().Add(30 * 24 * time.Hour).Truncate(time.Second)
	got := addTestSession(t, svc, terminalAutoFeesRequest(expiry, 123456789, peerA))

	if got.SessionType != litrpc.SessionType_TYPE_AUTOPILOT || got.SessionState != litrpc.SessionState_STATE_CREATED ||
		len(got.Id) != 4 || string(got.GroupId) != string(got.Id) || got.ExpiryTimestampSeconds != uint64(expiry.Unix()) {
		t.Fatalf("session %v", got)
	}
	if hex.EncodeToString(got.RemotePublicKey) != hex.EncodeToString(server.key.PubKey().SerializeCompressed()) {
		t.Fatal("remote key is not the autopilot's")
	}
	rules := got.AutopilotFeatureInfo["AutoFees"].Rules
	if rules[ruleChannelRestrict].GetChannelRestrict().ChannelIds[0] != 123456789 || rules[rulePeerRestrict].GetPeerRestrict().PeerIds[0] != peerA {
		t.Fatal("listed rules must show the real values")
	}
	if rules[ruleRateLimit] == nil || rules[ruleHistoryLimit] == nil || rules[ruleChanPolicy].GetChanPolicyBounds().MaxRatePpm != 2500 {
		t.Fatal("defaults and choices", rules)
	}
	if len(server.registers) != 1 {
		t.Fatal("not registered")
	}
	reg := server.registers[0]
	if string(reg.ResponderPubKey) != string(got.LocalPublicKey) || reg.MailboxAddr != svc.relay || reg.DevServer ||
		reg.LitVersion.Minor != 17 || reg.LndVersion.Minor != 21 || string(reg.FeatureConfigs["AutoFees"]) != `{"version":0}` {
		t.Fatalf("registration %v", reg)
	}
	sessions, _ := svc.store.list()
	if len(sessions) != 1 {
		t.Fatal(len(sessions))
	}
	s := sessions[0]
	if !s.isAutopilot() || s.Entropy != "" || s.RootKeyID != ceiling.RootKeyID || strings.Join(s.AllowedMethods, " ") != strings.Join(ceilingMethods(), " ") {
		t.Fatalf("stored %+v", s)
	}
	// The backend macaroon is the ceiling plus the expiry only; the client's
	// adds litd's rules (pseudonymized) and privacy caveats.
	var backendMac, clientMac macaroon.Macaroon
	b, _ := hex.DecodeString(s.Macaroon)
	c, _ := hex.DecodeString(s.ClientMacaroon)
	if backendMac.UnmarshalBinary(b) != nil || clientMac.UnmarshalBinary(c) != nil {
		t.Fatal("macaroons")
	}
	if len(backendMac.Caveats()) != 1 || !strings.HasPrefix(string(backendMac.Caveats()[0].Id), "time-before ") {
		t.Fatal("backend caveats", backendMac.Caveats())
	}
	cav := clientMac.Caveats()
	if len(cav) != 3 || !strings.HasPrefix(string(cav[1].Id), rulesCaveatPrefix) || string(cav[2].Id) != privacyCaveat {
		t.Fatal("client caveats")
	}
	rulesCaveat := string(cav[1].Id)
	if strings.Contains(rulesCaveat, "123456789") || strings.Contains(rulesCaveat, peerA) {
		t.Fatal("real restriction values sent to the autopilot server")
	}
	var parsed struct {
		FeatureRules map[string]map[string]string `json:"feature_rules"`
	}
	if err := json.Unmarshal([]byte(strings.TrimPrefix(rulesCaveat, rulesCaveatPrefix)), &parsed); err != nil || len(parsed.FeatureRules["AutoFees"]) != 5 {
		t.Fatal("rules caveat", err, parsed)
	}
	var cr channelRestrict
	_ = json.Unmarshal([]byte(parsed.FeatureRules["AutoFees"][ruleChannelRestrict]), &cr)
	_ = svc.privacy.do(s.GroupID, func(m *privacyMap) error {
		if real, err := m.revealUint64(cr.DenyList[0]); err != nil || real != 123456789 {
			t.Fatal("pseudonym does not map back", err, real)
		}
		return nil
	})

	// List and revoke.
	out, err := svc.listSessions(context.Background(), CallInfo{}, nil)
	if err != nil {
		t.Fatal(err)
	}
	list := &litrpc.ListAutopilotSessionsResponse{}
	if proto.Unmarshal(out, list) != nil || len(list.Sessions) != 1 {
		t.Fatal("list")
	}
	if _, err := svc.revokeSession(context.Background(), CallInfo{}, mustMarshal(t, &litrpc.RevokeAutopilotSessionRequest{LocalPublicKey: got.LocalPublicKey})); err != nil {
		t.Fatal(err)
	}
	s, _ = svc.store.load(s.ID)
	if !s.Revoked || !s.Autopilot.ServerRevoked || len(server.revoked) != 1 || s.RevokedAt.IsZero() {
		t.Fatal("revoke")
	}
	if _, err := svc.revokeSession(context.Background(), CallInfo{}, mustMarshal(t, &litrpc.RevokeAutopilotSessionRequest{LocalPublicKey: server.key.PubKey().SerializeCompressed()})); status.Code(err) != codes.NotFound {
		t.Fatal("unknown key", err)
	}

	// A new session linked to the revoked group proves ownership with the
	// group's first transport key and shares its privacy map.
	req := terminalAutoFeesRequest(expiry, 123456789, peerA)
	req.LinkedGroupId = got.GroupId
	linked := addTestSession(t, svc, req)
	if string(linked.GroupId) != string(got.GroupId) || string(linked.Id) == string(got.Id) {
		t.Fatal("linked group")
	}
	reg = server.registers[1]
	sig, err := ecdsa.ParseDERSignature(reg.GroupResponderSig)
	digest := sha256.Sum256(reg.ResponderPubKey)
	first, _ := btcec.ParsePubKey(reg.GroupResponderKey)
	if err != nil || string(reg.GroupResponderKey) != string(got.LocalPublicKey) || !sig.Verify(digest[:], first) {
		t.Fatal("link signature")
	}
	if _, err := svc.addSession(context.Background(), CallInfo{}, mustMarshal(t, req)); status.Code(err) != codes.FailedPrecondition {
		t.Fatal("linking to an active group", err)
	}
}

func TestAddAutopilotSessionRefusals(t *testing.T) {
	server := &fakeAutopilotServer{key: mustKey(t)}
	expiry := time.Now().Add(time.Hour)
	cases := map[string]struct {
		svc  *autopilotService
		edit func(*litrpc.AddAutopilotSessionRequest)
		code codes.Code
	}{
		"no server":  {testAutopilotService(t, nil, testCeiling(t)), nil, codes.FailedPrecondition},
		"no ceiling": {testAutopilotService(t, server, nil), nil, codes.FailedPrecondition},
		"AutoOpen": {nil, func(r *litrpc.AddAutopilotSessionRequest) {
			r.Features = map[string]*litrpc.FeatureConfig{"AutoOpen": {}}
		}, codes.Unimplemented},
		"unknown":       {nil, func(r *litrpc.AddAutopilotSessionRequest) { r.Features = map[string]*litrpc.FeatureConfig{"Nope": {}} }, codes.InvalidArgument},
		"no privacy":    {nil, func(r *litrpc.AddAutopilotSessionRequest) { r.NoPrivacyMapper = true }, codes.InvalidArgument},
		"dev server":    {nil, func(r *litrpc.AddAutopilotSessionRequest) { r.DevServer = true }, codes.InvalidArgument},
		"other mailbox": {nil, func(r *litrpc.AddAutopilotSessionRequest) { r.MailboxServerAddr = "evil.example:443" }, codes.InvalidArgument},
		"past expiry":   {nil, func(r *litrpc.AddAutopilotSessionRequest) { r.ExpiryTimestampSeconds = 1 }, codes.InvalidArgument},
		"unknown flags": {nil, func(r *litrpc.AddAutopilotSessionRequest) { r.PrivacyFlags, r.PrivacyFlagsSet = 1<<20, true }, codes.InvalidArgument},
		"session rules": {nil, func(r *litrpc.AddAutopilotSessionRequest) { r.SessionRules = r.Features["AutoFees"].Rules }, codes.InvalidArgument},
		"rate too high": {nil, func(r *litrpc.AddAutopilotSessionRequest) {
			r.Features["AutoFees"].Rules.Rules[ruleChanPolicy].GetChanPolicyBounds().MaxRatePpm = 50000
		}, codes.InvalidArgument},
		"foreign rule": {nil, func(r *litrpc.AddAutopilotSessionRequest) {
			r.Features["AutoFees"].Rules.Rules["channel-constraint"] = &litrpc.RuleValue{}
		}, codes.InvalidArgument},
		"pubkey config": {nil, func(r *litrpc.AddAutopilotSessionRequest) {
			r.Features["AutoFees"].Config = []byte(`{"node":"` + peerB + `"}`)
		}, codes.InvalidArgument},
	}
	for name, c := range cases {
		svc := c.svc
		if svc == nil {
			svc = testAutopilotService(t, server, testCeiling(t))
		}
		req := terminalAutoFeesRequest(expiry, 1, peerA)
		if c.edit != nil {
			c.edit(req)
		}
		if _, err := svc.addSession(context.Background(), CallInfo{}, mustMarshal(t, req)); status.Code(err) != c.code {
			t.Fatal(name, err)
		}
		if sessions, _ := svc.store.list(); len(sessions) != 0 {
			t.Fatal(name, "left a session behind")
		}
	}
	tooNew := testAutopilotService(t, &fakeAutopilotServer{key: mustKey(t), minLit: &ap.Version{Major: 1}}, testCeiling(t))
	if _, err := tooNew.addSession(context.Background(), CallInfo{}, mustMarshal(t, terminalAutoFeesRequest(expiry, 1, peerA))); status.Code(err) != codes.FailedPrecondition {
		t.Fatal("server requiring a newer litd", err)
	}
}

func TestConfigObfuscationMapsListsOfKeysAndChannelPoints(t *testing.T) {
	m := &privacyMap{RealToPseudo: map[string]string{}, PseudoToReal: map[string]string{}}
	out, err := obfuscateConfig(m, 0, []byte(`{"version":0,"pinned_peers":["`+peerA+`"],"chans":["`+txA+`:1"]}`))
	if err != nil || strings.Contains(string(out), peerA) || strings.Contains(string(out), txA) || !strings.Contains(string(out), `"version":0`) {
		t.Fatal(string(out), err)
	}
	clear, _ := obfuscateConfig(m, privacyFlags(1<<privClearPubkeys), []byte(`{"pinned_peers":["`+peerA+`"]}`))
	if !strings.Contains(string(clear), peerA) {
		t.Fatal("clear pubkeys flag ignored")
	}
}

func TestAutopilotAuthenticatorAcceptsOnlyTheSessionMacaroonWithMetaCaveats(t *testing.T) {
	base, _ := macaroon.New([]byte("root"), []byte("id"), "lnd", macaroon.LatestVersion)
	_ = base.AddFirstPartyCaveat([]byte(rulesCaveatPrefix + "{}"))
	raw, _ := base.MarshalBinary()
	now := time.Now()
	auth, err := autopilotAuthenticator(hex.EncodeToString(raw), func() time.Time { return now })
	if err != nil {
		t.Fatal(err)
	}
	present := func(caveats ...string) []byte {
		m := base.Clone()
		for _, c := range caveats {
			_ = m.AddFirstPartyCaveat([]byte(c))
		}
		b, _ := m.MarshalBinary()
		return b
	}
	if call, err := auth(present()); err != nil || call.Meta != nil {
		t.Fatal("exact credential", err)
	}
	meta := metaCaveatPrefix + `{"actor_name":"Autopilot","feature":"AutoFees","trigger":"t","intent":"i","structured_json_data":"{}"}`
	call, err := auth(present(meta, "time-before "+now.Add(time.Minute).Format(time.RFC3339)))
	if err != nil || call.Meta == nil || call.Meta.ActorName != "Autopilot" || call.Meta.Feature != "AutoFees" {
		t.Fatal("meta caveat", err, call)
	}
	for name, p := range map[string][]byte{
		"unknown caveat": present("ipaddr 1.2.3.4"),
		"two metas":      present(meta, meta),
		"expired":        present("time-before " + now.Add(-time.Minute).Format(time.RFC3339)),
		"bad meta":       present(metaCaveatPrefix + "{"),
	} {
		if _, err := auth(p); err == nil {
			t.Fatal(name, "accepted")
		}
	}
	// The macaroon the client's derives from (without the rules caveat) and
	// a forged signature are refused.
	parent, _ := macaroon.New([]byte("root"), []byte("id"), "lnd", macaroon.LatestVersion)
	parentRaw, _ := parent.MarshalBinary()
	if _, err := auth(parentRaw); err == nil {
		t.Fatal("caveat removal accepted")
	}
	forged, _ := macaroon.New([]byte("other root"), []byte("id"), "lnd", macaroon.LatestVersion)
	_ = forged.AddFirstPartyCaveat([]byte(rulesCaveatPrefix + "{}"))
	_ = forged.AddFirstPartyCaveat([]byte(meta))
	forgedRaw, _ := forged.MarshalBinary()
	if _, err := auth(forgedRaw); err == nil {
		t.Fatal("forged signature accepted")
	}
}

// fakeNode is a backend that records every call and answers the AutoFees
// methods with real-looking data.
type fakeNode struct {
	mu       sync.Mutex
	calls    []string
	macaroon []string
	policies []*lnrpc.PolicyUpdateRequest
	fwdReqs  []*lnrpc.ForwardingHistoryRequest
	fail     error
}

func (n *fakeNode) handler(_ interface{}, stream grpc.ServerStream) error {
	method, _ := grpc.MethodFromServerStream(stream)
	md, _ := metadata.FromIncomingContext(stream.Context())
	var req []byte
	if err := stream.RecvMsg(&req); err != nil {
		return err
	}
	n.mu.Lock()
	n.calls = append(n.calls, method)
	n.macaroon = append(n.macaroon, strings.Join(md.Get("macaroon"), ","))
	fail := n.fail
	n.mu.Unlock()
	if fail != nil {
		return fail
	}
	var resp proto.Message
	switch method {
	case mListChannels:
		resp = &lnrpc.ListChannelsResponse{Channels: []*lnrpc.Channel{
			{Active: true, RemotePubkey: peerA, ChannelPoint: txA + ":0", ChanId: 111, Capacity: 1000000, LocalBalance: 400000, RemoteBalance: 590000,
				CloseAddress: "bc1qsecret", PeerAlias: "alice", PendingHtlcs: []*lnrpc.HTLC{{Amount: 5, HashLock: []byte{1}}}},
			{Active: true, RemotePubkey: peerB, ChannelPoint: txB + ":1", ChanId: 222, Capacity: 2000000, LocalBalance: 1000000},
		}}
	case mFeeReport:
		resp = &lnrpc.FeeReportResponse{ChannelFees: []*lnrpc.ChannelFeeReport{{ChanId: 111, ChannelPoint: txA + ":0", BaseFeeMsat: 1000, FeePerMil: 100}}, DayFeeSum: 7}
	case mForwardingHistory:
		r := &lnrpc.ForwardingHistoryRequest{}
		_ = proto.Unmarshal(req, r)
		n.mu.Lock()
		n.fwdReqs = append(n.fwdReqs, r)
		n.mu.Unlock()
		resp = &lnrpc.ForwardingHistoryResponse{ForwardingEvents: []*lnrpc.ForwardingEvent{{ChanIdIn: 111, ChanIdOut: 222,
			AmtOutMsat: 1000000, FeeMsat: 1000, TimestampNs: uint64(time.Now().UnixNano()), PeerAliasIn: "alice"}}, LastOffsetIndex: 1}
	case mUpdateChannelPolicy:
		r := &lnrpc.PolicyUpdateRequest{}
		_ = proto.Unmarshal(req, r)
		n.mu.Lock()
		n.policies = append(n.policies, r)
		n.mu.Unlock()
		resp = &lnrpc.PolicyUpdateResponse{FailedUpdates: []*lnrpc.FailedUpdate{{Outpoint: &lnrpc.OutPoint{TxidStr: txA, OutputIndex: 0},
			Reason: lnrpc.UpdateFailure_UPDATE_FAILURE_INTERNAL_ERR, UpdateError: "edge " + txA + ":0 failed"}}}
	default:
		return status.Error(codes.Unimplemented, "unexpected")
	}
	b, _ := proto.Marshal(resp)
	// An unknown field on the response must never reach the autopilot.
	b = protowire.AppendTag(b, 999, protowire.BytesType)
	b = protowire.AppendString(b, "secret")
	return stream.SendMsg(&b)
}

type firewallHarness struct {
	t       *testing.T
	node    *fakeNode
	svc     *autopilotService
	session *Session
	conn    *grpc.ClientConn
	client  []byte
}

func newFirewallHarness(t *testing.T, writeLimit uint32) *firewallHarness {
	t.Helper()
	server := &fakeAutopilotServer{key: mustKey(t)}
	svc := testAutopilotService(t, server, testCeiling(t))
	req := terminalAutoFeesRequest(time.Now().Add(24*time.Hour), 222, "03cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")
	if writeLimit != 0 {
		req.Features["AutoFees"].Rules.Rules[ruleRateLimit] = &litrpc.RuleValue{Value: &litrpc.RuleValue_RateLimit{RateLimit: &litrpc.RateLimit{
			ReadLimit: &litrpc.Rate{Iterations: 1000, NumHours: 24}, WriteLimit: &litrpc.Rate{Iterations: writeLimit, NumHours: 720}}}}
	}
	addTestSession(t, svc, req)
	sessions, _ := svc.store.list()
	s := sessions[0]
	node := &fakeNode{}
	backend := localConn(t, grpc.NewServer(grpc.ForceServerCodec(opaqueCodec{}), grpc.UnknownServiceHandler(node.handler)))
	auth, err := autopilotAuthenticator(s.ClientMacaroon, time.Now)
	if err != nil {
		t.Fatal(err)
	}
	proxy, err := NewSessionProxyWith(backend, s.Macaroon, ProxyConfig{
		Local: newFirewall(s, backend, svc.actions, svc.privacy).handlers(), LocalOnly: true, Authenticate: auth})
	if err != nil {
		t.Fatal(err)
	}
	client, _ := hex.DecodeString(s.ClientMacaroon)
	return &firewallHarness{t: t, node: node, svc: svc, session: s, conn: localConn(t, proxy), client: client}
}

// call makes a call as the autopilot server does: the session macaroon with a
// meta caveat naming the feature.
func (h *firewallHarness) call(method, feature string, req, resp proto.Message) error {
	var m macaroon.Macaroon
	_ = m.UnmarshalBinary(h.client)
	if feature != "" {
		_ = m.AddFirstPartyCaveat([]byte(metaCaveatPrefix + `{"actor_name":"Autopilot","feature":"` + feature + `","trigger":"fees","intent":"set fees","structured_json_data":"{\"beforeFeePpm\":100}"}`))
	}
	raw, _ := m.MarshalBinary()
	ctx := metadata.AppendToOutgoingContext(context.Background(), "macaroon", hex.EncodeToString(raw))
	reqBytes := mustMarshal(h.t, req)
	var out []byte
	if err := h.conn.Invoke(ctx, method, &reqBytes, &out, grpc.ForceCodec(opaqueCodec{})); err != nil {
		return err
	}
	if len(protowireUnknown(out)) != 0 {
		h.t.Fatal("unknown field leaked to the autopilot")
	}
	return proto.Unmarshal(out, resp)
}

func protowireUnknown(b []byte) []byte {
	m := &lnrpc.ListChannelsResponse{}
	_ = proto.Unmarshal(b, m)
	for len(b) > 0 {
		num, typ, n := protowire.ConsumeTag(b)
		if n < 0 {
			return nil
		}
		if num == 999 {
			return []byte{1}
		}
		b = b[n:]
		m := protowire.ConsumeFieldValue(num, typ, b)
		if m < 0 {
			return nil
		}
		b = b[m:]
	}
	return nil
}

func TestFirewallMapsPrivacyAndUsesOnlyTheScopedMacaroon(t *testing.T) {
	h := newFirewallHarness(t, 0)
	first := &lnrpc.ListChannelsResponse{}
	if err := h.call(mListChannels, "AutoFees", &lnrpc.ListChannelsRequest{}, first); err != nil {
		t.Fatal(err)
	}
	c := first.Channels[0]
	if c.RemotePubkey == peerA || len(c.RemotePubkey) != len(peerA) || strings.HasPrefix(c.ChannelPoint, txA) || c.ChanId == 111 {
		t.Fatalf("identifiers not mapped: %v", c)
	}
	if c.CloseAddress != "" || c.PeerAlias != "" || len(c.PendingHtlcs) != 1 || c.PendingHtlcs[0].Amount != 0 {
		t.Fatal("unmapped fields passed", c)
	}
	if c.LocalBalance < 380000 || c.LocalBalance > 420000 || c.RemoteBalance != c.Capacity-c.LocalBalance {
		t.Fatal("balance not fuzzed within 5 %", c.LocalBalance)
	}
	second := &lnrpc.ListChannelsResponse{}
	_ = h.call(mListChannels, "AutoFees", &lnrpc.ListChannelsRequest{}, second)
	if second.Channels[0].ChannelPoint != c.ChannelPoint || second.Channels[0].ChanId != c.ChanId || second.Channels[0].RemotePubkey != c.RemotePubkey {
		t.Fatal("pseudonyms not stable")
	}
	report := &lnrpc.FeeReportResponse{}
	if err := h.call(mFeeReport, "AutoFees", &lnrpc.FeeReportRequest{}, report); err != nil || report.ChannelFees[0].ChannelPoint != c.ChannelPoint || report.ChannelFees[0].ChanId != c.ChanId {
		t.Fatal("fee report", err, report)
	}
	fwd := &lnrpc.ForwardingHistoryResponse{}
	start := uint64(time.Now().Add(-30 * 24 * time.Hour).Unix())
	if err := h.call(mForwardingHistory, "AutoFees", &lnrpc.ForwardingHistoryRequest{StartTime: start}, fwd); err != nil {
		t.Fatal(err)
	}
	if e := fwd.ForwardingEvents[0]; e.ChanIdIn != c.ChanId || e.PeerAliasIn != "" || e.AmtInMsat != e.AmtOutMsat+e.FeeMsat {
		t.Fatal("forwarding history", e)
	}
	// A policy update on the pseudo channel point reaches the node as the real one.
	cp := strings.Split(c.ChannelPoint, ":")
	policy := &lnrpc.PolicyUpdateRequest{Scope: &lnrpc.PolicyUpdateRequest_ChanPoint{ChanPoint: &lnrpc.ChannelPoint{
		FundingTxid: &lnrpc.ChannelPoint_FundingTxidStr{FundingTxidStr: cp[0]}, OutputIndex: atoiU32(cp[1])}},
		BaseFeeMsat: 1000, FeeRate: 0.0005, TimeLockDelta: 80}
	upd := &lnrpc.PolicyUpdateResponse{}
	if err := h.call(mUpdateChannelPolicy, "AutoFees", policy, upd); err != nil {
		t.Fatal(err)
	}
	h.node.mu.Lock()
	sent := h.node.policies[len(h.node.policies)-1]
	macs := append([]string(nil), h.node.macaroon...)
	h.node.mu.Unlock()
	if sent.GetChanPoint().GetFundingTxidStr() != txA || sent.GetChanPoint().OutputIndex != 0 {
		t.Fatal("node got", sent.GetChanPoint())
	}
	if f := upd.FailedUpdates[0]; f.Outpoint.TxidStr == txA || strings.Contains(f.UpdateError, txA) {
		t.Fatal("failed update leaks the real outpoint", f)
	}
	for _, m := range macs {
		if m != h.session.Macaroon {
			t.Fatal("node called without the session's scoped macaroon")
		}
	}
	// The action log has the real parameters Terminal shows.
	out, err := h.svc.listActions(context.Background(), CallInfo{}, mustMarshal(t, &litrpc.ListActionsRequest{ActorName: "Autopilot",
		FeatureName: "AutoFees", MethodName: mUpdateChannelPolicy, MaxNumActions: 1000, EndTimestamp: uint64(time.Now().Add(time.Minute).Unix()),
		State: litrpc.ActionState_STATE_DONE, Reversed: true}))
	if err != nil {
		t.Fatal(err)
	}
	actions := &litrpc.ListActionsResponse{}
	_ = proto.Unmarshal(out, actions)
	if len(actions.Actions) != 1 || !strings.Contains(actions.Actions[0].RpcParamsJson, txA) ||
		!strings.Contains(actions.Actions[0].RpcParamsJson, `"fee_rate"`) || actions.Actions[0].StructuredJsonData != `{"beforeFeePpm":100}` ||
		actions.Actions[0].Trigger != "fees" || hex.EncodeToString(actions.Actions[0].SessionId) != h.session.LitID {
		t.Fatalf("actions %v", actions.Actions)
	}
}

func atoiU32(s string) uint32 {
	var v uint32
	for _, c := range s {
		v = v*10 + uint32(c-'0')
	}
	return v
}

func TestFirewallEnforcesRules(t *testing.T) {
	h := newFirewallHarness(t, 0)
	chans := &lnrpc.ListChannelsResponse{}
	if err := h.call(mListChannels, "AutoFees", &lnrpc.ListChannelsRequest{}, chans); err != nil {
		t.Fatal(err)
	}
	pseudo := func(i int) *lnrpc.PolicyUpdateRequest_ChanPoint {
		cp := strings.Split(chans.Channels[i].ChannelPoint, ":")
		return &lnrpc.PolicyUpdateRequest_ChanPoint{ChanPoint: &lnrpc.ChannelPoint{
			FundingTxid: &lnrpc.ChannelPoint_FundingTxidStr{FundingTxidStr: cp[0]}, OutputIndex: atoiU32(cp[1])}}
	}
	ok := func() *lnrpc.PolicyUpdateRequest {
		return &lnrpc.PolicyUpdateRequest{Scope: pseudo(0), BaseFeeMsat: 1000, FeeRatePpm: 500, TimeLockDelta: 80}
	}
	refused := map[string]*lnrpc.PolicyUpdateRequest{}
	add := func(name string, edit func(*lnrpc.PolicyUpdateRequest)) {
		r := ok()
		edit(r)
		refused[name] = r
	}
	add("fee rate above bound", func(r *lnrpc.PolicyUpdateRequest) { r.FeeRatePpm = 2501 })
	add("base fee above bound", func(r *lnrpc.PolicyUpdateRequest) { r.BaseFeeMsat = 5001 })
	add("negative base fee", func(r *lnrpc.PolicyUpdateRequest) { r.BaseFeeMsat = -1 })
	add("zero fee rate under minimum", func(r *lnrpc.PolicyUpdateRequest) { r.FeeRatePpm = 0 })
	add("cltv below bound", func(r *lnrpc.PolicyUpdateRequest) { r.TimeLockDelta = 79 })
	add("max htlc above bound", func(r *lnrpc.PolicyUpdateRequest) { r.MaxHtlcMsat = 100000000001 })
	add("inbound discount too large", func(r *lnrpc.PolicyUpdateRequest) { r.InboundFee = &lnrpc.InboundFee{FeeRatePpm: -2501} })
	add("global", func(r *lnrpc.PolicyUpdateRequest) { r.Scope = &lnrpc.PolicyUpdateRequest_Global{Global: true} })
	add("create missing edge", func(r *lnrpc.PolicyUpdateRequest) { r.CreateMissingEdge = true })
	add("restricted channel", func(r *lnrpc.PolicyUpdateRequest) { r.Scope = pseudo(1) })
	add("unknown channel point", func(r *lnrpc.PolicyUpdateRequest) {
		r.Scope = &lnrpc.PolicyUpdateRequest_ChanPoint{ChanPoint: &lnrpc.ChannelPoint{FundingTxid: &lnrpc.ChannelPoint_FundingTxidStr{FundingTxidStr: txA}}}
	})
	for name, r := range refused {
		err := h.call(mUpdateChannelPolicy, "AutoFees", r, &lnrpc.PolicyUpdateResponse{})
		want := codes.PermissionDenied
		if name == "unknown channel point" {
			want = codes.InvalidArgument // not a pseudonym this group ever saw
		}
		if status.Code(err) != want {
			t.Fatal(name, err)
		}
	}
	h.node.mu.Lock()
	if len(h.node.policies) != 0 {
		t.Fatal("a refused update reached the node")
	}
	h.node.mu.Unlock()
	// History limit: AutoFees may look back at most 60 days.
	if err := h.call(mForwardingHistory, "AutoFees", &lnrpc.ForwardingHistoryRequest{StartTime: 0}, &lnrpc.ForwardingHistoryResponse{}); status.Code(err) != codes.PermissionDenied {
		t.Fatal("unbounded history", err)
	}
	// Calls without meta information, for another feature, or for a method
	// the session lacks never reach the node.
	if err := h.call(mFeeReport, "", &lnrpc.FeeReportRequest{}, &lnrpc.FeeReportResponse{}); status.Code(err) != codes.PermissionDenied {
		t.Fatal("missing meta", err)
	}
	if err := h.call(mFeeReport, "AutoOpen", &lnrpc.FeeReportRequest{}, &lnrpc.FeeReportResponse{}); status.Code(err) != codes.PermissionDenied {
		t.Fatal("other feature", err)
	}
	for _, m := range []string{"/lnrpc.Lightning/SendCoins", "/lnrpc.Lightning/GetInfo", "/routerrpc.Router/SendPaymentV2", litAddAutopilotSession} {
		if err := h.call(m, "AutoFees", &lnrpc.FeeReportRequest{}, &lnrpc.FeeReportResponse{}); status.Code(err) != codes.PermissionDenied {
			t.Fatal(m, err)
		}
	}
	// Request fields the bridge cannot check are refused.
	unknownReq := protowire.AppendTag(mustMarshal(t, &lnrpc.FeeReportRequest{}), 77, protowire.VarintType)
	unknownReq = protowire.AppendVarint(unknownReq, 1)
	var m macaroon.Macaroon
	_ = m.UnmarshalBinary(h.client)
	_ = m.AddFirstPartyCaveat([]byte(metaCaveatPrefix + `{"feature":"AutoFees"}`))
	raw, _ := m.MarshalBinary()
	var out []byte
	if err := h.conn.Invoke(metadata.AppendToOutgoingContext(context.Background(), "macaroon", hex.EncodeToString(raw)), mFeeReport,
		&unknownReq, &out, grpc.ForceCodec(opaqueCodec{})); status.Code(err) != codes.InvalidArgument {
		t.Fatal("unknown request field", err)
	}
	if err := h.call(mUpdateChannelPolicy, "AutoFees", ok(), &lnrpc.PolicyUpdateResponse{}); err != nil {
		t.Fatal("update within the rules", err)
	}
	// The backend's message is not passed on (it may name real channels).
	h.node.mu.Lock()
	h.node.fail = status.Error(codes.NotFound, "channel "+txA+":0 not found")
	h.node.mu.Unlock()
	err := h.call(mFeeReport, "AutoFees", &lnrpc.FeeReportRequest{}, &lnrpc.FeeReportResponse{})
	if status.Code(err) != codes.NotFound || strings.Contains(err.Error(), txA) {
		t.Fatal("backend error", err)
	}
	// The presented node-side macaroon alone is not an autopilot credential.
	backendRaw, _ := hex.DecodeString(h.session.Macaroon)
	if err := h.conn.Invoke(metadata.AppendToOutgoingContext(context.Background(), "macaroon", hex.EncodeToString(backendRaw)), mFeeReport,
		&unknownReq, &out, grpc.ForceCodec(opaqueCodec{})); status.Code(err) != codes.Unauthenticated {
		t.Fatal("backend macaroon accepted from the client", err)
	}
}

func TestFirewallRateLimitCountsTheGroupsWrites(t *testing.T) {
	h := newFirewallHarness(t, 1)
	chans := &lnrpc.ListChannelsResponse{}
	if err := h.call(mListChannels, "AutoFees", &lnrpc.ListChannelsRequest{}, chans); err != nil {
		t.Fatal(err)
	}
	cp := strings.Split(chans.Channels[0].ChannelPoint, ":")
	req := &lnrpc.PolicyUpdateRequest{Scope: &lnrpc.PolicyUpdateRequest_ChanPoint{ChanPoint: &lnrpc.ChannelPoint{
		FundingTxid: &lnrpc.ChannelPoint_FundingTxidStr{FundingTxidStr: cp[0]}, OutputIndex: atoiU32(cp[1])}},
		BaseFeeMsat: 1000, FeeRatePpm: 500, TimeLockDelta: 80}
	if err := h.call(mUpdateChannelPolicy, "AutoFees", req, &lnrpc.PolicyUpdateResponse{}); err != nil {
		t.Fatal(err)
	}
	if err := h.call(mUpdateChannelPolicy, "AutoFees", req, &lnrpc.PolicyUpdateResponse{}); status.Code(err) != codes.ResourceExhausted {
		t.Fatal("second write in the window", err)
	}
	// Reads have their own budget.
	if err := h.call(mFeeReport, "AutoFees", &lnrpc.FeeReportRequest{}, &lnrpc.FeeReportResponse{}); err != nil {
		t.Fatal(err)
	}
}

func netListenLoopback() (net.Listener, error) { return net.Listen("tcp", "127.0.0.1:0") }

func TestActionLogListSemanticsAndPersistence(t *testing.T) {
	dir := filepath.Join(t.TempDir(), "s")
	if _, err := openStore(dir); err != nil {
		t.Fatal(err)
	}
	log, err := openActionLog(dir)
	if err != nil {
		t.Fatal(err)
	}
	base := time.Unix(1700000000, 0)
	clock := base
	log.now = func() time.Time { return clock }
	for i := 0; i < 5; i++ {
		clock = base.Add(time.Duration(i) * time.Hour)
		feature := "AutoFees"
		if i == 4 {
			feature = "Other"
		}
		idx, err := log.add(action{SessionID: "aabbccdd", GroupID: "aabbccdd", ActorName: "Autopilot", FeatureName: feature, Method: mUpdateChannelPolicy})
		if err != nil {
			t.Fatal(err)
		}
		if i != 2 {
			_ = log.finish(idx, litrpc.ActionState_STATE_DONE, "")
		}
	}
	list := func(r *litrpc.ListActionsRequest) *litrpc.ListActionsResponse {
		resp, err := log.list(r)
		if err != nil {
			t.Fatal(err)
		}
		return resp
	}
	all := list(&litrpc.ListActionsRequest{FeatureName: "AutoFees", State: litrpc.ActionState_STATE_DONE, Reversed: true, CountTotal: true})
	if len(all.Actions) != 3 || all.TotalCount != 3 || all.Actions[0].Timestamp != uint64(base.Add(3*time.Hour).Unix()) || all.LastIndexOffset != 1 {
		t.Fatalf("reversed %v", all)
	}
	page := list(&litrpc.ListActionsRequest{FeatureName: "AutoFees", IndexOffset: 1, MaxNumActions: 1})
	if len(page.Actions) != 1 || page.Actions[0].Timestamp != uint64(base.Add(time.Hour).Unix()) || page.LastIndexOffset != 2 {
		t.Fatalf("page %v", page)
	}
	window := list(&litrpc.ListActionsRequest{StartTimestamp: uint64(base.Add(time.Hour).Unix()), EndTimestamp: uint64(base.Add(2 * time.Hour).Unix())})
	if len(window.Actions) != 2 {
		t.Fatalf("window %v", window)
	}
	if len(list(&litrpc.ListActionsRequest{GroupId: []byte{1, 2, 3, 4}}).Actions) != 0 || len(list(&litrpc.ListActionsRequest{SessionId: []byte{0xaa, 0xbb, 0xcc, 0xdd}}).Actions) != 5 {
		t.Fatal("session/group filter")
	}
	if _, err := log.list(&litrpc.ListActionsRequest{SessionId: []byte{1}}); err == nil {
		t.Fatal("bad session ID accepted")
	}
	// Reopen: the log persists (0600) and the cut-off call is an error.
	info, _ := os.Stat(filepath.Join(dir, "actions.json"))
	if info.Mode().Perm() != 0600 {
		t.Fatal(info.Mode())
	}
	reopened, err := openActionLog(dir)
	if err != nil {
		t.Fatal(err)
	}
	errs, _ := reopened.list(&litrpc.ListActionsRequest{State: litrpc.ActionState_STATE_ERROR})
	if len(errs.Actions) != 1 || errs.Actions[0].ErrorReason == "" {
		t.Fatal("pending action after restart", errs)
	}
	if idx, _ := reopened.add(action{Method: mFeeReport}); idx != 6 {
		t.Fatal("index continues", idx)
	}
}

func TestPrivacyMapPersistsAndRevealsOnlyKnownPseudonyms(t *testing.T) {
	dir := filepath.Join(t.TempDir(), "s")
	if _, err := openStore(dir); err != nil {
		t.Fatal(err)
	}
	store := newPrivacyStore(dir)
	var pk string
	var id uint64
	var cp string
	if err := store.do("01020304", func(m *privacyMap) error {
		var err error
		if pk, err = m.hideString(peerA); err != nil {
			return err
		}
		if id, err = m.hideUint64(111); err != nil {
			return err
		}
		cp, err = m.hideChanPointStr(txA + ":3")
		return err
	}); err != nil {
		t.Fatal(err)
	}
	// A failed mapping adds nothing.
	_ = store.do("01020304", func(m *privacyMap) error { _, _ = m.hideString(peerB); return errors.New("boom") })
	again := newPrivacyStore(dir)
	_ = again.do("01020304", func(m *privacyMap) error {
		if p, _ := m.hideString(peerA); p != pk {
			t.Fatal("pubkey pseudonym changed")
		}
		if v, err := m.revealUint64(id); err != nil || v != 111 {
			t.Fatal("chan id", v, err)
		}
		parts := strings.Split(cp, ":")
		if tx, idx, err := m.revealChanPoint(parts[0], atoiU32(parts[1])); err != nil || tx != txA || idx != 3 {
			t.Fatal("chan point", tx, idx, err)
		}
		if _, err := m.real(peerB); err == nil || len(m.RealToPseudo) != 3 {
			t.Fatal("failed mapping persisted")
		}
		return nil
	})
	if err := store.do("../x", func(*privacyMap) error { return nil }); err == nil {
		t.Fatal("bad group accepted")
	}
}

func TestKeepAliveRevokesRejectedAndReportsExpired(t *testing.T) {
	server := &fakeAutopilotServer{key: mustKey(t)}
	svc := testAutopilotService(t, server, testCeiling(t))
	addTestSession(t, svc, terminalAutoFeesRequest(time.Now().Add(time.Hour), 1, peerA))
	svc.keepAlive(context.Background())
	if len(server.activated) != 1 {
		t.Fatal("not activated")
	}
	server.reject = true
	svc.keepAlive(context.Background())
	sessions, _ := svc.store.list()
	if !sessions[0].Revoked {
		t.Fatal("rejected session still active")
	}
	svc.keepAlive(context.Background())
	svc.keepAlive(context.Background())
	sessions, _ = svc.store.list()
	if len(server.revoked) != 1 || !sessions[0].Autopilot.ServerRevoked {
		t.Fatal("server told", len(server.revoked))
	}
}

func TestRevokeOfAutopilotSessionKeepsTheSharedRootKey(t *testing.T) {
	server := &fakeAutopilotServer{key: mustKey(t)}
	svc := testAutopilotService(t, server, testCeiling(t))
	addTestSession(t, svc, terminalAutoFeesRequest(time.Now().Add(time.Hour), 1, peerA))
	sessions, _ := svc.store.list()
	// No admin macaroon or backend is reachable; revoke must still succeed
	// locally and never ask the node to delete the shared root key.
	err := run(context.Background(), "revoke", []string{"--state-dir", svc.store.Dir, "--tls-cert", "/nonexistent",
		"--admin-macaroon", "/nonexistent", "--backend", "127.0.0.1:1", "--id", sessions[0].ID})
	if err != nil {
		t.Fatal(err)
	}
	s, _ := svc.store.load(sessions[0].ID)
	if !s.Revoked || s.RootKeyDeleted || s.Macaroon == "" {
		t.Fatal("revoke", s.Revoked, s.RootKeyDeleted)
	}
}

func TestAutopilotServerClientSpeaksTheServerProtocol(t *testing.T) {
	key := mustKey(t)
	server := grpc.NewServer(grpc.UnknownServiceHandler(func(_ interface{}, stream grpc.ServerStream) error {
		method, _ := grpc.MethodFromServerStream(stream)
		switch method {
		case "/autopilotserverrpc.Autopilot/ListFeatures":
			if err := stream.RecvMsg(&ap.ListFeaturesRequest{}); err != nil {
				return err
			}
			return stream.SendMsg(liveFeatures())
		case "/autopilotserverrpc.Autopilot/RegisterSession":
			req := &ap.RegisterSessionRequest{}
			if err := stream.RecvMsg(req); err != nil {
				return err
			}
			if req.MailboxAddr != "relay:443" {
				return status.Error(codes.InvalidArgument, "mailbox")
			}
			return stream.SendMsg(&ap.RegisterSessionResponse{InitiatorPubKey: key.PubKey().SerializeCompressed()})
		}
		return status.Error(codes.Unimplemented, method)
	}))
	listener, err := netListenLoopback()
	if err != nil {
		t.Fatal(err)
	}
	go func() { _ = server.Serve(listener) }()
	t.Cleanup(server.Stop)
	client, err := newAutopilotServerClient(listener.Addr().String(), "", true)
	if err != nil {
		t.Fatal(err)
	}
	features, err := client.ListFeatures(context.Background())
	if err != nil || features.Features["AutoFees"].Rules[ruleChanPolicy] == nil {
		t.Fatal(err)
	}
	reg, err := client.RegisterSession(context.Background(), &ap.RegisterSessionRequest{MailboxAddr: "relay:443"})
	if err != nil || string(reg.InitiatorPubKey) != string(key.PubKey().SerializeCompressed()) {
		t.Fatal(err)
	}
	if _, err := newAutopilotServerClient("example.com:12010", "", true); err == nil {
		t.Fatal("plain text to a remote host allowed")
	}
	if resolveAutopilotServer("mainnet") != autopilotMainnetServer || resolveAutopilotServer("testnet") != autopilotTestnetServer {
		t.Fatal("aliases")
	}
}
