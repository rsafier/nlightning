# Lightning Terminal Autopilot through the LNC bridge (NL-1240)

Owner request (2026-10-07): the Autopilot page of Lightning Terminal
(terminal.lightning.engineering) should work against an NLightning node paired
through `tools/lnc`, as it does against litd. This reverses the "autopilot not
supported" note of NL-1239. This document records what litd does, what the
bridge now emulates, the security model, and what is still missing.

Sources: lightninglabs/lightning-terminal `master` (litd v0.17.6), file paths
below are relative to that repository; Terminal's production bundle
(`_next/static/chunks/pages/_app-57b499551144209a.js`, fetched 2026-10-06); and
live calls to Lightning Labs' autopilot servers on 2026-10-07.

## 1. How litd runs Autopilot

### 1.1 litrpc.Autopilot (`litrpc/lit-autopilot.proto`, `session_rpcserver.go`)

- `ListAutopilotFeatures` asks the autopilot server for its features and, per
  rule, reports `known` (litd has a rule manager for it) with the server's
  default, minimum and maximum values; `requires_upgrade` is set when a feature
  names an unknown rule or privacy flag.
- `AddAutopilotSession` (`session_rpcserver.go` AddAutopilotSession):
  validates the requested features against the server's list; for every rule
  the request sets, checks the value lies within the server's min/max
  (`rules.*.VerifySane`), fills the other rules with the server defaults; ORs
  the features' privacy flags unless the request sets them; maps restriction
  lists and feature configs to pseudonyms (`RealToPseudo`,
  `firewall.ObfuscateConfig`); creates a session of type `TYPE_AUTOPILOT` whose
  macaroon carries the feature permissions (`uri` entity) and the caveats
  `lnd-custom lit-mac-fw rules:{json}` and `lnd-custom privacy`
  (`firewall/caveats.go`) plus `time-before`; signs the new session key with the
  group's first session key when the request links to an earlier (revoked)
  group; calls the server's `RegisterSession`; stores the server's key as the
  session's remote key and starts serving it on the mailbox (a KK Noise
  session: no pairing phrase is used).
- `ListAutopilotSessions` lists the `TYPE_AUTOPILOT` sessions with
  `autopilot_feature_info` (the rules with real values) and `feature_configs`.
- `RevokeAutopilotSession` revokes locally and tells the server
  (`autopilotserver.Client.SessionRevoked`, best effort).

### 1.2 The autopilot server client (`autopilotserver/client.go`, `autopilotserverrpc/autopilotserver.proto`)

