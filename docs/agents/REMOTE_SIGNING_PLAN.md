# Remote signing research and implementation plan

Research date: 2026-10-07. Base: `wip/fafo`, `525347f6c0d6ed8bf0ce4d451e11e49265ccbed5`.
Status: native remote-signing MVP and durable request reconciliation implemented on `wip/remotesigner`; a pinned VLS core compatibility spike is available. Nitro deployment and the production VLS adapter remain planned work.

## FAFO compatibility policy

Owner decision, 2026-10-07: this is the FAFO edition and the owner is the only user. Backward compatibility with prior NLightning versions is not required. The VLS track starts with fresh VLS-derived identities, wallets and channels; custom legacy key derivation, existing-channel import and historical state migration are out of scope. Stock VLS channel-key differences are research findings, not delivery blockers. Safety and durable recovery for channels created by the selected backend remain required.

## Recommendation

Extract the existing C# signing and key operations into a small remote service first, initially on a separate hardened VM. Keep its API typed around Lightning operations, with explicit capabilities and signer-owned durable state. Then package that service for AWS Nitro Enclaves. Develop VLS as another backend behind the same application contracts, starting with new static-remotekey and zero-fee anchors channels.

These are separate choices: remote hosting isolates keys; VLS validates protocol and spending policy. VLS can also run inside Nitro. A remote version of `LocalLightningSigner` preserves our current feature coverage and derivation, but does not automatically protect funds from a compromised node that submits malicious requests. Its existing checks trust node-provided channel state, commitment advancement and wallet reservations. Describe the initial security guarantee as key isolation, not complete host-compromise resistance.

Avoid designing the public service as `SignHash(key, digest)` or exposing private derivation. Preserve local mode, introduce `remote-native` and `vls` as explicit modes, and never fall back to a local private key when a remote backend fails.

## MVP implementation

The prototype consists of `NLightning.Signing.Contracts`, `NLightning.Infrastructure.RemoteSigning` and `NLightning.Signer`. `Signing:Mode=RemoteNative` selects remote adapters before node key-file loading; local mode remains the default. The daemon forwards all current `ILightningSigner` operations through explicit typed dispatch, and its `ISecureKeyManager` facade refuses private-key export. Invoice signing, public wallet derivation, backup/peer-storage encryption and offer path IDs now have safe operation boundaries. See [signer launch instructions](../../src/NLightning.Signer/README.md).

The local transport is gRPC/HTTP2 over a private Unix socket, authenticated by a random token file. It verifies the configured network and optionally pins the node public key. Requests have version/operation IDs, bounded payloads and deadlines. A single service instance serializes access; network outages stay distinct from signing refusals, with no local fallback or automatic retry after an unknown outcome. Wallet snapshots include current reservations and are replaced for each wallet-signing call.

An append-only, fsynced journal restores channel/funding guards, sticky data-loss/broadcast state and retirement tombstones. Consumed taproot/close/splice nonce outcomes are bound to the public nonce and exact payload, allowing identical replay while refusing conflicting reuse after restart. Seed-injection mode also persists a public identity/network-bound key-index journal before allocating keys. These files protect ordinary restarts; a malicious host can still roll them back together.

The broader requirements below remain the production roadmap. The MVP has no Nitro packaging, attestation, network TLS endpoint, external freshness authority, distributed writer fencing, VLS adapter or feature-capability negotiation. Supported safety mutations, nonce-consuming signatures and key-index allocations now retain bounded durable request receipts. `Prepare` / `Execute` / `Reconcile` expose explicit outcome recovery; transport failures retain the exact envelope. Separate key-index persistence has a write-ahead unknown marker. Ephemeral nonce creation and other unsupported operations cannot be reconciled across restart. The node does not yet persist these envelopes or automatically reconcile its database transitions; that application integration remains a gate. Receipts fail closed at capacity and have no safe online compaction procedure yet. Signing authorization still trusts node-supplied channel state and wallet reservations, and public verification currently incurs remote calls. Do not use this prototype as a production funds-protection boundary.

## Boundaries on the base branch

