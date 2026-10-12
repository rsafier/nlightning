package main

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/btcsuite/btcd/btcec/v2"
	"github.com/btcsuite/btcd/btcec/v2/ecdsa"
	ap "github.com/rsafier/nlightning/tools/lnc/internal/autopilotserverrpc"
	"github.com/rsafier/nlightning/tools/lnc/internal/litrpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
	"google.golang.org/protobuf/proto"
	"gopkg.in/macaroon.v2"
)

// featureState is one subscribed feature of an autopilot session.
type featureState struct {
	// Rules hold the real values the bridge enforces.
	Rules ruleSet `json:"rules"`
	// Config is the feature configuration as Terminal sent it (real values).
	Config string `json:"config,omitempty"`
	// Permissions are the node methods the feature may call.
	Permissions []string `json:"permissions"`
}

// autopilotInfo is what an autopilot session adds to a Session.
type autopilotInfo struct {
	Server       string                   `json:"server"`
	MailboxAddr  string                   `json:"mailbox_addr"`
	Features     map[string]*featureState `json:"features"`
	Privacy      bool                     `json:"privacy"`
	PrivacyFlags uint64                   `json:"privacy_flags"`
	// Caveats are litd's firewall caveats on the client macaroon (the rules
	// with pseudonymized values, and the privacy caveat).
	Caveats       []string `json:"caveats"`
	ServerRevoked bool     `json:"server_revoked,omitempty"`
}

// autopilotCeiling is the backend macaroon every autopilot session derives its
// own from (autopilot-init bakes it once with the admin macaroon): it grants
// exactly the methods the firewall implements, so no autopilot session can
// reach anything else on the node even if the bridge had a bug.
type autopilotCeiling struct {
	RootKeyID uint64   `json:"root_key_id"`
	Macaroon  string   `json:"macaroon"`
	Methods   []string `json:"methods"`
}

func autopilotCeilingPath(dir string) string { return filepath.Join(dir, "autopilot.json") }

func ceilingMethods() []string {
	out := make([]string, 0, len(firewallMethods))
	for m := range firewallMethods {
		out = append(out, m)
	}
	sort.Strings(out)
	return out
}

func loadAutopilotCeiling(dir string) (*autopilotCeiling, error) {
	data, err := readPrivateFile(autopilotCeilingPath(dir))
	if err != nil {
		return nil, err
	}
	var c autopilotCeiling
	if err := json.Unmarshal(data, &c); err != nil {
		return nil, fmt.Errorf("autopilot.json: %w", err)
	}
	for _, m := range c.Methods {
		if !firewallMethods[m] {
			return nil, fmt.Errorf("autopilot.json grants %s, which the firewall does not implement", m)
		}
	}
	if _, err := hex.DecodeString(c.Macaroon); err != nil || c.Macaroon == "" {
		return nil, errors.New("autopilot.json holds no macaroon")
	}
	return &c, nil
}

// addCaveats returns the hex macaroon with first-party caveats appended.
func addCaveats(macHex string, caveats ...string) (string, error) {
	raw, err := hex.DecodeString(macHex)
	if err != nil {
		return "", err
	}
	var m macaroon.Macaroon
	if err := m.UnmarshalBinary(raw); err != nil {
		return "", err
	}
	for _, c := range caveats {
		if err := m.AddFirstPartyCaveat([]byte(c)); err != nil {
			return "", err
		}
	}
	out, err := m.MarshalBinary()
	if err != nil {
		return "", err
	}
	return hex.EncodeToString(out), nil
}

// autopilotService is the bridge's litd emulation: litrpc.Autopilot and
// litrpc.Firewall.ListActions for the person's own sessions.
type autopilotService struct {
	store   *Store
	server  autopilotServer // nil: no autopilot server configured
	ceiling *autopilotCeiling
	relay   string
	actions *actionLog
	privacy *privacyStore
	// lock serializes session changes against the serve loop (bridge.mu);
	// addMu serializes AddAutopilotSession calls.
	lock  func() func()
	addMu sync.Mutex
	now   func() time.Time
}

