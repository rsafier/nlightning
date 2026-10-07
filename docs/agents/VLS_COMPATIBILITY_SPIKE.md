# VLS compatibility spike

Research date: 2026-10-07. Branch: `wip/remotesigner`.
Canonical VLS source: `cb8a64c71d3b214951e752281f05b9090e77f074`.

## Scope and reproduction

[The standalone Rust executable](../../tools/vls-compat-spike/README.md) exercises
VLS's public core API directly with its real `SimpleValidatorFactory` and
`enforce_balance=true`. No null validator, test-utils feature, direct
EnforcementState edits or hidden private-key export is used. The fixture uses a
32-byte `[42; 32]` node seed, an artificial 3,000,000 sat funding outpoint and
known peer keys/shachain seed. A test-only fixed starting-time entropy factory makes
LDK channel vectors reproducible; it must never be used by a live signer. The
public identities and basepoints are reproducible.

Run from repository root:

```bash
rustup toolchain install 1.94.0 --profile minimal
./tools/vls-compat-spike/run.sh
```

Rust and every Cargo dependency are pinned for this spike. The runner clones the
canonical GitLab repository, verifies the exact revision and tracked cleanliness,
and builds with the committed Cargo.lock. Dependencies include `lightning 0.2.6`
and `bitcoin 0.32.101`; the manifest's allowed version ranges are not used to
re-resolve packages on each run. This crate is outside NLightning's solution and
adds no production dependency or `Signing:Mode=Vls` capability.

## Observed validation

The checked-in runner completed with exit 0 on 2026-10-07 using Rust 1.94.0:
`PASS: pinned VLS regtest core compatibility spike`. Both ECDSA channel types
completed every acceptance/rejection assertion below. The build produced no
warnings. `rustfmt --check` and `bash -n run.sh` passed.
[Recorded fixture stdout](../../tools/vls-compat-spike/fixture-output.txt) contains
the public key vectors and exact policy refusal messages.

The spike asserts the same-seed mainnet node-ID match directly, the three regtest
node-ID mismatches, and **all five channel key-role mismatches for all three stock
styles on both regtest and mainnet** (30 comparisons). For example:

| Fixture | Compressed public key |
| --- | --- |
| NLightning v3 node / VLS LND mainnet node | `032a30bdc67f618be95531a577f4e52b24aa59130a7b411cb79971b285c0d8cf60` |
| VLS LND regtest node | `02fe5942b512192d05898f53f6427fe6c21b513a9920308cc21da7c26af1184f09` |
| NLightning channel index 1 funding key | `03819c33a3e1c8efeffe125f07a3ac9aeadde61a78d30daa68da2c4c66dcf1c419` |
| VLS LND mainnet channel dbid 1 funding key | `031989cfd6fb0174c2b712b14053eca43c8dc054baa1dc7f66df4634a988da0bda` |

The NLightning side of these vectors is an independent Rust reconstruction of
its documented BIP32 paths, not a call into the C# key manager. It is a derivation
comparison, not an import/migration conformance test. Wallet derivations and
commitment seed equivalence are not tested.

## What the core exercise covers

Both `StaticRemoteKey` and `AnchorsZeroFeeHtlc` use normal channel setup and
commitment progression starting at commitment 0:

- Stable node identity and safe channel setup with peer funding/basepoints,
  funding outpoint, contest delays and channel type.
- Rejection of an invalid peer signature on our initial commitment, followed by
  validation and activation of the correctly signed commitment.
- Signing the peer's commitments 0 and 1, independently verifying each funding
  ECDSA signature against its BIP143 transaction sighash; identical retries
  yield the identical signature.
- Rejection of a conflicting retry that moves 100,000 sat to the peer, a skipped
  commitment number, and an incorrect revocation secret. Rejections leave the
  commitment/revocation counters unchanged.
- Validated peer revocation 0 and its identical retry, followed by signing
  commitment 2; then peer revocation 1.
- Rejection of an unauthorized outgoing 100,000 sat HTLC, followed by explicit
  `add_keysend` authorization and successful commitment 3 signing with one HTLC
  signature. This proves authorization at HTLC admission, not completed payment
  settlement or preimage handling.
- Signing our already validated initial commitment for force close and
  independently verifying its funding signature.

This is a regtest-network core exercise with deterministic transaction fixtures,
not a live regtest blockchain, a protocol transport test, an NLightning-to-VLS
integration or peer interoperability proof. HTLC signatures are counted but not
independently verified here. Persistence is `DummyPersister`, so no state survives
process exit and no crash/replay/fencing guarantee is demonstrated. Default
regtest policy has `use_chain_state=false`; the spike does not prove funding
confirmation, SPV/oracle checks or on-chain monitoring.

## Required semantic adapter

