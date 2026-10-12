# Pinned VLS compatibility spike

Research tooling, separate from the NLightning build and node. This executable
uses the real VLS core policy/signing implementation on `Network::Regtest`;
it does not connect to bitcoind, run vlsd, or implement a C# VLS adapter.
Its fixed seeds and funding outpoint are test fixtures. Its `DummyPersister`
is intentionally in-memory: this does not establish crash/restart safety.

Prerequisites: Git, a C compiler/linker, and Rust through rustup:

```bash
rustup toolchain install 1.94.0 --profile minimal
./tools/vls-compat-spike/run.sh
```

The runner fetches canonical VLS revision
`cb8a64c71d3b214951e752281f05b9090e77f074`, checks the revision and tracked
source cleanliness, stages this crate beside it, and builds with the checked-in
Cargo.lock (`--locked`). It uses the user cache, not the node's source tree.
An existing cache at a different revision is refused.

For an existing verified checkout, use the expected cache layout:

```bash
mkdir -p /tmp/nltg-vls-spike
ln -s /absolute/path/to/pinned/vls /tmp/nltg-vls-spike/source
VLS_SPIKE_CACHE=/tmp/nltg-vls-spike ./tools/vls-compat-spike/run.sh
```

Any failed assertion, policy refusal of an expected valid operation, or unexpected
acceptance exits nonzero. Success prints `PASS: pinned VLS regtest core compatibility spike`.
See [the assessment](../../docs/agents/VLS_COMPATIBILITY_SPIKE.md) for exact
coverage, outputs, semantic adapter requirements and limitations.