func (a *autopilotService) checkReady() error {
	if a.server == nil {
		return status.Error(codes.FailedPrecondition, "autopilot is not enabled on this bridge (serve --autopilot-server)")
	}
	if a.ceiling == nil {
		return status.Error(codes.FailedPrecondition, "autopilot is not initialized (run nltg-lnc autopilot-init)")
	}
	return nil
}

// featureSupport says whether the bridge can run a server feature: every rule
// known and every method one the firewall implements and the ceiling grants.
func (a *autopilotService) featureSupport(f *ap.Feature) (known map[string]bool, ok bool) {
	known = map[string]bool{}
	ok = true
	for name := range f.Rules {
		known[name] = knownRules[name]
		ok = ok && knownRules[name]
	}
	if checkPrivacyFlags(f.PrivacyFlags) != nil {
		ok = false
	}
	for _, p := range f.PermissionsList {
		if !firewallMethods[p.Method] || (a.ceiling != nil && !contains(a.ceiling.Methods, p.Method)) {
			ok = false
		}
	}
	return known, ok
}

func ruleValuesProto(name string, r *ap.Rule, known bool) *litrpc.RuleValues {
	out := &litrpc.RuleValues{Known: known}
	if !known {
		return out
	}
	conv := func(raw []byte) *litrpc.RuleValue {
		v, err := parseRuleJSON(name, raw)
		if err != nil {
			return nil
		}
		return ruleToProto(v)
	}
	out.Defaults, out.MinValue, out.MaxValue = conv(r.Default), conv(r.MinValue), conv(r.MaxValue)
	return out
}

// ListAutopilotFeatures returns the server's features as litd does, with
// requires_upgrade set for a feature the bridge cannot run. Without an
// autopilot server the list is empty.
func (a *autopilotService) listFeatures(ctx context.Context, _ CallInfo, request []byte) ([]byte, error) {
	if err := proto.Unmarshal(request, &litrpc.ListAutopilotFeaturesRequest{}); err != nil {
		return nil, status.Error(codes.InvalidArgument, "malformed request")
	}
	resp := &litrpc.ListAutopilotFeaturesResponse{Features: map[string]*litrpc.Feature{}}
	if a.server == nil {
		return proto.Marshal(resp)
	}
	features, err := a.server.ListFeatures(ctx)
	if err != nil {
		return nil, status.Errorf(codes.Unavailable, "autopilot server: %v", status.Convert(err).Message())
	}
	for key, f := range features.Features {
		known, ok := a.featureSupport(f)
		rules := map[string]*litrpc.RuleValues{}
		for name, r := range f.Rules {
			rules[name] = ruleValuesProto(name, r, known[name])
		}
		perms := make([]*litrpc.Permissions, 0, len(f.PermissionsList))
		for _, p := range f.PermissionsList {
			ops := make([]*litrpc.MacaroonPermission, 0, len(p.Operations))
			for _, o := range p.Operations {
				ops = append(ops, &litrpc.MacaroonPermission{Entity: o.Entity, Action: o.Action})
			}
			perms = append(perms, &litrpc.Permissions{Method: p.Method, Operations: ops})
		}
		resp.Features[key] = &litrpc.Feature{Name: f.Name, Description: f.Description, Rules: rules,
			PermissionsList: perms, RequiresUpgrade: !ok, DefaultConfig: string(f.DefaultConfig), PrivacyFlags: f.PrivacyFlags}
	}
	return proto.Marshal(resp)
}

var hexPubkey = regexp.MustCompile(`^(02|03)[0-9a-fA-F]{64}$`)

