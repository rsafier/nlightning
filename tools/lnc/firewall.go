package main

import (
	"context"
	"crypto/subtle"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"strings"
	"time"

	"github.com/lightningnetwork/lnd/lnrpc"
	"github.com/rsafier/nlightning/tools/lnc/internal/litrpc"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
	"gopkg.in/macaroon.v2"
)

// litd's firewall caveats (lightning-terminal firewall/caveats.go).
const (
	metaCaveatPrefix  = "lnd-custom lit-mac-fw meta:"
	rulesCaveatPrefix = "lnd-custom lit-mac-fw rules:"
	privacyCaveat     = "lnd-custom privacy"
)

// MetaInfo is the meta information an autopilot attaches to each call as a
// first-party caveat on its session macaroon (firewall.InterceptMetaInfo).
type MetaInfo struct {
	ActorName          string `json:"actor_name"`
	Feature            string `json:"feature"`
	Trigger            string `json:"trigger"`
	Intent             string `json:"intent"`
	StructuredJsonData string `json:"structured_json_data"`
}

// autopilotAuthenticator checks a presented credential against an autopilot
// session's client macaroon. The client may append first-party caveats, as
// litd's autopilot does with its meta caveat on every call; the result is
// accepted only when its signature is exactly the client macaroon's signature
// extended over those caveats (so only a holder of the client macaroon can
// produce it) and every appended caveat is one the bridge understands: at most
// one meta caveat and time-before caveats that have not passed. Anything else
// (third-party caveats, unknown conditions) is refused.
func autopilotAuthenticator(clientMacHex string, now func() time.Time) (func([]byte) (CallInfo, error), error) {
	raw, err := hex.DecodeString(clientMacHex)
	if err != nil {
		return nil, err
	}
	var base macaroon.Macaroon
	if err := base.UnmarshalBinary(raw); err != nil {
		return nil, err
	}
	return func(presented []byte) (CallInfo, error) {
		var p macaroon.Macaroon
		if err := p.UnmarshalBinary(presented); err != nil {
			return CallInfo{}, err
		}
		if subtle.ConstantTimeCompare(p.Id(), base.Id()) != 1 || p.Location() != base.Location() {
			return CallInfo{}, errors.New("different macaroon")
		}
		pc, bc := p.Caveats(), base.Caveats()
		if len(pc) < len(bc) {
			return CallInfo{}, errors.New("caveats removed")
		}
		for i := range bc {
			if string(pc[i].Id) != string(bc[i].Id) || string(pc[i].VerificationId) != string(bc[i].VerificationId) || pc[i].Location != bc[i].Location {
				return CallInfo{}, errors.New("caveats changed")
			}
		}
		check := base.Clone()
		var call CallInfo
		for _, c := range pc[len(bc):] {
			if len(c.VerificationId) != 0 {
				return CallInfo{}, errors.New("third-party caveat")
			}
			id := string(c.Id)
			switch {
			case strings.HasPrefix(id, metaCaveatPrefix):
				if call.Meta != nil {
					return CallInfo{}, errors.New("more than one meta caveat")
				}
				var meta MetaInfo
				if err := json.Unmarshal([]byte(id[len(metaCaveatPrefix):]), &meta); err != nil {
					return CallInfo{}, err
				}
				call.Meta = &meta
			case strings.HasPrefix(id, "time-before "):
				t, err := time.Parse(time.RFC3339Nano, strings.TrimPrefix(id, "time-before "))
				if err != nil || !now().Before(t) {
					return CallInfo{}, errors.New("expired")
				}
			default:
				return CallInfo{}, errors.New("unsupported caveat")
			}
			if err := check.AddFirstPartyCaveat(c.Id); err != nil {
				return CallInfo{}, err
			}
		}
		if subtle.ConstantTimeCompare(check.Signature(), p.Signature()) != 1 {
			return CallInfo{}, errors.New("signature mismatch")
		}
		return call, nil
	}, nil
}