| NLightning boundary | VLS boundary and required change |
| --- | --- |
| `CommitmentSigningService.SignRemoteCommitment` currently builds a transaction then calls `ILightningSigner.SignChannelTransaction` and separately signs HTLCs | Send the complete commitment specification to `SignRemoteCommitmentTx2` (message 1019), which calls `sign_counterparty_commitment_tx_phase2`. VLS rebuilds and validates the commitment and returns funding and HTLC signatures together. A generic transaction-signing proxy is insufficient. |
| `EngineCommitmentVerifierPort` / `CommitmentSigningService.VerifyLocalCommitment` verifies peer signatures using local public data | Call `ValidateCommitmentTx2` (1035) with the semantic specification plus funding/HTLC signatures. In VLS mode, public verification alone cannot advance the signer-owned policy state. Keep signature count/order/encoding checks explicit. |
| `ChannelStateTransitionService.CreateRevokeAndAck` calls `AdvanceLocalCommitment` then `RevealPerCommitmentSecret` after the node DB save | With VLS protocol >=5, use explicit `RevokeCommitmentTx` (40) only after the DB transition is durable. Validation and revocation are separate; protocol version <5 couples them. Preserve unknown-outcome reconciliation between node and signer stores. |
| Incoming `revoke_and_ack` currently reaches the engine and node shachain | Also call `ValidateRevocation` (36) before allowing signer commitment progression. VLS validates the secret against its recorded peer point and its shachain. |
| NLightning allocated numeric channel key index | VLS `NewChannel` takes a peer ID and monotonically managed dbid; channel identity is a 41-byte peer-ID-plus-little-endian-dbId value. Keep this signer ID separate from the 32-byte BOLT channel ID and persist their mapping. |
| HTLC/payment send, receive and forwarding | Register approved invoices or explicit keysend with VLS; route fee and amount policies need a trusted provisioning/authorization credential separate from an untrusted node. The spike grants authorization locally and does not design that credential. |

VLS and NLightning expose increasing commitment numbers. VLS's transaction/key
construction converts them to `2^48 - 1 - number`; its shachain secret validation
uses the same reverse index. Do not reverse numbers a second time at the protocol
boundary. VLS `HTLCInfo2.offered_htlcs` means **offered by that transaction's
broadcaster**, whereas NLightning specifies its holder side. For a peer commitment,
our outgoing HTLC is VLS's received HTLC. The protocol handler flips extracted
HTLC directions for remote versus local commitment validation; reproduce this
explicitly with directional vectors rather than sorting arbitrary signatures.

The raw VLS protocol BitcoinSignature is a 64-byte compact ECDSA signature plus a
separate sighash byte. Convert DER/compact forms at the edge and retain the
appropriate `ALL` / `SINGLE|ANYONECANPAY` HTLC sighash semantics. Authenticate the
Rust gateway transport and map policy refusals separately from timeouts; there
must be no downgrade to the native signer.

## Derivation and migration

NLightning's v3 node path is `m/1017'/0'/6'/0/0` for every network. VLS LND style
uses coin type 0 for Bitcoin and 1 for regtest/testnet/signet. Consequently a
matching BIP32 seed can match the **node identity on mainnet** under LND style;
it does not match on regtest, and even mainnet node-ID equality does not migrate
channel keys or wallet state. Legacy NLightning v1/v2 root construction is also
different.

NLightning channel roots use `m/6425'/0'/0'/0/index`, followed by hardened roles:
funding 0, revocation 1, payment 2, delay 3, HTLC 4, commitment seed 5. VLS Native
(CLN style) uses HKDF with peer/channel IDs; LDK style has its own BIP32/hashed
layout; LND style uses `m/1017'/coin'/family'/0/index` with families funding 0,
revocation 1, HTLC 2, payment 3, delay 4 and a separately HKDF-derived commitment
seed. None is the NLightning channel scheme. Rotated funding keys, shachain
history, wallet account paths and allocated indexes require explicit equivalence
vectors and custom derivation/state import to preserve existing channels.

Start with **fresh VLS-backed nodes and new channels**. Do not import the native
seed and imply channel compatibility. A future custom key-derivation backend
must be measured and reviewed along with state conversion; supplying only a node
secret does not reproduce the BIP32 root.

## Unsupported subset and next gate

At the pinned revision, `SignSpliceTx` exists in the protocol but its handler is
`unimplemented!()` and it is not advertised. No NLightning taproot/MuSig2 channel
or gossip v2 operation family was found in the wire protocol. This spike does not
prove dual funding, interactive RBF, BOLT 12 or any on-chain claim/sweep/penalty
operation. NLightning backup/peer-storage encryption, offer path IDs, accounting
and liquidity signatures still need scoped extensions or another explicit design.

First usable adapter gate: authenticated protocol gateway + fresh node identity,
ordinary single-funded ECDSA static-remotekey/zero-fee anchors channels, semantic
setup/sign/validate/revoke hooks, explicit invoice/keysend approval, durable
signer persistence, and negotiated-feature suppression before peers connect.
Refuse startup with unsupported persisted channels. Then prove live send/receive,
forwarding, reconnect, cooperative/force close, HTLC success/timeout, penalty and
CPFP. Do not advertise VLS mode until these paths work and malicious requests are
rejected with unchanged durable state.

The broader [remote-signing plan](REMOTE_SIGNING_PLAN.md) remains applicable,
including enclave state freshness, rollback protection and single-writer fencing.