// obfuscateConfig replaces node keys and channel points in a feature
// configuration with pseudonyms, as litd's ObfuscateConfig does for lists of
// strings. Other strings that look like node keys or channel points are
// refused rather than sent in clear.
func obfuscateConfig(m *privacyMap, flags privacyFlags, config []byte) ([]byte, error) {
	if len(config) == 0 {
		return nil, nil
	}
	var cfg map[string]any
	if err := json.Unmarshal(config, &cfg); err != nil {
		return nil, fmt.Errorf("feature config: %w", err)
	}
	sensitive := func(s string) bool {
		_, _, err := decodeChanPoint(strings.TrimSpace(s))
		return err == nil || hexPubkey.MatchString(strings.TrimSpace(s))
	}
	for k, v := range cfg {
		switch val := v.(type) {
		case string:
			if sensitive(val) {
				return nil, fmt.Errorf("feature config value %s must be a list to be mapped", k)
			}
		case []any:
			for i, item := range val {
				s, ok := item.(string)
				if !ok {
					continue
				}
				s = strings.TrimSpace(s)
				var err error
				switch {
				case hexPubkey.MatchString(s):
					if !flags.has(privClearPubkeys) {
						s, err = m.hideString(strings.ToLower(s))
					}
				case sensitive(s):
					if !flags.has(privClearChanIDs) {
						s, err = m.hideChanPointStr(s)
					}
				}
				if err != nil {
					return nil, err
				}
				val[i] = s
			}
		case map[string]any:
			return nil, fmt.Errorf("feature config value %s: nested objects are not supported", k)
		}
	}
	return json.Marshal(cfg)
}

// pseudoRules returns the rule values the autopilot server sees (litd's
// RealToPseudo): channel IDs and peer keys in deny lists pseudonymized.
func pseudoRules(m *privacyMap, flags privacyFlags, rules ruleSet) (map[string]string, error) {
	out := map[string]string{}
	for _, r := range rules.named() {
		v := r.value
		switch x := v.(type) {
		case *channelRestrict:
			if !flags.has(privClearChanIDs) {
				ids := make([]uint64, len(x.DenyList))
				for i, id := range x.DenyList {
					p, err := m.hideUint64(id)
					if err != nil {
						return nil, err
					}
					ids[i] = p
				}
				v = &channelRestrict{DenyList: ids}
			}
		case *peerRestrict:
			if !flags.has(privClearPubkeys) {
				peers := make([]string, len(x.DenyList))
				for i, p := range x.DenyList {
					h, err := m.hideString(strings.ToLower(p))
					if err != nil {
						return nil, err
					}
					peers[i] = h
				}
				v = &peerRestrict{DenyList: peers}
			}
		}
		data, err := json.Marshal(v)
		if err != nil {
			return nil, err
		}
		out[r.name] = string(data)
	}
	return out, nil
}

func newLitID(sessions []*Session) (string, error) {
	used := map[string]bool{}
	for _, s := range sessions {
		used[s.LitID] = true
		used[s.GroupID] = true
	}
	for i := 0; i < 64; i++ {
		b := make([]byte, 4)
		if _, err := rand.Read(b); err != nil {
			return "", err
		}
		id := hex.EncodeToString(b)
		if !used[id] {
			return id, nil
		}
	}
	return "", errors.New("could not pick a session ID")
}