// The methods an autopilot session can be granted: those of the AutoFees
// feature, each with privacy mapping and rule checks implemented here.
const (
	mForwardingHistory   = "/lnrpc.Lightning/ForwardingHistory"
	mFeeReport           = "/lnrpc.Lightning/FeeReport"
	mListChannels        = "/lnrpc.Lightning/ListChannels"
	mUpdateChannelPolicy = "/lnrpc.Lightning/UpdateChannelPolicy"
)

var firewallMethods = map[string]bool{
	mForwardingHistory: true, mFeeReport: true, mListChannels: true, mUpdateChannelPolicy: true,
}

// firewallReadMethods are the read-only ones (the rate-limit rule counts reads
// and writes separately; litd classifies by the method's permission).
var firewallReadMethods = map[string]bool{mForwardingHistory: true, mFeeReport: true, mListChannels: true}

func isReadMethod(m string) bool { return firewallReadMethods[m] }

// firewall serves one autopilot session: every call goes through call(), never
// straight to the node.
type firewall struct {
	session  *Session
	backend  grpc.ClientConnInterface
	actions  *actionLog
	privacy  *privacyStore
	now      func() time.Time
	invokeFn func(ctx context.Context, method string, req, resp proto.Message) error
}

func newFirewall(s *Session, backend grpc.ClientConnInterface, actions *actionLog, privacy *privacyStore) *firewall {
	f := &firewall{session: s, backend: backend, actions: actions, privacy: privacy, now: time.Now}
	f.invokeFn = f.invokeBackend
	return f
}

// handlers returns a local handler for every firewall method the session was
// granted.
func (f *firewall) handlers() map[string]LocalHandler {
	out := map[string]LocalHandler{}
	for _, m := range f.session.AllowedMethods {
		if !firewallMethods[m] {
			continue
		}
		method := m
		out[method] = func(ctx context.Context, call CallInfo, request []byte) ([]byte, error) {
			return f.call(ctx, method, call, request)
		}
	}
	return out
}

// invokeBackend makes one unary node call with the session's own backend
// macaroon (the scoped autopilot macaroon, never an admin one).
func (f *firewall) invokeBackend(ctx context.Context, method string, req, resp proto.Message) error {
	reqBytes, err := proto.Marshal(req)
	if err != nil {
		return err
	}
	ctx = metadata.NewOutgoingContext(ctx, metadata.Pairs("macaroon", f.session.Macaroon))
	var respBytes []byte
	if err := f.backend.Invoke(ctx, method, &reqBytes, &respBytes, grpc.ForceCodec(opaqueCodec{})); err != nil {
		return err
	}
	return proto.Unmarshal(respBytes, resp)
}

func (f *firewall) privacyOn() bool { return f.session.Autopilot != nil && f.session.Autopilot.Privacy }

func (f *firewall) flags() privacyFlags { return privacyFlags(f.session.Autopilot.PrivacyFlags) }

