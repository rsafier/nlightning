package main

import (
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"sort"
	"time"

	"github.com/rsafier/nlightning/tools/lnc/internal/litrpc"
)

// Autopilot rules, as litd defines them (lightning-terminal rules/*.go). The
// autopilot server sends each feature's rules as JSON in litd's field names
// (default, min and max values); Terminal sends the user's choice as
// litrpc.RuleValue. The bridge keeps the real (unmapped) values of a session's
// rules and enforces them on every call the session makes (firewall.go).
const (
	ruleRateLimit       = "rate-limit"
	ruleHistoryLimit    = "history-limit"
	ruleChanPolicy      = "channel-policy-bounds"
	ruleChannelRestrict = "channel-restriction"
	rulePeerRestrict    = "peer-restriction"
)

// knownRules are the rules the bridge can enforce. A feature that names any
// other rule (AutoOpen's channel-constraint and on-chain-budget) is reported
// with requires_upgrade and cannot be subscribed to.
var knownRules = map[string]bool{
	ruleRateLimit: true, ruleHistoryLimit: true, ruleChanPolicy: true,
	ruleChannelRestrict: true, rulePeerRestrict: true,
}

type rate struct {
	Iterations uint32 `json:"iterations"`
	NumHours   uint32 `json:"num_hours"`
}

func (r *rate) lessThan(o *rate) bool {
	return float64(r.Iterations)/float64(r.NumHours) < float64(o.Iterations)/float64(o.NumHours)
}

type rateLimit struct {
	WriteLimit *rate `json:"write_limit"`
	ReadLimit  *rate `json:"read_limit"`
}

type historyLimit struct {
	StartDate time.Time     `json:"start_date,omitempty"`
	Duration  time.Duration `json:"duration,omitempty"`
}

// startDate is the earliest time a request may ask about.
func (h *historyLimit) startDate(now time.Time) time.Time {
	if h.Duration > 0 {
		return now.Add(-h.Duration)
	}
	return h.StartDate
}

type chanPolicyBounds struct {
	MinBaseMsat  uint64 `json:"min_base_msat"`
	MaxBaseMsat  uint64 `json:"max_base_msat"`
	MinRatePPM   uint32 `json:"min_rate_ppm"`
	MaxRatePPM   uint32 `json:"max_rate_ppm"`
	MinCLTVDelta uint32 `json:"min_cltv_delta"`
	MaxCLTVDelta uint32 `json:"max_cltv_delta"`
	MinHtlcMsat  uint64 `json:"min_htlc_msat"`
	MaxHtlcMsat  uint64 `json:"max_htlc_msat"`
}

type channelRestrict struct {
	DenyList []uint64 `json:"channel_deny_list"`
}

type peerRestrict struct {
	DenyList []string `json:"peer_deny_list"`
}

// ruleSet is one feature's rules with real (unmapped) values. Nil means the
// rule is not part of the feature.
type ruleSet struct {
	RateLimit       *rateLimit        `json:"rate_limit,omitempty"`
	HistoryLimit    *historyLimit     `json:"history_limit,omitempty"`
	ChanPolicy      *chanPolicyBounds `json:"chan_policy_bounds,omitempty"`
	ChannelRestrict *channelRestrict  `json:"channel_restrict,omitempty"`
	PeerRestrict    *peerRestrict     `json:"peer_restrict,omitempty"`
}

// parseRuleJSON decodes one rule value in litd's JSON form (as the autopilot
// server sends defaults, minimums and maximums) and validates it.
func parseRuleJSON(name string, raw []byte) (any, error) {
	var v any
	switch name {
	case ruleRateLimit:
		v = &rateLimit{}
	case ruleHistoryLimit:
		v = &historyLimit{}
	case ruleChanPolicy:
		v = &chanPolicyBounds{}
	case ruleChannelRestrict:
		v = &channelRestrict{}
	case rulePeerRestrict:
		v = &peerRestrict{}
	default:
		return nil, fmt.Errorf("%s is not a known rule", name)
	}
	if err := json.Unmarshal(raw, v); err != nil {
		return nil, fmt.Errorf("rule %s: %w", name, err)
	}
	return v, validateRule(v)
}