// addSession is AddAutopilotSession: it checks the request against the
// server's feature definitions, creates a session whose credential carries
// litd's rule caveats, registers it with the autopilot server and stores it;
// the serve loop then serves it on the mailbox for the server's key.
func (a *autopilotService) addSession(ctx context.Context, _ CallInfo, request []byte) ([]byte, error) {
	req := &litrpc.AddAutopilotSessionRequest{}
	if err := proto.Unmarshal(request, req); err != nil {
		return nil, status.Error(codes.InvalidArgument, "malformed request")
	}
	if err := a.checkReady(); err != nil {
		return nil, err
	}
	now := a.now()
	switch {
	case len(req.Features) == 0:
		return nil, status.Error(codes.InvalidArgument, "must include at least one feature")
	case req.ExpiryTimestampSeconds > uint64(now.Add(10*365*24*time.Hour).Unix()):
		return nil, status.Error(codes.InvalidArgument, "expiry too far in the future")
	case !time.Unix(int64(req.ExpiryTimestampSeconds), 0).After(now):
		return nil, status.Error(codes.InvalidArgument, "expiry must be in the future")
	case req.NoPrivacyMapper:
		return nil, status.Error(codes.InvalidArgument, "this bridge always uses the privacy mapper")
	case req.DevServer:
		return nil, status.Error(codes.InvalidArgument, "dev_server (mailbox without TLS) is not supported")
	case req.SessionRules != nil && len(req.SessionRules.Rules) != 0:
		return nil, status.Error(codes.InvalidArgument, "session-wide rules are not supported")
	case req.MailboxServerAddr != "" && req.MailboxServerAddr != a.relay:
		return nil, status.Errorf(codes.InvalidArgument, "this bridge serves sessions through %s only", a.relay)
	}
	expiry := time.Unix(int64(req.ExpiryTimestampSeconds), 0).UTC()
	server, err := a.server.ListFeatures(ctx)
	if err != nil {
		return nil, status.Errorf(codes.Unavailable, "autopilot server: %v", status.Convert(err).Message())
	}
	// Privacy flags: the request's when set, otherwise every feature's ORed.
	var flags uint64
	if req.PrivacyFlagsSet {
		flags = req.PrivacyFlags
	}
	features := map[string]*featureState{}
	permSet := map[string]bool{}
	for name, cfg := range req.Features {
		f, ok := server.Features[name]
		if !ok {
			return nil, status.Errorf(codes.InvalidArgument, "%s is not a feature provided by the Autopilot server", name)
		}
		if _, supported := a.featureSupport(f); !supported {
			return nil, status.Errorf(codes.Unimplemented, "feature %s needs rules or node methods this bridge does not provide", name)
		}
		if !req.PrivacyFlagsSet {
			flags |= f.PrivacyFlags
		}
		state := &featureState{}
		chosen := map[string]bool{}
		if cfg.GetRules() != nil {
			for ruleName, value := range cfg.Rules.Rules {
				spec, ok := f.Rules[ruleName]
				if !ok {
					return nil, status.Errorf(codes.InvalidArgument, "autopilot did not specify %s as a rule for feature %s", ruleName, name)
				}
				v, err := ruleFromProto(ruleName, value)
				if err != nil {
					return nil, status.Errorf(codes.InvalidArgument, "rule %s: %v", ruleName, err)
				}
				lo, err := parseRuleJSON(ruleName, spec.MinValue)
				if err != nil {
					return nil, status.Errorf(codes.Internal, "autopilot server rule %s: %v", ruleName, err)
				}
				hi, err := parseRuleJSON(ruleName, spec.MaxValue)
				if err != nil {
					return nil, status.Errorf(codes.Internal, "autopilot server rule %s: %v", ruleName, err)
				}
				if err := verifySane(v, lo, hi, now); err != nil {
					return nil, status.Errorf(codes.InvalidArgument, "rule value for %s not valid for feature %s: %v", ruleName, name, err)
				}
				state.Rules.set(v)
				chosen[ruleName] = true
			}
		}
		for ruleName, spec := range f.Rules {
			if chosen[ruleName] {
				continue
			}
			v, err := parseRuleJSON(ruleName, spec.Default)
			if err != nil {
				return nil, status.Errorf(codes.Internal, "autopilot server rule %s: %v", ruleName, err)
			}
			state.Rules.set(v)
		}
		for _, p := range f.PermissionsList {
			state.Permissions = append(state.Permissions, p.Method)
			permSet[p.Method] = true
		}
		sort.Strings(state.Permissions)
		state.Config = string(cfg.GetConfig())
		features[name] = state
	}
	if err := checkPrivacyFlags(flags); err != nil {
		return nil, status.Error(codes.InvalidArgument, err.Error())
	}
	pf := privacyFlags(flags)

	a.addMu.Lock()
	defer a.addMu.Unlock()
	sessions, err := a.store.list()
	if err != nil {
		return nil, status.Error(codes.Internal, "session store unavailable")
	}
	litID, err := newLitID(sessions)
	if err != nil {
		return nil, status.Error(codes.Internal, err.Error())
	}
	group := litID
	var linkKey *btcec.PrivateKey
	if len(req.LinkedGroupId) != 0 {
		if len(req.LinkedGroupId) != 4 {
			return nil, status.Error(codes.InvalidArgument, "linked group ID must be 4 bytes")
		}
		group = hex.EncodeToString(req.LinkedGroupId)
		var first *Session
		for _, s := range sessions {
			if !s.isAutopilot() || s.GroupID != group {
				continue
			}
			if !s.Revoked && now.Before(s.ExpiresAt) {
				return nil, status.Errorf(codes.FailedPrecondition, "linked session %s is still active; revoke it first", s.LitID)
			}
			if s.LitID == group {
				first = s
			}
		}
		if first == nil {
			return nil, status.Errorf(codes.NotFound, "no session group %s", group)
		}
		raw, err := hex.DecodeString(first.PrivateKey)
		if err != nil || len(raw) != 32 {
			return nil, status.Error(codes.FailedPrecondition, "the group's first session has no transport key left")
		}
		linkKey, _ = btcec.PrivKeyFromBytes(raw)
	}

	session, _, err := newSession("", expiry, nil)
	if err != nil {
		return nil, status.Error(codes.Internal, err.Error())
	}
	session.ID = litID + session.ID[8:]
	session.Entropy = ""
	session.Type, session.LitID, session.GroupID = sessionTypeAutopilot, litID, group
	session.Name = req.Label
	session.RootKeyID = a.ceiling.RootKeyID
	for m := range permSet {
		session.AllowedMethods = append(session.AllowedMethods, m)
	}
	sort.Strings(session.AllowedMethods)

	// Pseudonymize the rules and configs the autopilot server will see.
	featureRules := map[string]map[string]string{}
	obfuscated := map[string][]byte{}
	err = a.privacy.do(group, func(m *privacyMap) error {
		for name, st := range features {
			r, err := pseudoRules(m, pf, st.Rules)
			if err != nil {
				return err
			}
			featureRules[name] = r
			if obfuscated[name], err = obfuscateConfig(m, pf, []byte(st.Config)); err != nil {
				return err
			}
		}
		return nil
	})
	if err != nil {
		return nil, status.Error(codes.InvalidArgument, err.Error())
	}
	rulesJSON, err := json.Marshal(struct {
		SessionRules map[string]string            `json:"session_rules"`
		FeatureRules map[string]map[string]string `json:"feature_rules"`
	}{FeatureRules: featureRules})
	if err != nil {
		return nil, status.Error(codes.Internal, err.Error())
	}
	caveats := []string{rulesCaveatPrefix + string(rulesJSON), privacyCaveat}
	session.Macaroon, err = addCaveats(a.ceiling.Macaroon, "time-before "+expiry.Format(time.RFC3339))
	if err != nil {
		return nil, status.Error(codes.Internal, "could not derive the session macaroon")
	}
	if session.ClientMacaroon, err = addCaveats(session.Macaroon, caveats...); err != nil {
		return nil, status.Error(codes.Internal, "could not derive the session macaroon")
	}
	session.Autopilot = &autopilotInfo{Server: a.server.Address(), MailboxAddr: a.relay, Features: features,
		Privacy: true, PrivacyFlags: flags, Caveats: caveats}

	keyBytes, _ := hex.DecodeString(session.PrivateKey)
	local, _ := btcec.PrivKeyFromBytes(keyBytes)
	regReq := &ap.RegisterSessionRequest{ResponderPubKey: local.PubKey().SerializeCompressed(), MailboxAddr: a.relay,
		FeatureConfigs: obfuscated, LitVersion: emulatedLitVersion, LndVersion: emulatedLndVersion, PrivacyFlags: flags}
	if linkKey != nil {
		digest := sha256.Sum256(regReq.ResponderPubKey)
		regReq.GroupResponderKey = linkKey.PubKey().SerializeCompressed()
		regReq.GroupResponderSig = ecdsa.Sign(linkKey, digest[:]).Serialize()
	}
	if err := checkTerms(ctx, a.server); err != nil {
		return nil, status.Error(codes.FailedPrecondition, err.Error())
	}
	reg, err := a.server.RegisterSession(ctx, regReq)
	if err != nil {
		return nil, status.Errorf(codes.Unavailable, "error registering session with autopilot server: %v", status.Convert(err).Message())
	}
	remote, err := btcec.ParsePubKey(reg.InitiatorPubKey)
	if err != nil {
		return nil, status.Error(codes.Unavailable, "the autopilot server returned an invalid key")
	}
	session.RemoteKey = hex.EncodeToString(remote.SerializeCompressed())
	unlock := a.lock()
	err = a.store.save(session)
	unlock()
	if err != nil {
		_ = a.server.RevokeSession(ctx, regReq.ResponderPubKey)
		return nil, status.Error(codes.Internal, "could not store the session")
	}
	return proto.Marshal(&litrpc.AddAutopilotSessionResponse{Session: marshalAutopilotSession(session, now)})
}