// call runs one autopilot call: authorization by feature, decode, privacy
// reveal, action log, rules, node call, privacy hide, outcome.
func (f *firewall) call(ctx context.Context, method string, call CallInfo, request []byte) ([]byte, error) {
	info := f.session.Autopilot
	if info == nil {
		return nil, status.Error(codes.PermissionDenied, "not an autopilot session")
	}
	if !f.now().Before(f.session.ExpiresAt) {
		return nil, status.Error(codes.Unauthenticated, "session expired")
	}
	if call.Meta == nil {
		return nil, status.Error(codes.PermissionDenied, "missing MetaInfo")
	}
	feature, ok := info.Features[call.Meta.Feature]
	if !ok {
		return nil, status.Errorf(codes.PermissionDenied, "feature %s does not correspond to a feature of this session", call.Meta.Feature)
	}
	if !contains(feature.Permissions, method) {
		return nil, status.Errorf(codes.PermissionDenied, "method %s is not allowed for feature %s", method, call.Meta.Feature)
	}
	req, resp, err := newMessages(method)
	if err != nil {
		return nil, status.Error(codes.PermissionDenied, err.Error())
	}
	if err := proto.Unmarshal(request, req); err != nil {
		return nil, status.Error(codes.InvalidArgument, "malformed request")
	}
	// Fields this bridge does not know cannot be checked or mapped, so they
	// are refused rather than passed on.
	if hasUnknownFields(req) {
		return nil, status.Error(codes.InvalidArgument, "request carries fields the bridge cannot check")
	}
	if f.privacyOn() {
		if err := f.revealRequest(method, req); err != nil {
			return nil, status.Error(codes.InvalidArgument, "unknown pseudonym in request")
		}
	}
	// The rate-limit window counts the group's earlier calls of this feature.
	var limit *rate
	window := time.Time{}
	if rl := feature.Rules.RateLimit; rl != nil {
		limit = rl.WriteLimit
		if isReadMethod(method) {
			limit = rl.ReadLimit
		}
		window = f.now().Add(-time.Duration(limit.NumHours) * time.Hour)
	}
	params, _ := lnrpc.ProtoJSONMarshalOpts.Marshal(req)
	// The action is recorded and the group's earlier calls of this feature in
	// the rate-limit window counted under one lock, so concurrent calls cannot
	// both slip under the limit.
	index, prior, err := f.actions.addCounting(action{
		SessionID: f.session.LitID, GroupID: f.session.GroupID, MacaroonID: f.session.LitID,
		ActorName: call.Meta.ActorName, FeatureName: call.Meta.Feature, Trigger: call.Meta.Trigger,
		Intent: call.Meta.Intent, Structured: call.Meta.StructuredJsonData, Method: method, ParamsJSON: string(params),
	}, window, isReadMethod)
	if err != nil {
		return nil, status.Error(codes.Internal, "could not record the action")
	}
	fail := func(code codes.Code, reason string) ([]byte, error) {
		_ = f.actions.finish(index, litrpc.ActionState_STATE_ERROR, reason)
		return nil, status.Error(code, reason)
	}
	if limit != nil && prior >= limit.Iterations {
		return fail(codes.ResourceExhausted, "too many requests received")
	}
	if err := f.checkRules(ctx, method, feature.Rules, req); err != nil {
		return fail(codes.PermissionDenied, err.Error())
	}
	if err := f.invokeFn(ctx, method, req, resp); err != nil {
		code := status.Code(err)
		_ = f.actions.finish(index, litrpc.ActionState_STATE_ERROR, status.Convert(err).Message())
		// The node's message may name real channels; only its code goes out.
		return nil, status.Errorf(code, "node call failed (%s)", code)
	}
	out := proto.Message(resp)
	if f.privacyOn() {
		if out, err = f.hideResponse(method, resp); err != nil {
			return fail(codes.Internal, "privacy mapping failed")
		}
	} else {
		out = stripUnknown(resp)
	}
	encoded, err := proto.Marshal(out)
	if err != nil {
		return fail(codes.Internal, "encoding failed")
	}
	_ = f.actions.finish(index, litrpc.ActionState_STATE_DONE, "")
	return encoded, nil
}

func contains(list []string, v string) bool {
	for _, s := range list {
		if s == v {
			return true
		}
	}
	return false
}

func newMessages(method string) (proto.Message, proto.Message, error) {
	switch method {
	case mForwardingHistory:
		return &lnrpc.ForwardingHistoryRequest{}, &lnrpc.ForwardingHistoryResponse{}, nil
	case mFeeReport:
		return &lnrpc.FeeReportRequest{}, &lnrpc.FeeReportResponse{}, nil
	case mListChannels:
		return &lnrpc.ListChannelsRequest{}, &lnrpc.ListChannelsResponse{}, nil
	case mUpdateChannelPolicy:
		return &lnrpc.PolicyUpdateRequest{}, &lnrpc.PolicyUpdateResponse{}, nil
	}
	return nil, nil, fmt.Errorf("%s is not supported for autopilot sessions", method)
}

