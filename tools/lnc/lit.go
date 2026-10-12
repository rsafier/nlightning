package main

import (
	"context"

	"google.golang.org/protobuf/encoding/protowire"
)

// Lightning Terminal (terminal.lightning.engineering) talks to litd, not to a
// bare lnd. After pairing it calls lnrpc GetInfo and then refuses the node unless
// the session macaroon grants /litrpc.Autopilot/ListAutopilotSessions; it then
// asks litrpc.Status for the sub-servers it may use, and its Autopilot page
// drives litrpc.Autopilot and reads litrpc.Firewall.ListActions. The bridge
// answers these litrpc calls itself (never the node): Status and the autopilot
// lists for every Terminal session, adding and revoking autopilot sessions for
// sessions granted it (autopilot.go).
const (
	litSubServerStatus        = "/litrpc.Status/SubServerStatus"
	litListAutopilotSessions  = "/litrpc.Autopilot/ListAutopilotSessions"
	litListAutopilotFeatures  = "/litrpc.Autopilot/ListAutopilotFeatures"
	litAddAutopilotSession    = "/litrpc.Autopilot/AddAutopilotSession"
	litRevokeAutopilotSession = "/litrpc.Autopilot/RevokeAutopilotSession"
	litListActions            = "/litrpc.Firewall/ListActions"
)

// litLocalHandlers are the litrpc methods the bridge answers. With no
// autopilot service the autopilot lists are empty and the rest fail with
// FailedPrecondition.
func litLocalHandlers(a *autopilotService) map[string]LocalHandler {
	out := map[string]LocalHandler{
		litSubServerStatus: func(context.Context, CallInfo, []byte) ([]byte, error) { return subServerStatusResponse(), nil },
	}
	if a == nil {
		empty := func(context.Context, CallInfo, []byte) ([]byte, error) { return nil, nil }
		out[litListAutopilotSessions] = empty
		out[litListAutopilotFeatures] = empty
		out[litListActions] = empty
		return out
	}
	out[litListAutopilotSessions] = a.listSessions
	out[litListAutopilotFeatures] = a.listFeatures
	out[litAddAutopilotSession] = a.addSession
	out[litRevokeAutopilotSession] = a.revokeSession
	out[litListActions] = a.listActions
	return out
}

// litSubServers lists litd's sub-server names (lightning-terminal
// subservers/subserver.go and terminal.go) with whether the bridge reports them
// running. Only lnd (NLightning's LND-compatible API) and lit (this bridge's
// litd emulation, which includes autopilot and the firewall) are up; litd has
// no separate status entry for autopilot or the firewall. Loop, Pool, Faraday,
// Taproot Assets and accounts are not provided, so they are reported disabled
// and Terminal hides their pages.
var litSubServers = []struct {
	name    string
	running bool
}{
	{"lit", true},
	{"lnd", true},
	{"loop", false},
	{"pool", false},
	{"faraday", false},
	{"taproot-assets", false},
	{"accounts", false},
}

// subServerStatusResponse encodes litrpc.SubServerStatusResp
// (litrpc/lit-status.proto): map<string, SubServerStatus> sub_servers = 1, with
// SubServerStatus { bool disabled = 1; bool running = 2; string error = 3;
// string custom_status = 4; }.
func subServerStatusResponse() []byte {
	var out []byte
	for _, s := range litSubServers {
		var status []byte
		if !s.running {
			status = protowire.AppendTag(status, 1, protowire.VarintType)
			status = protowire.AppendVarint(status, 1)
		} else {
			status = protowire.AppendTag(status, 2, protowire.VarintType)
			status = protowire.AppendVarint(status, 1)
		}
		var entry []byte
		entry = protowire.AppendTag(entry, 1, protowire.BytesType)
		entry = protowire.AppendString(entry, s.name)
		entry = protowire.AppendTag(entry, 2, protowire.BytesType)
		entry = protowire.AppendBytes(entry, status)
		out = protowire.AppendTag(out, 1, protowire.BytesType)
		out = protowire.AppendBytes(out, entry)
	}
	return out
}

// localMethodsFor returns the locally answered methods the session's permission
// list grants. Older sessions lack these URIs; their litrpc calls are refused.
func localMethodsFor(all map[string]LocalHandler, allowed []string) map[string]LocalHandler {
	granted := map[string]bool{}
	for _, m := range allowed {
		granted[m] = true
	}
	out := map[string]LocalHandler{}
	for m, fn := range all {
		if granted[m] {
			out[m] = fn
		}
	}
	return out
}