func (a *autopilotService) listSessions(_ context.Context, _ CallInfo, request []byte) ([]byte, error) {
	if err := proto.Unmarshal(request, &litrpc.ListAutopilotSessionsRequest{}); err != nil {
		return nil, status.Error(codes.InvalidArgument, "malformed request")
	}
	sessions, err := a.store.list()
	if err != nil {
		return nil, status.Error(codes.Internal, "session store unavailable")
	}
	resp := &litrpc.ListAutopilotSessionsResponse{}
	sort.Slice(sessions, func(i, j int) bool { return sessions[i].CreatedAt.Before(sessions[j].CreatedAt) })
	for _, s := range sessions {
		if s.isAutopilot() && s.Autopilot != nil {
			resp.Sessions = append(resp.Sessions, marshalAutopilotSession(s, a.now()))
		}
	}
	return proto.Marshal(resp)
}

func localPubKey(s *Session) []byte {
	raw, err := hex.DecodeString(s.PrivateKey)
	if err != nil || len(raw) != 32 {
		return nil
	}
	k, _ := btcec.PrivKeyFromBytes(raw)
	return k.PubKey().SerializeCompressed()
}

// revokeSession is RevokeAutopilotSession: the session is disabled locally at
// once (the serve loop closes its connection) and the server is told on a
// best-effort basis, as litd does; the activation loop retries the latter.
func (a *autopilotService) revokeSession(ctx context.Context, _ CallInfo, request []byte) ([]byte, error) {
	req := &litrpc.RevokeAutopilotSessionRequest{}
	if err := proto.Unmarshal(request, req); err != nil {
		return nil, status.Error(codes.InvalidArgument, "malformed request")
	}
	if _, err := btcec.ParsePubKey(req.LocalPublicKey); err != nil {
		return nil, status.Error(codes.InvalidArgument, "error parsing public key")
	}
	unlock := a.lock()
	sessions, err := a.store.list()
	var target *Session
	for _, s := range sessions {
		if s.isAutopilot() && string(localPubKey(s)) == string(req.LocalPublicKey) {
			target = s
		}
	}
	if err == nil && target != nil {
		err = a.store.update(target.ID, func(s *Session) error {
			if !s.Revoked {
				s.Revoked, s.RevokedAt = true, a.now().UTC()
			}
			return nil
		})
	}
	unlock()
	if err != nil {
		return nil, status.Error(codes.Internal, "session store unavailable")
	}
	if target == nil {
		return nil, status.Error(codes.NotFound, "session not found")
	}
	if a.server != nil {
		if a.server.RevokeSession(ctx, req.LocalPublicKey) == nil {
			a.markServerRevoked(target.ID)
		}
	}
	return proto.Marshal(&litrpc.RevokeAutopilotSessionResponse{})
}