func validateRule(v any) error {
	switch r := v.(type) {
	case *rateLimit:
		if r.ReadLimit == nil || r.WriteLimit == nil || r.ReadLimit.NumHours == 0 || r.WriteLimit.NumHours == 0 {
			return errors.New("rate-limit needs read and write limits over a positive number of hours")
		}
	case *historyLimit:
		if !r.StartDate.IsZero() && r.Duration != 0 {
			return errors.New("history-limit cannot set both start date and duration")
		}
		if r.Duration < 0 {
			return errors.New("history-limit duration must not be negative")
		}
	case *chanPolicyBounds:
		if r.MinBaseMsat > r.MaxBaseMsat || r.MinRatePPM > r.MaxRatePPM || r.MinCLTVDelta > r.MaxCLTVDelta {
			return errors.New("channel-policy-bounds minimum above maximum")
		}
		if r.MaxBaseMsat > math.MaxInt64 {
			return errors.New("channel-policy-bounds base fee too large")
		}
	case *peerRestrict:
		for _, p := range r.DenyList {
			if b, err := hex.DecodeString(p); err != nil || len(b) != 33 {
				return fmt.Errorf("peer-restriction: %q is not a node public key", p)
			}
		}
	}
	return nil
}

// ruleFromProto converts Terminal's RuleValue to a rule value.
func ruleFromProto(name string, v *litrpc.RuleValue) (any, error) {
	var out any
	switch name {
	case ruleRateLimit:
		p := v.GetRateLimit()
		if p == nil || p.ReadLimit == nil || p.WriteLimit == nil {
			return nil, errors.New("rate-limit value missing")
		}
		out = &rateLimit{
			ReadLimit:  &rate{Iterations: p.ReadLimit.Iterations, NumHours: p.ReadLimit.NumHours},
			WriteLimit: &rate{Iterations: p.WriteLimit.Iterations, NumHours: p.WriteLimit.NumHours},
		}
	case ruleHistoryLimit:
		p := v.GetHistoryLimit()
		if p == nil {
			return nil, errors.New("history-limit value missing")
		}
		if p.StartTime != 0 && p.Duration != 0 {
			return nil, errors.New("cant set both start time and duration")
		}
		if p.StartTime != 0 {
			if p.StartTime > math.MaxInt64 {
				return nil, errors.New("history-limit start time out of range")
			}
			out = &historyLimit{StartDate: time.Unix(int64(p.StartTime), 0).UTC()}
		} else {
			if p.Duration > uint64(math.MaxInt64/int64(time.Second)) {
				return nil, errors.New("history-limit duration out of range")
			}
			out = &historyLimit{Duration: time.Duration(p.Duration) * time.Second}
		}
	case ruleChanPolicy:
		p := v.GetChanPolicyBounds()
		if p == nil {
			return nil, errors.New("channel-policy-bounds value missing")
		}
		out = &chanPolicyBounds{MinBaseMsat: p.MinBaseMsat, MaxBaseMsat: p.MaxBaseMsat, MinRatePPM: p.MinRatePpm,
			MaxRatePPM: p.MaxRatePpm, MinCLTVDelta: p.MinCltvDelta, MaxCLTVDelta: p.MaxCltvDelta,
			MinHtlcMsat: p.MinHtlcMsat, MaxHtlcMsat: p.MaxHtlcMsat}
	case ruleChannelRestrict:
		p := v.GetChannelRestrict()
		if p == nil {
			return nil, errors.New("channel-restriction value missing")
		}
		out = &channelRestrict{DenyList: append([]uint64(nil), p.ChannelIds...)}
	case rulePeerRestrict:
		p := v.GetPeerRestrict()
		if p == nil {
			return nil, errors.New("peer-restriction value missing")
		}
		out = &peerRestrict{DenyList: append([]string(nil), p.PeerIds...)}
	default:
		return nil, fmt.Errorf("%s is not a known rule", name)
	}
	return out, validateRule(out)
}

// ruleToProto converts a rule value to litrpc.RuleValue.
func ruleToProto(v any) *litrpc.RuleValue {
	switch r := v.(type) {
	case *rateLimit:
		return &litrpc.RuleValue{Value: &litrpc.RuleValue_RateLimit{RateLimit: &litrpc.RateLimit{
			ReadLimit:  &litrpc.Rate{Iterations: r.ReadLimit.Iterations, NumHours: r.ReadLimit.NumHours},
			WriteLimit: &litrpc.Rate{Iterations: r.WriteLimit.Iterations, NumHours: r.WriteLimit.NumHours},
		}}}
	case *historyLimit:
		var start uint64
		if !r.StartDate.IsZero() && r.StartDate.Unix() > 0 {
			start = uint64(r.StartDate.Unix())
		}
		return &litrpc.RuleValue{Value: &litrpc.RuleValue_HistoryLimit{HistoryLimit: &litrpc.HistoryLimit{
			StartTime: start, Duration: uint64(r.Duration.Seconds())}}}
	case *chanPolicyBounds:
		return &litrpc.RuleValue{Value: &litrpc.RuleValue_ChanPolicyBounds{ChanPolicyBounds: &litrpc.ChannelPolicyBounds{
			MinBaseMsat: r.MinBaseMsat, MaxBaseMsat: r.MaxBaseMsat, MinRatePpm: r.MinRatePPM, MaxRatePpm: r.MaxRatePPM,
			MinCltvDelta: r.MinCLTVDelta, MaxCltvDelta: r.MaxCLTVDelta, MinHtlcMsat: r.MinHtlcMsat, MaxHtlcMsat: r.MaxHtlcMsat}}}
	case *channelRestrict:
		return &litrpc.RuleValue{Value: &litrpc.RuleValue_ChannelRestrict{ChannelRestrict: &litrpc.ChannelRestrict{
			ChannelIds: append([]uint64(nil), r.DenyList...)}}}
	case *peerRestrict:
		return &litrpc.RuleValue{Value: &litrpc.RuleValue_PeerRestrict{PeerRestrict: &litrpc.PeerRestrict{
			PeerIds: append([]string(nil), r.DenyList...)}}}
	}
	return nil
}