| Code | Useful boundary or required change |
| --- | --- |
| `src/NLightning.Domain/Bitcoin/Interfaces/ILightningSigner.cs` | Main signing boundary, synchronous; covers channel creation, commitments, HTLCs, wallet/funding, sweeps, anchors, splice fundings, BOLT 12, taproot/MuSig2 and gossip v2. Some methods return mutated transaction objects or use `out` parameters; transport DTOs must encode those results explicitly. |
| `src/NLightning.Infrastructure.Bitcoin/Signers/LocalLightningSigner*.cs` | Reuse crypto and guards for the native backend. Dependencies include key manager, UTXO/reservation state, funding builder and lazy channel-state lookup. This is more than moving a private-key buffer. |
| `src/NLightning.Domain/Protocol/Interfaces/ISecureKeyManager.cs` | Currently exports node private key and channel/wallet xprvs. Split private operations from public identity, wallet descriptors and birth height; do not implement a remote proxy that returns these secrets. |
| `src/NLightning.Infrastructure/Node/Factories/PeerServiceFactory.cs` | BOLT 8 already injects `ComputeNodeSharedSecret`; Sphinx and route blinding use the same seam. Node-key ECDH can be remote without exporting the node secret. Ephemeral transport keys remain local. |
| `src/NLightning.Bolt11/Models/Invoice.cs:Encode()` | Still copies the node private key. Introduce recoverable invoice signing of the actual HRP and data, then assemble and verify the encoded invoice locally. |
| `src/NLightning.Infrastructure.Bitcoin/Wallet/BitcoinWalletService.cs` | Derives private keys just to generate addresses. Use account xpubs/descriptors and public derivation; retain database address reservations. |
| `ChannelBackupService.DeriveKey`, `PeerStorageCipher.DeriveKey`, `OfferPathIds` | Still access the node secret for encryption/HMAC. Replace with narrowly scoped seal/open and path-ID operations, using backend-specific formats and derivation; prior-version compatibility is not required. Do not export the node key to satisfy these consumers. |
| `src/NLightning.Daemon/Program.cs`, `Extensions/NodeServiceExtensions.cs`, `Services/NltgDaemonService.cs` | Startup creates/loads a concrete `SecureKeyManager`; configuration and index reconciliation also assume it. Add backend selection before key-file initialization and a public metadata/index-reservation abstraction. |
| `src/NLightning.Infrastructure.Bitcoin/DependencyInjection.cs` | Unconditionally installs `LocalLightningSigner`; replace with explicit backend registration. |

The existing native schemes are reference behavior, not a backward-compatibility contract: legacy v1/v2 files use the node secret as master with the genesis chain code; v3 uses BIP32 with node path `m/1017'/0'/6'/0/0`. Channel keys use `m/6425'/0'/0'/0/index` and hardened child roles. Fresh VLS deployments use stock VLS derivation. Once a backend creates channels, ordinary restart/recovery must retain their keys and safety history; the FAFO policy does not permit resetting active-channel state.

Current signing-state recovery reads the node database through `IChannelSigningInfoSource`. `AdvanceLocalCommitment` accepts the caller's number, and `RevealPerCommitmentSecret` trusts that advancement. `ChannelStateTransitionService` currently saves the transition before advancing/revealing. Preserve this crash ordering, but do not mistake a node's “saved” RPC for independent validation against a compromised node.

## Native service design

Proposed projects: `NLightning.Signing.Contracts` for versioned wire DTOs, `NLightning.Infrastructure.RemoteSigning` for adapters/transports, and `NLightning.Signer` for the independent executable. Keep Domain BCL-only. The signer executable should compose crypto/builders plus signer storage, without node gossip, peer handlers, general IPC, payment processor or arbitrary database access.

The same-machine MVP uses authenticated gRPC/HTTP2 over an owner-only Unix domain socket. A replaceable stream connector keeps a future vsock transport independent of signing operations. A networked VM deployment needs mTLS ending at the signer. Private networking does not replace client authorization. Bind requests to provisioned node identity, network, signer instance/epoch, operation ID, channel and funding IDs; bound lengths, deadlines, concurrent calls and error responses. Separate operator provisioning/policy APIs from the node's signing credential.

Initial RPC families:

- Public identity, descriptors, birth height, protocol version and supported capabilities.
- Durable channel-key index allocation/reservation and public basepoints/commitment points.
- Channel setup, validated local/counterparty commitment operations, revocation acceptance/release, sticky data-loss and broadcast state, funding registration/locking.
- Funding, wallet, HTLC, sweep, penalty and anchor operations with full transactions, input positions, amounts, scripts and derivation hints. Wallet state/reservations must be supplied explicitly or maintained by the signer; `IUtxoMemoryRepository` will no longer be shared memory. Host-supplied reservations alone are not spend authorization.
- Node ECDH, typed gossip/liquidity/accounting signatures, recoverable BOLT 11 signatures, BOLT 12 signatures/path IDs, backup and peer-storage encryption.
- MuSig2 nonce creation/consumption and typed partial-signature operations for the native backend. Never transport secret nonces.