func (a *autopilotService) markServerRevoked(id string) {
	unlock := a.lock()
	defer unlock()
	_ = a.store.update(id, func(s *Session) error {
		if s.Autopilot != nil {
			s.Autopilot.ServerRevoked = true
		}
		return nil
	})
}

func (a *autopilotService) listActions(_ context.Context, _ CallInfo, request []byte) ([]byte, error) {
	req := &litrpc.ListActionsRequest{}
	if err := proto.Unmarshal(request, req); err != nil {
		return nil, status.Error(codes.InvalidArgument, "malformed request")
	}
	resp, err := a.actions.list(req)
	if err != nil {
		return nil, status.Error(codes.InvalidArgument, err.Error())
	}
	return proto.Marshal(resp)
}

// keepAlive is litd's activation loop (autopilotserver/client.go): every
// active autopilot session is re-activated with the server once per cadence;
// a session the server rejects for good is revoked locally; a revoked or
// expired session is reported to the server until it acknowledges.
func (a *autopilotService) keepAlive(ctx context.Context) {
	if a.server == nil {
		return
	}
	sessions, err := a.store.list()
	if err != nil {
		return
	}
	now := a.now()
	for _, s := range sessions {
		if !s.isAutopilot() || s.Autopilot == nil || s.Autopilot.Server != a.server.Address() {
			continue
		}
		pub := localPubKey(s)
		if pub == nil {
			continue
		}
		if s.Revoked || !now.Before(s.ExpiresAt) {
			if !s.Autopilot.ServerRevoked && a.server.RevokeSession(ctx, pub) == nil {
				a.markServerRevoked(s.ID)
			}
			continue
		}
		if err := a.server.ActivateSession(ctx, pub); err != nil && strings.Contains(err.Error(), "the client has been rejected") {
			unlock := a.lock()
			_ = a.store.update(s.ID, func(s *Session) error {
				s.Revoked, s.RevokedAt = true, a.now().UTC()
				return nil
			})
			unlock()
		}
	}
}