func hasUnknownFields(m proto.Message) bool {
	found := false
	var visit func(msg proto.Message)
	visit = func(msg proto.Message) {
		r := msg.ProtoReflect()
		if len(r.GetUnknown()) > 0 {
			found = true
			return
		}
		r.Range(func(fd protoreflect.FieldDescriptor, v protoreflect.Value) bool {
			if fd.Message() == nil {
				return true
			}
			switch {
			case fd.IsList():
				l := v.List()
				for i := 0; i < l.Len(); i++ {
					visit(l.Get(i).Message().Interface())
				}
			case fd.IsMap():
				v.Map().Range(func(_ protoreflect.MapKey, mv protoreflect.Value) bool {
					if fd.MapValue().Message() != nil {
						visit(mv.Message().Interface())
					}
					return !found
				})
			default:
				visit(v.Message().Interface())
			}
			return !found
		})
	}
	visit(m)
	return found
}

// stripUnknown drops fields this bridge does not know from a response before
// it reaches the autopilot (unmapped data must not pass through).
func stripUnknown(m proto.Message) proto.Message {
	c := proto.Clone(m)
	var visit func(msg proto.Message)
	visit = func(msg proto.Message) {
		r := msg.ProtoReflect()
		r.SetUnknown(nil)
		r.Range(func(fd protoreflect.FieldDescriptor, v protoreflect.Value) bool {
			if fd.Message() == nil || fd.IsMap() {
				return true
			}
			if fd.IsList() {
				l := v.List()
				for i := 0; i < l.Len(); i++ {
					visit(l.Get(i).Message().Interface())
				}
				return true
			}
			visit(v.Message().Interface())
			return true
		})
	}
	visit(c)
	return c
}

// checkRules applies the feature's rules to a (revealed) request.
func (f *firewall) checkRules(ctx context.Context, method string, rules ruleSet, req proto.Message) error {
	switch r := req.(type) {
	case *lnrpc.ForwardingHistoryRequest:
		if h := rules.HistoryLimit; h != nil {
			start := h.startDate(f.now())
			if start.Unix() > 0 && r.StartTime < uint64(start.Unix()) {
				return fmt.Errorf("can't request a start time before %s", start.UTC().Format(time.RFC3339))
			}
		}
	case *lnrpc.PolicyUpdateRequest:
		return f.checkPolicyUpdate(ctx, rules, r)
	}
	return nil
}