// verifySane checks a chosen rule value against the autopilot server's
// minimum and maximum for the feature, as litd's VerifySane does.
func verifySane(v, minV, maxV any, now time.Time) error {
	switch r := v.(type) {
	case *rateLimit:
		lo, hi := minV.(*rateLimit), maxV.(*rateLimit)
		if r.ReadLimit.lessThan(lo.ReadLimit) || hi.ReadLimit.lessThan(r.ReadLimit) {
			return errors.New("read limit is not between the min and max")
		}
		if r.WriteLimit.lessThan(lo.WriteLimit) || hi.WriteLimit.lessThan(r.WriteLimit) {
			return errors.New("write limit is not between the min and max")
		}
	case *historyLimit:
		lo := minV.(*historyLimit)
		if !r.startDate(now).Before(lo.startDate(now)) {
			return fmt.Errorf("history-limit start date must be before %s", lo.startDate(now).UTC().Format(time.RFC3339))
		}
	case *chanPolicyBounds:
		lo, hi := minV.(*chanPolicyBounds), maxV.(*chanPolicyBounds)
		switch {
		case r.MinBaseMsat < lo.MinBaseMsat || r.MinBaseMsat > hi.MinBaseMsat:
			return errors.New("invalid min base fee")
		case r.MaxBaseMsat < lo.MaxBaseMsat || r.MaxBaseMsat > hi.MaxBaseMsat:
			return errors.New("invalid max base fee")
		case r.MinRatePPM < lo.MinRatePPM || r.MinRatePPM > hi.MinRatePPM:
			return errors.New("invalid min proportional fee")
		case r.MaxRatePPM < lo.MaxRatePPM || r.MaxRatePPM > hi.MaxRatePPM:
			return errors.New("invalid max proportional fee")
		case r.MinCLTVDelta < lo.MinCLTVDelta || r.MinCLTVDelta > hi.MinCLTVDelta:
			return errors.New("invalid min cltv delta")
		case r.MaxCLTVDelta < lo.MaxCLTVDelta || r.MaxCLTVDelta > hi.MaxCLTVDelta:
			return errors.New("invalid max cltv delta")
		case r.MinHtlcMsat < lo.MinHtlcMsat || r.MinHtlcMsat > hi.MinHtlcMsat:
			return errors.New("invalid min htlc msat amt")
		case r.MaxHtlcMsat < lo.MaxHtlcMsat || r.MaxHtlcMsat > hi.MaxHtlcMsat:
			return errors.New("invalid max htlc msat amt")
		}
	}
	return nil
}

// set places a rule value into the set by its type.
func (s *ruleSet) set(v any) {
	switch r := v.(type) {
	case *rateLimit:
		s.RateLimit = r
	case *historyLimit:
		s.HistoryLimit = r
	case *chanPolicyBounds:
		s.ChanPolicy = r
	case *channelRestrict:
		s.ChannelRestrict = r
	case *peerRestrict:
		s.PeerRestrict = r
	}
}

// named returns the set's rules by litd rule name, in name order.
func (s *ruleSet) named() []namedRule {
	var out []namedRule
	add := func(name string, ok bool, v any) {
		if ok {
			out = append(out, namedRule{name, v})
		}
	}
	add(ruleChanPolicy, s.ChanPolicy != nil, s.ChanPolicy)
	add(ruleChannelRestrict, s.ChannelRestrict != nil, s.ChannelRestrict)
	add(ruleHistoryLimit, s.HistoryLimit != nil, s.HistoryLimit)
	add(rulePeerRestrict, s.PeerRestrict != nil, s.PeerRestrict)
	add(ruleRateLimit, s.RateLimit != nil, s.RateLimit)
	sort.Slice(out, func(i, j int) bool { return out[i].name < out[j].name })
	return out
}

type namedRule struct {
	name  string
	value any
}