- Methods: `Terms` (minimum litd version; litd refuses to start below it),
  `ListFeatures`, `RegisterSession` (responder key, mailbox address,
  `dev_server`, obfuscated feature configs, litd and lnd versions, group key and
  link signature, privacy flags; returns the server's initiator key),
  `ActivateSession` (re-sent per session every `autopilot.pingcadence`, 1 h;
  an error containing "the client has been rejected" revokes the session) and
  `RevokeSession`.
- Servers (`terminal.go`): mainnet `autopilot.lightning.finance:12010`,
  testnet `test.autopilot.lightning.finance:12010`. **For every other network
  (signet, regtest, simnet) litd has no default and refuses to start with
  autopilot enabled unless `autopilot.address` is set** ("no autopilot server
  address specified"). Lightning Labs runs no signet autopilot server.
- Live probe (2026-10-07, both servers): `Terms` min version 0.8.0;
  `ListFeatures` returns `AutoFees` and `AutoOpen`:
  - AutoFees, privacy flags 0, config `{"version":0}`, permissions
    `ForwardingHistory`, `FeeReport`, `ListChannels` (offchain read) and
    `UpdateChannelPolicy` (offchain write); rules `history-limit` (default 60
    days, min 40, max 80), `channel-restriction`, `peer-restriction`,
    `rate-limit` (default 1000 per 72 h both ways; between 1 per 720 h and 1000
    per 24 h) and `channel-policy-bounds` (default base 0-10,000 msat, rate
    0-5,000 ppm, CLTV 60-120, HTLC 1 msat-100,000,000,000 msat).
  - AutoOpen, privacy flags 129 (clear pubkeys and network addresses),
    permissions `GetInfo`, `ConnectPeer`, `ListChannels`, `PendingChannels`,
    `ClosedChannels`, `BatchOpenChannel`, `WalletBalance`; rules
    `channel-constraint`, `on-chain-budget`, `peer-restriction`, `rate-limit`,
    `channel-policy-bounds`.
  The fixture `liveFeatures()` in `tools/lnc/autopilot_test.go` is this answer.

### 1.3 The firewall (`firewall/`, `rules/`, `firewalldb/`)

litd registers RPC middleware with lnd for macaroons that carry `lnd-custom`
caveats:

- Request logger (`firewall/request_logger.go`): records an action (pending)
  per call with the URI, the request as JSON and the meta information the
  autopilot attaches to each call as a caveat
  `lnd-custom lit-mac-fw meta:{"actor_name","feature","trigger","intent","structured_json_data"}`;
  marks it done or errored on the response. `litrpc.Firewall.ListActions`
  reads this log (`session_rpcserver.go` ListActions; filters, order and
  offset in `db/sqlc/actions_custom.go`).
- Rule enforcer (`firewall/rule_enforcer.go`): refuses calls without meta
  information, calls for a feature not in the session, and methods outside the
  feature's permissions; applies the feature's rules:
  `rate-limit` (`rules/rate_limit.go`: counts the group's and feature's read or
  write actions in the window), `history-limit` (`rules/history_limit.go`:
  `ForwardingHistory.start_time` not before the limit),
  `channel-policy-bounds` (`rules/chan_policy_bounds.go`: base fee, fee rate,
  CLTV delta, min/max HTLC of `UpdateChannelPolicy`), `channel-restriction` and
  `peer-restriction` (`rules/channel_restrictions.go`,
  `rules/peer_restrictions.go`: no update on a denied channel or a denied
  peer's channel; no global update while a channel list is set).
- Privacy mapper (`firewall/privacy_mapper.go`, `firewalldb/privacy_mapper.go`):
  per session group, real node keys, channel IDs and channel points are
  replaced by random pseudonyms (stored pairs, so they are stable), amounts
  vary by up to 5 % and timestamps by up to 10 minutes on every response, the
  channel initiator is random, pending HTLCs are reduced to their count, and
  every field it does not list is dropped; requests are mapped back (pseudo to
  real). `PrivacyMapConversion` exposes the map to the owner.

### 1.4 What AutoFees does over its session

It calls `ListChannels`, `FeeReport` and `ForwardingHistory` (bounded by the
history limit) to read pseudonymized channels, current fees and forwarding
history, decides per channel, and calls `UpdateChannelPolicy` for one channel
point at a time within the policy bounds, attaching the meta caveat
(`actor_name` "Autopilot", `feature` "AutoFees", the old fees in
`structured_json_data`).

### 1.5 What Terminal needs (bundle)

- `listAutopilotFeatures`, `listAutopilotSessions`, `addAutopilotSession`
  (features `{AutoFees: {rules, config: base64 {"version":0}}}`,
  `mailboxServerAddr` `mailbox.terminal.lightning.today:443`, `linkedGroupId`
  of the previous group when settings change, Terminal's own policy bounds and
  channel/peer deny lists from its per-channel AutoFees toggles),
  `revokeAutopilotSession`.
- `firewall.listActions({actorName: "Autopilot", featureName: "AutoFees",
  methodName: "/lnrpc.Lightning/UpdateChannelPolicy", state: STATE_DONE,
  reversed: true, ...})`; it parses `rpc_params_json` as
  `{chan_point: {funding_txid_str, output_index}, base_fee_msat, fee_rate}` with
  real channel points (it matches them to its channels) and
  `structured_json_data` for the old fees.
- `SubServerStatus`: Terminal reads only `lit`, `lnd`, `loop` and
  `taprootAssets`; litd has no separate autopilot or firewall entry.
- The session status badge ("Running", "Pending") comes from the autopilot
  server's REST API, called by the browser:
  `https://autopilot.lightning.finance:12010/v1/autopilotserver/sessions/<key>`
  on mainnet, Lightning Labs' staging host on testnet, and
  `https://localhost:11110` on any other network. On signet the badge
  therefore stays "Pending" whatever the node does.

## 2. What the bridge implements

| Piece | File | Behaviour |
|---|---|---|
| litrpc handlers | `tools/lnc/lit.go`, `autopilot.go` | `ListAutopilotFeatures` (the server's, `requires_upgrade` when the bridge cannot run a feature: AutoOpen), `AddAutopilotSession`, `ListAutopilotSessions`, `RevokeAutopilotSession`, `Firewall.ListActions`; never forwarded to the node |
| Server client | `autopilot_server.go` | `Terms`, `ListFeatures`, `RegisterSession`, `ActivateSession`, `RevokeSession` over TLS; `serve --autopilot-server` (mainnet, testnet or host:port) |
| Autopilot sessions | `autopilot.go`, `session.go`, `serve.go` | stored like other sessions (`type: autopilot`), served on the mailbox for the server's key (KK), hourly activation, revocation reported until the server acknowledges |
| Firewall | `firewall.go` | credential check, meta caveat, feature/method authorization, rules, privacy mapping, action log; nothing reaches the node unmapped or unchecked |
| Rules | `rules.go` | litd's JSON and proto forms, `VerifySane`, enforcement |
| Privacy map | `privacy.go` | per group, persisted `privacy-<group>.json` (0600) |
| Action log | `actions.go` | persisted `actions.json` (0600, last 100,000 actions); ListActions with litd's filter, order, offset and count semantics |
| Autopilot macaroon | `main.go` `autopilot-init` | one backend macaroon with exactly the four AutoFees methods, baked once with the admin macaroon (`autopilot.json`, 0600); `--rotate` replaces it |

### 2.1 Security model

- **No admin credential in `serve`.** `autopilot-init` (run once by the
  operator with the admin macaroon) bakes the autopilot macaroon: `uri`
  permissions for `ForwardingHistory`, `FeeReport`, `ListChannels`,
  `UpdateChannelPolicy` only, on a root key of its own. Each autopilot session's
  backend macaroon is that macaroon plus `time-before <expiry>` (attenuated
  without the root key). Even a bridge bug cannot let an autopilot session reach
  any other node method: the node checks the macaroon.
- **The client never holds a node credential.** The autopilot server receives
  (in the Noise auth data, as litd sends it) the backend macaroon plus litd's
  rules and privacy caveats. Its signature chain cannot be shortened back to the
  backend macaroon, and the node refuses its `lnd-custom` caveats, so it is
  useless against the node directly. The bridge accepts it only through
  `autopilotAuthenticator`: the presented macaroon must be exactly the client
  macaroon with appended first-party caveats whose signature the bridge
  recomputes; the only caveats it may append are one meta caveat and
  `time-before` (not passed). Third-party and unknown caveats are refused.
- **Local only.** An autopilot session's proxy has `LocalOnly`: every granted
  method runs in the firewall; everything else is `PermissionDenied`. User
  sessions never have litrpc calls forwarded either.
- **Per call** (`firewall.call`): session not expired; meta caveat present; its
  feature subscribed by the session; the method in that feature's permissions;
  the request decodes and carries no field unknown to the bridge's lnrpc
  (v0.19) types; pseudonyms map back (an unknown one is refused); the action is
  logged; rules pass; the node is called with the session's backend macaroon;
  the response is rebuilt field by field with pseudonyms and fuzzing; the
  node's error text is replaced by its code (it can name real channels).
- **Who can start an autopilot.** `AddAutopilotSession` and
  `RevokeAutopilotSession` are granted to `--profile wallet` sessions only;
  `ListAutopilotSessions`, `ListAutopilotFeatures` and `ListActions` to both
  profiles.

### 2.2 Rules enforced

| Rule | Enforcement in the bridge |
|---|---|
| Feature/method | method must be in the feature's server-declared permissions and in the autopilot macaroon |
| `rate-limit` | as litd: the group's and feature's earlier reads (or writes) in the last `num_hours`, refused calls included, must be fewer than `iterations`; `ResourceExhausted` otherwise |
| `history-limit` | `ForwardingHistory.start_time` must be at or after now - duration (or the start date); `start_time` 0 is refused |
| `channel-policy-bounds` | as litd: base fee, fee rate (`fee_rate` or `fee_rate_ppm`), CLTV delta, min HTLC (when specified), max HTLC; stricter: negative base fee refused, inbound fee bounded to +/- the max base and max rate (the node refuses non-zero inbound fees anyway) |
| `channel-restriction` | the update's (real) channel point is looked up with `ListChannels`; a denied channel ID is refused; an unknown channel point is refused (litd asks to retry) |
| `peer-restriction` | same lookup; a denied peer's channel is refused |
| Stricter than litd | global-scope `UpdateChannelPolicy` and `create_missing_edge` always refused; `no_privacy_mapper`, `dev_server`, session-wide rules and another mailbox than the bridge's relay are refused at `AddAutopilotSession` |

### 2.3 Not enforced or not implemented

- **AutoOpen** (`BatchOpenChannel` is not implemented by the node; its rules
  `channel-constraint` and `on-chain-budget` are not implemented here): reported
  `requires_upgrade`, refused at `AddAutopilotSession` with `UNIMPLEMENTED`.
- **No signet autopilot server.** Without `--autopilot-server` the bridge
  returns an empty feature list (the page loads with nothing to enable) and
  `AddAutopilotSession` fails with `FailedPrecondition`. `--autopilot-server
  testnet` on a signet node registers with Lightning Labs' testnet server; that
  server cannot tell the chain (it only sees pseudonyms) but this use is not
  sanctioned by Lightning Labs and was not exercised in this change. Terminal's
  session badge stays "Pending" on signet (it asks localhost).
- **Not run live.** The server protocol, registration, the KK session and the
  firewall are proven in-process (`autopilot_test.go`); no session was
  registered with Lightning Labs' servers from this change (that registers a
  real session with a third party; the operator decides).
- **`PrivacyMapConversion`**, `ListSessions`/`AddSession` of `litrpc.Sessions`
  and accounts are not implemented (Terminal does not call them on the
  Autopilot page).
- **Unknown request fields.** The bridge's lnrpc types are LND v0.19's; a newer
  autopilot that sends fields added later (for example newer
  `ForwardingHistoryRequest` filters) is refused with `InvalidArgument` rather
  than passed on unchecked.
- **Privacy map growth.** Pseudonym pairs are never pruned (litd neither).

## 3. Deploy

1. Rebuild the bridge (`go build -o nltg-lnc .` in `tools/lnc`). No node change.
2. Once, with the admin macaroon: `nltg-lnc autopilot-init --state-dir ...
   --backend ... --tls-cert ... --admin-macaroon ...`.
3. Restart `serve` with `--autopilot-server mainnet` (mainnet node) or
   `testnet`, or an explicit `host:port` (`--autopilot-tls-cert` for a private
   CA). Without the flag the page loads with no features.
4. Create a new `--profile wallet` session and pair Terminal again: sessions
   made before this change lack `ListActions` and the autopilot write grants.
