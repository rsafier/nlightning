# VLS infrastructure

Experimental fresh-key regtest backend, selected with `Signing:Mode=Vls`.
No legacy key/channel migration and no native signer fallback.

- `VlsGatewayTransport` uses bounded authenticated newline-delimited JSON over Unix sockets. No implicit retries. Node credentials and trusted payment-approval credentials belong to separate callers/sockets.
- `VlsSignerConnection` preserves exact JSON result bytes for durable workflows. Saved envelopes contain request IDs and commands, never credentials. Reconciliation is read-only and must not execute.
- `VlsChannelMappingRegistry` reserves mappings before allocation RPCs. VLS IDs are 41 bytes (peer public key + little-endian dbid), distinct from BOLT IDs. Existing allocation receipts and signer identity/network must match exactly.
- `VlsSigningWorkflowCoordinator` captures semantic state changes in node persistence. Completed node requests require an unchanged signer receipt on replay; uncertainty blocks. Application state saves consume workflows atomically.
- `VlsLightningSigner` uses policy-aware setup, commitment signing/validation, activation, revocation, close, and funding APIs. Generic digest/channel signing is refused. Peer funding-signature verification is public-only and remains available for cooperative-close negotiation. Opening and normal commitment paths explicitly use the Domain VLS ports.
- Wallet derivation uses stock Native VLS account m/0/0, one nonhardened child. Receive/change metadata map to child 2*index+change. Only P2WPKH is supported. Public derivation is cached locally from the VLS account xpub; no private keys are exported.
- Force-close witnesses require sorted funding keys and independent verification of both signatures against the exact host transaction and funding amount. Historical FundingSatoshis is actually millisatoshis; divide by 1000 for Bitcoin values.
- The gateway persists VLS channel and NodeState policy changes, safety metadata and immutable receipts in one Redb transaction before reply. Canonicalize hex channel IDs before safety-metadata lookup; mixed-case strings must not bypass invalidation.

Dust HTLC exposure is gated to zero before offers reach the commitment engine: pinned VLS phase2 consumes actual nondust outputs and cannot account settled trimmed payments safely. Never weaken balance enforcement or pass dust entries into phase2 to bypass this limit.

- `VlsLightningSigner.Onchain.cs` (NL-1320): BOLT 5 resolution through VLS's semantic signers (`sign_holder_htlc`, `sign_delayed_sweep`, `sign_counterparty_htlc_sweep`, `sign_justice_sweep`, `sign_to_remote_sweep`; operation codes `VlsOnchainOperations` 2100-2104). HTLC transactions and delayed sweeps only for the commitment signed for broadcast; every sweep pays one VLS wallet child path (`FindWalletPath`). Not node-owned workflows (NL-1321); VLS sees no blocks, keep sweep locktimes at 0 or `cltv_expiry` (NL-1322).

Unsupported: anchors lifecycle (the gateway refuses anchors on-chain resolution), taproot, splicing, dual funding, BOLT12, node auxiliary encryption and general wallet spending. Native code is not an acceptable substitute. Wallet funding and close operations have signer receipts but do not yet have complete node-owned request-ID crash workflows. See docs/agents/VLS_NODE_MVP.md for acceptance scope and limitations.

Validation (root orchestrates .NET builds/format to avoid concurrent MSBuild conflicts): `tools/vls-gateway/run.sh test` builds pinned Rust and runs the actual-process proof. Set `NLTG_VLS_GATEWAY_BINARY` to that binary before selecting explicit `VlsGatewayProcessTests`, `VlsChannelHarnessTests`, and `VlsNodeWorkflowCrashTests` in RemoteSigning.Tests. Live acceptance lives in Integration.Tests/Cluster/Live/VlsSignerLndClusterTests. Use the repository Release build and format gates.