// checkPolicyUpdate enforces channel-policy-bounds (litd's checks, plus the
// inbound fee bounded by the outbound bounds), the channel and peer
// restrictions, and two refusals litd does not make: a global (all channels)
// update and create_missing_edge.
func (f *firewall) checkPolicyUpdate(ctx context.Context, rules ruleSet, r *lnrpc.PolicyUpdateRequest) error {
	if r.GetGlobal() {
		return errors.New("autopilot sessions may only update one channel at a time")
	}
	cp := r.GetChanPoint()
	if cp == nil {
		return errors.New("no channel point specified")
	}
	if r.CreateMissingEdge {
		return errors.New("create_missing_edge is not permitted")
	}
	if b := rules.ChanPolicy; b != nil {
		if r.BaseFeeMsat < 0 || uint64(r.BaseFeeMsat) < b.MinBaseMsat || uint64(r.BaseFeeMsat) > b.MaxBaseMsat {
			return errors.New("invalid base fee amount")
		}
		if r.FeeRate == 0 && r.FeeRatePpm == 0 && b.MinRatePPM > 0 {
			return errors.New("invalid fee rate")
		}
		feeRate := r.FeeRatePpm
		if r.FeeRate != 0 {
			if r.FeeRate < 0 || math.IsNaN(r.FeeRate) || r.FeeRate*1e6 > math.MaxUint32 {
				return errors.New("invalid fee rate")
			}
			feeRate = uint32(math.Round(r.FeeRate * 1e6))
		}
		if feeRate < b.MinRatePPM || feeRate > b.MaxRatePPM {
			return errors.New("invalid fee rate")
		}
		if r.TimeLockDelta < b.MinCLTVDelta || r.TimeLockDelta > b.MaxCLTVDelta {
			return errors.New("invalid cltv delta")
		}
		if r.MinHtlcMsatSpecified && r.MinHtlcMsat < b.MinHtlcMsat {
			return errors.New("invalid min htlc msat amount")
		}
		if r.MaxHtlcMsat > b.MaxHtlcMsat {
			return errors.New("invalid max htlc msat amount")
		}
		if in := r.InboundFee; in != nil {
			if int64(in.BaseFeeMsat) < -int64(b.MaxBaseMsat) || int64(in.BaseFeeMsat) > int64(b.MaxBaseMsat) ||
				int64(in.FeeRatePpm) < -int64(b.MaxRatePPM) || int64(in.FeeRatePpm) > int64(b.MaxRatePPM) {
				return errors.New("invalid inbound fee")
			}
		}
	} else {
		return errors.New("feature has no channel-policy-bounds rule")
	}
	if rules.ChannelRestrict == nil && rules.PeerRestrict == nil {
		return nil
	}
	txid, err := lnrpc.GetChanPointFundingTxid(cp)
	if err != nil {
		return err
	}
	point := fmt.Sprintf("%s:%d", txid.String(), cp.GetOutputIndex())
	channels := &lnrpc.ListChannelsResponse{}
	if err := f.invokeFn(ctx, mListChannels, &lnrpc.ListChannelsRequest{}, channels); err != nil {
		return errors.New("could not look up the channel")
	}
	for _, c := range channels.Channels {
		if c.ChannelPoint != point {
			continue
		}
		if rules.ChannelRestrict != nil {
			for _, id := range rules.ChannelRestrict.DenyList {
				if id == c.ChanId {
					return errors.New("illegal action on channel in channel restriction list")
				}
			}
		}
		if rules.PeerRestrict != nil {
			for _, p := range rules.PeerRestrict.DenyList {
				if strings.EqualFold(p, c.RemotePubkey) {
					return errors.New("illegal action on peer in peer restriction list")
				}
			}
		}
		return nil
	}
	return errors.New("unknown channel point")
}

// revealRequest replaces pseudonyms in a request with the real values.
func (f *firewall) revealRequest(method string, req proto.Message) error {
	switch r := req.(type) {
	case *lnrpc.ListChannelsRequest:
		if len(r.Peer) == 0 || f.flags().has(privClearPubkeys) {
			return nil
		}
		return f.privacy.do(f.session.GroupID, func(m *privacyMap) error {
			real, err := m.real(hex.EncodeToString(r.Peer))
			if err != nil {
				return err
			}
			r.Peer, err = hex.DecodeString(real)
			return err
		})
	case *lnrpc.PolicyUpdateRequest:
		cp := r.GetChanPoint()
		if cp == nil {
			return nil
		}
		txid, err := lnrpc.GetChanPointFundingTxid(cp)
		if err != nil {
			return err
		}
		newTxid, newIndex := txid.String(), cp.GetOutputIndex()
		if !f.flags().has(privClearChanIDs) {
			err = f.privacy.do(f.session.GroupID, func(m *privacyMap) error {
				var err error
				newTxid, newIndex, err = m.revealChanPoint(newTxid, newIndex)
				return err
			})
			if err != nil {
				return err
			}
		}
		r.Scope = &lnrpc.PolicyUpdateRequest_ChanPoint{ChanPoint: &lnrpc.ChannelPoint{
			FundingTxid: &lnrpc.ChannelPoint_FundingTxidStr{FundingTxidStr: newTxid}, OutputIndex: newIndex}}
	}
	return nil
}

// hideResponse builds the autopilot's view of a response, field by field as
// litd's privacy mapper does (firewall/privacy_mapper.go): identifiers
// replaced by stored pseudonyms, amounts and timestamps randomized, and every
// field not listed dropped.
func (f *firewall) hideResponse(method string, resp proto.Message) (proto.Message, error) {
	flags := f.flags()
	var out proto.Message
	err := f.privacy.do(f.session.GroupID, func(m *privacyMap) error {
		var err error
		switch r := resp.(type) {
		case *lnrpc.ForwardingHistoryResponse:
			out, err = hideForwardingHistory(m, flags, r)
		case *lnrpc.FeeReportResponse:
			out, err = hideFeeReport(m, flags, r)
		case *lnrpc.ListChannelsResponse:
			out, err = hideListChannels(m, flags, r)
		case *lnrpc.PolicyUpdateResponse:
			out, err = hidePolicyUpdate(m, flags, r)
		default:
			err = fmt.Errorf("no privacy mapping for %s", method)
		}
		return err
	})
	return out, err
}