Keep public signature verification, transaction construction where safe, invoice assembly and public MuSig aggregation local. For compatibility, existing synchronous interfaces can initially use a bounded dedicated RPC worker; measure channel-lock hold times and avoid blocking the transport's own event loop. Subsequently introduce async application ports for operations that need network waits. Audit callers before choosing how transient unavailability is surfaced: a generic `SignerException` must not accidentally trigger a force close for every short outage.

### Signer state and crash consistency

Give the signer its own durable record of allocated indexes, registered keys/fundings, validated commitments, revocation progress, broadcast/data-loss marks, nonce consumption and request outcomes. Node DB state can bootstrap or reconcile under explicit rules; it must not overwrite newer signer state or clear sticky safety marks. `UnregisterChannel` must not erase durable security history and allow resurrection with older state.

For mutating requests, bind the operation ID to a canonical payload digest. Commit state and the response before returning signatures or revocation secrets. An identical retry returns the stored response; another payload with the same ID is refused. A timeout means unknown outcome: query/retry the operation rather than create another nonce or rerun a signing action with changed data.

There is no shared transaction between the signer store and node DB. Define recoverable pending stages for each commitment/revocation/funding operation. The signer first validates and durably records the new safe state; the node then saves its transition before sending wire messages or releasing revocations. After a crash between stores, reconcile the pending operation without rolling either side backward. Prove both node-ahead and signer-ahead cases with injected failures.

MuSig2 needs additional treatment: a deterministic verification nonce must be bound to the exact session/message context, and durable records must prevent a second conflicting use after restart. Ephemeral close/splice nonce handles bind to a signer epoch. On enclave restart, invalidate unused handles and restart the negotiation; for already consumed handles return the persisted outcome. Concurrent writers and cloned signer instances must be fenced before they can sign.

## AWS Nitro deployment

### Provisioning pattern from Eclair

