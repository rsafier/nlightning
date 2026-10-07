package main

import "google.golang.org/protobuf/encoding/protowire"

// Lightning Terminal (terminal.lightning.engineering) talks to litd, not to a
// bare lnd. After pairing it calls lnrpc GetInfo and then refuses the node unless
// the session macaroon grants /litrpc.Autopilot/ListAutopilotSessions; it then
// asks litrpc.Status for the sub-servers it may use. The bridge answers these few
// litrpc reads itself, read-only and without touching the node, so Terminal sees
// lnd running and every other sub-server off instead of failing on UNIMPLEMENTED.
const (
	litSubServerStatus       = "/litrpc.Status/SubServerStatus"
	litListAutopilotSessions = "/litrpc.Autopilot/ListAutopilotSessions"
	litListAutopilotFeatures = "/litrpc.Autopilot/ListAutopilotFeatures"
)

// litLocalMethods are the methods the bridge answers locally (when the session's
// macaroon grants them); every other method goes to the backend.
var litLocalMethods = map[string]func() []byte{
	litSubServerStatus: subServerStatusResponse,
	// Empty ListAutopilotSessionsResponse / ListAutopilotFeaturesResponse: no
	// autopilot exists here, which Terminal shows as an empty list.
	litListAutopilotSessions: func() []byte { return nil },
	litListAutopilotFeatures: func() []byte { return nil },
}

// litSubServers lists litd's sub-server names (lightning-terminal
// subservers/subserver.go and terminal.go) with whether the bridge reports them
// running. Only lnd (NLightning's LND-compatible API) and lit (this bridge's
// minimal status shim) are up; Loop, Pool, Faraday and Taproot Assets are not
// provided, so they are reported disabled and Terminal hides their pages.
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
// list grants. Older sessions lack these URIs; their calls go to the backend.
func localMethodsFor(allowed []string) map[string]func() []byte {
	granted := map[string]bool{}
	for _, m := range allowed {
		granted[m] = true
	}
	out := map[string]func() []byte{}
	for m, fn := range litLocalMethods {
		if granted[m] {
			out[m] = fn
		}
	}
	return out
}