func hideForwardingHistory(m *privacyMap, flags privacyFlags, r *lnrpc.ForwardingHistoryResponse) (proto.Message, error) {
	events := make([]*lnrpc.ForwardingEvent, len(r.ForwardingEvents))
	for i, fe := range r.ForwardingEvents {
		chanIn, chanOut := fe.ChanIdIn, fe.ChanIdOut
		var err error
		if !flags.has(privClearChanIDs) {
			if chanIn, err = m.hideUint64(chanIn); err != nil {
				return nil, err
			}
			if chanOut, err = m.hideUint64(chanOut); err != nil {
				return nil, err
			}
		}
		amtOut, fee := fe.AmtOutMsat, fe.FeeMsat
		if !flags.has(privClearAmounts) {
			if amtOut, err = hideAmount(amtOut); err != nil {
				return nil, err
			}
			if fee, err = hideAmount(fee); err != nil {
				return nil, err
			}
		}
		amtIn := amtOut + fee
		ts := time.Unix(0, int64(fe.TimestampNs))
		if fe.TimestampNs == 0 {
			ts = time.Unix(int64(fe.Timestamp), 0)
		}
		if !flags.has(privClearTimeStamps) {
			if ts, err = hideTimestamp(ts); err != nil {
				return nil, err
			}
		}
		events[i] = &lnrpc.ForwardingEvent{ChanIdIn: chanIn, ChanIdOut: chanOut, AmtIn: amtIn / 1000, AmtOut: amtOut / 1000,
			Fee: fee / 1000, FeeMsat: fee, AmtInMsat: amtIn, AmtOutMsat: amtOut,
			TimestampNs: uint64(ts.UnixNano()), Timestamp: uint64(ts.Unix())}
	}
	return &lnrpc.ForwardingHistoryResponse{ForwardingEvents: events, LastOffsetIndex: r.LastOffsetIndex}, nil
}

func hideFeeReport(m *privacyMap, flags privacyFlags, r *lnrpc.FeeReportResponse) (proto.Message, error) {
	fees := make([]*lnrpc.ChannelFeeReport, len(r.ChannelFees))
	for i, c := range r.ChannelFees {
		id, cp := c.ChanId, c.ChannelPoint
		if !flags.has(privClearChanIDs) {
			var err error
			if id, err = m.hideUint64(id); err != nil {
				return nil, err
			}
			if cp, err = m.hideChanPointStr(cp); err != nil {
				return nil, err
			}
		}
		fees[i] = &lnrpc.ChannelFeeReport{ChanId: id, ChannelPoint: cp, BaseFeeMsat: c.BaseFeeMsat, FeePerMil: c.FeePerMil,
			FeeRate: c.FeeRate, InboundBaseFeeMsat: c.InboundBaseFeeMsat, InboundFeePerMil: c.InboundFeePerMil}
	}
	return &lnrpc.FeeReportResponse{ChannelFees: fees, DayFeeSum: r.DayFeeSum, WeekFeeSum: r.WeekFeeSum, MonthFeeSum: r.MonthFeeSum}, nil
}