func marshalAutopilotSession(s *Session, now time.Time) *litrpc.Session {
	state := litrpc.SessionState_STATE_CREATED
	switch {
	case s.Revoked:
		state = litrpc.SessionState_STATE_REVOKED
	case !now.Before(s.ExpiresAt):
		state = litrpc.SessionState_STATE_EXPIRED
	case s.Confirmed:
		state = litrpc.SessionState_STATE_IN_USE
	}
	id, _ := hex.DecodeString(s.LitID)
	group, _ := hex.DecodeString(s.GroupID)
	remote, _ := hex.DecodeString(s.RemoteKey)
	out := &litrpc.Session{Id: id, Label: s.Name, SessionState: state, SessionType: litrpc.SessionType_TYPE_AUTOPILOT,
		ExpiryTimestampSeconds: uint64(s.ExpiresAt.Unix()), LocalPublicKey: localPubKey(s), RemotePublicKey: remote,
		CreatedAt: uint64(s.CreatedAt.Unix()), GroupId: group, AutopilotFeatureInfo: map[string]*litrpc.RulesMap{},
		FeatureConfigs: map[string]string{}}
	if !s.RevokedAt.IsZero() {
		out.RevokedAt = uint64(s.RevokedAt.Unix())
	}
	recipe := &litrpc.MacaroonRecipe{}
	for _, m := range s.AllowedMethods {
		recipe.Permissions = append(recipe.Permissions, &litrpc.MacaroonPermission{Entity: "uri", Action: m})
	}
	if info := s.Autopilot; info != nil {
		out.MailboxServerAddr = info.MailboxAddr
		out.PrivacyFlags = info.PrivacyFlags
		recipe.Caveats = append(recipe.Caveats, info.Caveats...)
		for name, f := range info.Features {
			rules := map[string]*litrpc.RuleValue{}
			for _, r := range f.Rules.named() {
				rules[r.name] = ruleToProto(r.value)
			}
			out.AutopilotFeatureInfo[name] = &litrpc.RulesMap{Rules: rules}
			out.FeatureConfigs[name] = f.Config
		}
	}
	out.MacaroonRecipe = recipe
	return out
}

// revokeAutopilotSessionsLocally disables every autopilot session (after the
// ceiling's root key changes, their macaroons no longer verify anyway).
func revokeAutopilotSessionsLocally(store *Store, now time.Time) error {
	sessions, err := store.list()
	if err != nil {
		return err
	}
	for _, s := range sessions {
		if !s.isAutopilot() || s.Revoked {
			continue
		}
		if err := store.update(s.ID, func(s *Session) error {
			s.Revoked, s.RevokedAt = true, now.UTC()
			return nil
		}); err != nil {
			return err
		}
	}
	return nil
}

func saveAutopilotCeiling(dir string, c *autopilotCeiling) error {
	data, err := json.MarshalIndent(c, "", "  ")
	if err != nil {
		return err
	}
	return writePrivateFile(dir, autopilotCeilingPath(dir), data)
}

func ceilingExists(dir string) bool {
	_, err := os.Lstat(autopilotCeilingPath(dir))
	return err == nil
}