[ACINQ's deployment description](https://acinq.co/blog/securing-a-100M-lightning-node) runs Eclair itself inside Nitro. Its immutable launcher verifies a signed application package, then establishes secure connections to a master node to obtain application secrets. Network and database access pass through proxies because enclave storage is ephemeral. This supports separating secret provisioning from image construction and application execution. It is a full-node enclave design; extracting only our signer preserves a different trust boundary.

Our prototype implements that separation with `--seed-stdin --state-file`: a provisioning process supplies a 32-byte BIP32 seed directly to signer memory on every start. No private key file is read or created. Public identity, allocated key indexes and signing state persist separately. This proves the provisioning boundary locally; it does not authenticate a provisioning peer or attest an image. An attested master service or KMS recipient-attestation client can replace stdin provisioning later. Both still need authenticated state freshness and writer fencing.

### Deployment requirements

A normal Nitro-based EC2 VM is not a Nitro Enclave. For protection from parent-instance root, run key handling, validation, policy, authorization and nonce/state checks inside the enclave. The parent relays traffic and stores ciphertext. Enclaves have no external network or persistent storage; their external communication passes through vsock. [AWS concepts](https://docs.aws.amazon.com/enclaves/latest/user/nitro-enclave-concepts.html).

Build a measured enclave image and release the encrypted seed/data key through KMS recipient attestation restricted to approved measurements and the intended IAM role. The attestation public key ensures the decrypted material is readable only inside the enclave. Check the entire effective KMS authorization policy so a separate unconditioned grant cannot decrypt outside the enclave. Reject debug enclave measurements. Plan approved-image upgrades and revocation of obsolete images. [AWS KMS integration](https://docs.aws.amazon.com/enclaves/latest/user/connect-enclave-kms.html).

Terminate client authentication/encryption inside the enclave, or use an attestation-bound authenticated channel. A TLS endpoint only on the parent would let a compromised parent substitute requests. Forward KMS, storage and chain-service traffic through tightly scoped parent relays; protect application data end to end.

Store encrypted, authenticated signer snapshots/journal outside the enclave. Encryption and MACs detect modification, but do not detect replay of an older valid snapshot. Require a monotonic state authority outside the compromised parent, atomic conditional updates, authenticated freshness and a fencing epoch checked at commit. A trusted storage service can serve this role only under an explicit threat assumption that it and its authorization cannot be rolled back by the parent. Attestation and KMS seed release alone do not provide this guarantee. Start with one active signer; failover is conditional on obtaining the current fenced epoch and state.

Provision/import keys directly into the approved signer through an authenticated encrypted administrative path. Keep separately controlled recovery material. For existing nodes, inventory and retire old unlocked processes, key files and backups as part of cutover; isolation of a new copy does not invalidate old key copies. Availability still depends on the parent/network/KMS/storage, so recovery and deadline monitoring remain necessary.

## VLS compatibility and integration

Canonical VLS source inspected and exercised at `cb8a64c71d3b214951e752281f05b9090e77f074`; pin and re-evaluate before implementation. The [compatibility spike](VLS_COMPATIBILITY_SPIKE.md) runs the real core validator for static-remotekey and zero-fee anchors, validates and independently verifies commitment signatures, checks revocations, and rejects unauthorized HTLCs. It uses in-memory persistence and artificial funding fixtures; it is not a protocol gateway or live VLS payment proof. The GitHub repository describes itself as a lazy mirror. Published instructions demonstrate LDK through `lnrod`, but NLightning is an independent C# implementation and needs a protocol adapter. [LDK guide](https://vls.tech/docs/v0.14.0/get-started/ldk-vls/), [node protocol guide](https://vls.tech/docs/v0.14.0/get-started/newnodeintegration/).

| Operation | Assessment at inspected revision |
| --- | --- |
| Static-remotekey and zero-fee anchors commitments | Protocol includes channel setup, commitment signing/validation and revocation validation. Map full semantic state rather than forward our raw transaction signing call. |
| BOLT 8/4 ECDH, BOLT 11, gossip and message signatures | Existing protocol families; verify exact hash domains, signature encoding and route-blinded ECDH behavior against our vectors. |
| Wallet/funding, HTLC claims, penalties and anchors | Protocol messages exist, including `SignWithdrawal`, `SignAnyPenaltyToUs` and `SignAnchorspend`. Implement complete on-chain coverage in our adapter; the LDK adapter's TODOs do not establish that the core lacks these operations. |
| BOLT 12 | Protocol includes invoice signing and other BOLT 12 paths. Our transient payer/blinded keys and offer-path IDs need a separate mapping/derivation review, not a claim of drop-in compatibility. |
| Splicing, concurrent fundings and rotated funding keys | Blocked for stock integration: `SignSpliceTx` handler is `unimplemented!()` and is not advertised. A protocol message definition is not implemented support. |
| Dual funding and interactive RBF | No acceptance assumed. Prove beneficial-value policy, both roles, multiple inputs and attempt recovery before advertising. |
| Simple taproot/MuSig2 and gossip v2 | No corresponding NLightning operation family found in inspected wire protocol. Treat as unsupported until an upstream extension/custom backend is demonstrated. P2TR wallet support is a separate question from taproot Lightning channel support. |
| Existing NLightning key/state migration | VLS LND style can match NLightning v3 node identity on mainnet with the same BIP32 seed, but regtest node identity differs and no stock style matches NLightning channel derivation. Node identity equality does not migrate channels or wallet state. Use fresh VLS-backed identities and channels. Prior-version key/state migration is out of scope under the FAFO policy; this mismatch is not an adapter blocker. |
| Backup/peer storage, offer path IDs, accounting and liquidity signatures | NLightning-specific contracts require explicit supported extensions or redesigned scoped secrets. Do not route them through an unrestricted signing escape hatch. |

Evidence: pinned [protocol messages](https://gitlab.com/lightning-signer/validating-lightning-signer/-/blob/cb8a64c71d3b214951e752281f05b9090e77f074/vls-protocol/src/msgs.rs), [handler](https://gitlab.com/lightning-signer/validating-lightning-signer/-/blob/cb8a64c71d3b214951e752281f05b9090e77f074/vls-protocol-signer/src/handler.rs), [derivation styles](https://gitlab.com/lightning-signer/validating-lightning-signer/-/blob/cb8a64c71d3b214951e752281f05b9090e77f074/vls-core/src/signer/derive.rs).

Prefer a C# adapter to the VLS protocol through an authenticated transport to `vlsd` or a small Rust protocol gateway. A gateway can use VLS's existing codec and handlers, avoiding an FFI dependency in the node. It must preserve validation and error semantics. Start VLS on a separate VM with durable storage; package for Nitro only after remote state semantics are proven. Evaluate `vls-persist`/Lightning Storage Server rather than inventing a parallel persistence layer, but audit freshness, authenticated commits and cloned-writer fencing for the enclave threat model.

VLS needs semantic hooks above today's low-level `ILightningSigner`: counterparty commitment specifications, local commitment signatures/HTLCs, peer revocations, approved invoices/keysend and chain state. `CommitmentSigningService`, `EngineCommitmentSignerPort` and `ChannelStateTransitionService` are the main application integration points. Map commitment numbering and byte order explicitly, and supply validated setup data including peer basepoints, delays, funding and channel type.

Compute negotiated features from node support intersected with signer capabilities. In VLS mode, suppress unsupported splice/dual-fund/taproot/gossip features and refuse unsupported persisted channels at startup. Scope the first usable VLS milestone to ordinary ECDSA channels and BOLT 11; enable offers and other extensions only after their full flows pass. Unsupported features must fail before channel negotiation, not at a signing TODO.

Do not equate VLS policy enforcement with full recovery autonomy. Its current README documents remaining signer-state-loss recovery and chain-tracking limitations, and incomplete counterparty-close/breach disaster recovery. Verify recovery on our integration and keep independent chain/deadline monitoring and a tested remedy path. [Canonical limitations](https://gitlab.com/lightning-signer/validating-lightning-signer/-/blob/cb8a64c71d3b214951e752281f05b9090e77f074/README.md), [heartbeat model](https://vls.tech/docs/v0.14.0/security/heartbeat/).

## Implementation sequence and acceptance gates

Current sprint: [node recovery](REMOTE_SIGNING_RECOVERY.md) covers normal commitment
signing, taproot reconnect signing and post-save revocation release. The separate
[VLS gateway](../../tools/vls-gateway/README.md) exercises authenticated semantic
commands and transactional policy-state/receipt persistence. Neither closes the
broader on-chain, deployment fencing or live VLS adapter acceptance gates below.
The branch incorporates FAFO `dd598216` (PR #32); no legacy compatibility is required.

1. **Contracts and capability spike.** Inventory every secret consumer; introduce public identity/wallet metadata, invoice signing and scoped auxiliary operations; define operation IDs/error categories and capability negotiation. Implement a small VLS regtest spike for setup, payment and revocation to validate the protocol mapping before committing to its service architecture.
2. **Native remote prototype.** Extract signer execution and state dependencies, add VM transport and explicit DI/startup mode, replace remaining secret exports. Keep native derivation and all currently enabled features. Acceptance: node process starts without a master/node key or xprv, invoices/payments work, wallet addresses match local mode, and backup/restore works through remote operations.
3. **Durable state and failures.** Add signer journal, exact retry outcomes, pending cross-store reconciliation, sticky guards and epoch fencing. Acceptance: injected crashes at every persistence/response boundary, stale registrations/snapshots, duplicate/mismatched requests and two active writers cannot cause revocation mistakes or nonce reuse. Prove force-close/HTLC/penalty/anchor recovery with signer/node restarts.
4. **Nitro packaging.** Measured image, KMS provisioning, enclave-side authentication, vsock relays and authenticated external storage. Acceptance on real Nitro hardware: unauthorized images/debug mode cannot obtain key material; parent cannot alter authenticated requests or replay state; full restart recovers the latest state; revoked image and expired writer fail closed. A container prototype cannot prove these properties.
5. **VLS supported subset.** Complete semantic adapter, payment authorization, capability gates and all ECDSA on-chain resolution paths. Acceptance: static-remotekey/anchors interoperability, send/receive/routing, reconnection, mutual/force close, HTLC success/timeout, penalties and CPFP; rejected malicious commitments and excessive/unauthorized spends. Block unsupported channels at startup. VLS protocol rejection must not silently downgrade to native signing.
6. **Extensions.** Add scoped NLightning auxiliary operations and BOLT 12, then evaluate upstream splicing/dual funding/taproot work separately. Prior-version key and channel migration are excluded by the FAFO compatibility policy.

Use existing signer/vector tests as backend conformance tests: `test/NLightning.Infrastructure.Bitcoin.Tests/Signers/`, `Taproot/`, integration BOLT 3 vectors and `Persistence/SignerStateReloadTests.cs`/`SplicedSigningInfoReloadTests.cs`. Add failure injection tests that prove safety outcomes, not only serialization. Run the affected unit suites and repository build/format checks for implementation PRs; use `scripts/run-cluster.sh` for interoperability and on-chain suites, with net10.0 and the repository's SQL Server exclusion. Measure remote round trips under channel locks, concurrent routing throughput and ECDH latency before rollout.

No engineering-time estimate is committed yet. The cheapest first proof is native remote execution; the largest uncertainties are independent durable safety state, full VLS on-chain/auxiliary coverage, and payment authorization. The VLS spike and crash-consistency prototype should resolve these before a delivery estimate or production rollout.