func hideListChannels(m *privacyMap, flags privacyFlags, r *lnrpc.ListChannelsResponse) (proto.Message, error) {
	channels := make([]*lnrpc.Channel, len(r.Channels))
	for i, c := range r.Channels {
		var err error
		remote := c.RemotePubkey
		if !flags.has(privClearPubkeys) {
			if remote, err = m.hideString(remote); err != nil {
				return nil, err
			}
		}
		cp, id := c.ChannelPoint, c.ChanId
		if !flags.has(privClearChanIDs) {
			if cp, err = m.hideChanPointStr(cp); err != nil {
				return nil, err
			}
			if id, err = m.hideUint64(id); err != nil {
				return nil, err
			}
		}
		initiator := c.Initiator
		if !flags.has(privClearChanInitiator) {
			if initiator, err = hideBool(); err != nil {
				return nil, err
			}
		}
		local, err := maybeHideAmount(flags, c.LocalBalance)
		if err != nil {
			return nil, err
		}
		if local > c.Capacity {
			local = c.Capacity
		}
		remoteBal := c.RemoteBalance
		if !flags.has(privClearAmounts) {
			remoteBal = c.Capacity - local
		}
		received, err := maybeHideAmount(flags, c.TotalSatoshisReceived)
		if err != nil {
			return nil, err
		}
		sent, err := maybeHideAmount(flags, c.TotalSatoshisSent)
		if err != nil {
			return nil, err
		}
		unsettled, err := maybeHideAmount(flags, c.UnsettledBalance)
		if err != nil {
			return nil, err
		}
		// Only the number of pending HTLCs is shown unless HTLCs are clear.
		htlcs := make([]*lnrpc.HTLC, len(c.PendingHtlcs))
		for j := range htlcs {
			if flags.has(privClearHTLCs) {
				htlcs[j] = stripUnknown(c.PendingHtlcs[j]).(*lnrpc.HTLC)
			} else {
				htlcs[j] = &lnrpc.HTLC{}
			}
		}
		channels[i] = &lnrpc.Channel{
			RemotePubkey: remote, ChannelPoint: cp, ChanId: id, Initiator: initiator, LocalBalance: local,
			RemoteBalance: remoteBal, TotalSatoshisReceived: received, TotalSatoshisSent: sent,
			UnsettledBalance: unsettled, PendingHtlcs: htlcs,
			Active: c.Active, Capacity: c.Capacity, CommitFee: c.CommitFee, CommitWeight: c.CommitWeight,
			FeePerKw: c.FeePerKw, NumUpdates: c.NumUpdates, CsvDelay: c.CsvDelay, Private: c.Private,
			ChanStatusFlags: c.ChanStatusFlags, LocalChanReserveSat: c.LocalChanReserveSat,
			RemoteChanReserveSat: c.RemoteChanReserveSat, StaticRemoteKey: c.StaticRemoteKey,
			CommitmentType: c.CommitmentType, Lifetime: c.Lifetime, Uptime: c.Uptime, ThawHeight: c.ThawHeight,
			LocalConstraints: cloneConstraints(c.LocalConstraints), RemoteConstraints: cloneConstraints(c.RemoteConstraints),
			ZeroConf: c.ZeroConf,
		}
	}
	return &lnrpc.ListChannelsResponse{Channels: channels}, nil
}

func cloneConstraints(c *lnrpc.ChannelConstraints) *lnrpc.ChannelConstraints {
	if c == nil {
		return nil
	}
	return stripUnknown(c).(*lnrpc.ChannelConstraints)
}

func hidePolicyUpdate(m *privacyMap, flags privacyFlags, r *lnrpc.PolicyUpdateResponse) (proto.Message, error) {
	failed := make([]*lnrpc.FailedUpdate, len(r.FailedUpdates))
	for i, u := range r.FailedUpdates {
		failed[i] = &lnrpc.FailedUpdate{Reason: u.Reason, UpdateError: u.UpdateError}
		if !flags.has(privClearChanIDs) {
			// The node's text may name the real outpoint.
			failed[i].UpdateError = u.Reason.String()
		}
		if u.Outpoint == nil {
			continue
		}
		txid, index := u.Outpoint.TxidStr, u.Outpoint.OutputIndex
		if !flags.has(privClearChanIDs) {
			var err error
			if txid, index, err = m.hideChanPoint(txid, index); err != nil {
				return nil, err
			}
		}
		failed[i].Outpoint = &lnrpc.OutPoint{TxidStr: txid, OutputIndex: index}
	}
	return &lnrpc.PolicyUpdateResponse{FailedUpdates: failed}, nil
}
