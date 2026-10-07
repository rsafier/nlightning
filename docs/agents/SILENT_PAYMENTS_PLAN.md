# Silent payments plan (BIP 352, NL-1254)

Status: **planned** (2026-10-07, plan only; no product code yet). Epic NL-1254, tasks NL-1255..NL-1270 in `docs/agents/ISSUES.md` ("Silent payments (BIP 352)" section). Branch of this plan: `wip/sp-plan` from `wip/fafo` at `89d8c7be`.

This plan is written for agents who implement it without the context of the conversation that produced it. Read §0 first, then the task you are given. CLAUDE.md is still the authority on layers, conventions, migrations and the test cycle.

## 0. How to use this document

- **Spec:** BIP 352 version **1.1.1** (bitcoin/bips `bip-0352.mediawiki`, changelog entry 2026-04-16). The BIP and its vectors win over this plan and over any other implementation. When the BIP text and the reference code differ, follow the BIP and add an ISSUES entry.
- **Vectors:** `bip-0352/send_and_receive_test_vectors.json` at bitcoin/bips commit `c2ac36f48f71615984087fd151f410457edfed72` (last change 2026-04-16; SHA-256 of the file `f5f9ed4afd76a1b76f3c70b1cbe67532f89abbe559f8e02d7fc3d8ecb93af4a1`; 28 test cases, each with one sending and one or two receiving cases). Vendor the file unchanged (task NL-1256). Reference code: `bip-0352/reference.py` at the same commit, built on the vendored secp256k1lab 1.0.0.
- **Task order:** SP-C (shared core) → SP-S (send) ∥ SP-R (receive) → SP-T (proofs) → SP-X (extras). The dependency graph is in §4.0. Each task names its ledger ID. When a task is done, set its entry to `fixed (<SHA>)` in the same commit and recount the Summary rows (CLAUDE.md "Issue tracking").
- **Owner decisions** are numbered D-SP1..D-SP14 in §1.3. Each has a recommendation. Implementers follow the recommendation until the owner overrides it, and record the override in §8.
- **Do not** add a BOLT feature bit. Silent payments are an on-chain wallet feature and never appear on the Lightning wire.

## 1. Goal, scope and decisions

### 1.1 Goal

The node's on-chain wallet can:
- **pay** a BIP 352 silent payment address (`sp1q…`, `tsp1q…`, `sprt1q…`) from `withdraw`, and from LND gRPC `SendCoins` once SP-X2 is done;
- **receive** to its own silent payment address. A block scanner, run inside the chain monitor, finds payments. Found outputs become ordinary wallet UTXOs that channel funding, fee inputs, withdrawals and the accounting books can use;
- **recover** silent payment funds from the key file alone by rescanning from a birthday height.

Every BIP 352 vector passes byte for byte.

### 1.2 Scope and non-goals

In scope:
- version 0 addresses;
- labels, including the change label `m = 0`;
- full-node scanning against our own bitcoind, in ZMQ and Poll modes (NL-1094);
- reorg rollback;
- rescan;
- accounting;
- the operator commands;
- a regtest proof between two NLightning nodes;
- interop with one reference wallet.

Non-goals:
- **CoinJoin or other multi-party sends.** BIP 352 recommends against them, as there is no security proof in a collaborative setting.
- **Sending from inputs whose private keys we do not hold alone.** This covers the shared funding input of a splice, the peer's inputs in interactive-tx, and MuSig2 funding keys. See D-SP11.
- **BIP 375 PSBT fields and BIP 374 DLEQ proofs.** These are for external signers. We sign in-process.
- **Being a tweak-index server for other wallets.**
- **Remote scanning that hands our scan key to a server** (Frigate-style). This is never allowed (§6).
- **Light-client scanning as the default.** It is an option in SP-X1.
- **Output descriptors (`sp()`) and BIP 392 descriptors.** We will follow them if they are adopted.
- **Silent payments over Lightning.** No BOLT exists for this, and BOLT 12 covers the static-address use case off chain.

### 1.3 Owner decisions (recommendations in bold)

| ID | Question | Options | Recommendation |
|---|---|---|---|
| D-SP1 | Name of the switch, and its default | (a) a config section `SilentPayments` with `Enabled`; (b) a BOLT 9-style experimental feature | **(a) `SilentPayments:Enabled`, default `false` on every network at first.** This is not a BOLT feature, so it does not belong in `FeatureOptions.ExperimentalFeatures`. Follow the non-BOLT pattern (`LndGrpcOptions`, `CashuPaymentProcessorOptions`: `Enabled` + `AllowMainnet` + `GetValidationErrors(isMainnet)` + `IValidateOptions` with `ValidateOnStart`). Sub-switches: `Send` (default `true` when `Enabled`) and `Receive` (default `true` when `Enabled`). |
| D-SP2 | Mainnet gate | refuse on mainnet unless `AllowMainnet`; or allow | **Refused on mainnet unless `SilentPayments:AllowMainnet=true`** until NL-1270 (a mainnet canary with a small amount, both directions, plus a rescan). Sending could be opened first, because it needs no scanning and only the vectors decide its correctness (sub-decision of NL-1270). |
| D-SP3 | Derivation path and account | BIP 352's `m/352'/coin'/account'/{1',0'}/0`. Coin type 0' everywhere (as our BIP84/86 deposit paths), or 1' on test networks (BIP44) | **`m/352'/0'/0'` on mainnet and `m/352'/1'/0'` on every test network (testnet, testnet4, signet, Mutinynet, regtest), account 0.** Our deposit paths hard-code coin 0' (`Domain/Bitcoin/Constants/KeyConstants.cs`), but silent payment wallets recover from a seed by the BIP's path. A user who restores the BIP39 mnemonic of a v3 key file into Sparrow, Cake or Dana on signet must find the funds. Scan key: `…/1'/0`. Spend key: `…/0'/0`. Both hardened as the BIP requires (`/0` under a hardened parent, exactly as written in the BIP). |
| D-SP4 | Legacy key files (v1/v2, NL-158) | refuse; or derive from the legacy master | **Derive from the same master `SecureKeyManager` uses (for v1/v2: node key plus genesis chain code), and log at start that this wallet cannot be recovered by other silent payment wallets.** For v1/v2, `info`/`getspaddress` print `recoverable_elsewhere: false`. Refusing would leave older nodes without the feature for no safety gain: our own rescan still recovers the funds. |
| D-SP5 | Labels | none; change only; operator labels | **The change label `m = 0` is always scanned. Operator labels `m ≥ 1` are created with `getspaddress --label <name>`, are capped by `SilentPayments:MaxLabels` (default 1,000) and are stored in `SilentPaymentLabels`. Recovery also scans `SilentPayments:RecoveryLabelCount` (default 100; the BIP suggests up to 100k when unknown) of unnamed labels.** Labels are not identities: every labeled address shares `B_scan` (BIP 352 "Labels"). |
| D-SP6 | Change of a send that pays a silent payment address | P2WPKH (today's `FeeInputSelector` change, line ~362); BIP86 P2TR; our own SP change label `m = 0` | **BIP86 P2TR change from the wallet (`GetUnusedAddressAsync(P2Tr, isChange: true)`) for any transaction with an SP output.** With P2WPKH change next to a P2TR payment, anyone can tell which output is the change. SP change (`m = 0`) is offered behind `SilentPayments:ChangeToSilentPayment` (default `false`). The sender knows the output at signing time and inserts it directly (§3.4). Other wallet sends keep their current change type. |
| D-SP7 | Birthday height | wallet `HeightOfBirth`; tip at first enable; operator input | **The tip at the first start with `Receive` on, stored in `SilentPaymentScanState.BirthdayHeight`.** No address of ours existed before that. A restored node (`restorechanbackup` / new key file from a mnemonic) runs `sprescan --from-height <h>`. When the operator knows the seed was used for SP elsewhere, `SilentPayments:BirthdayHeight` overrides it. |
| D-SP8 | Scanning on pruned nodes and Poll mode | live only; refuse when pruned | **Live scanning works on pruned nodes:** the tip's undo data exists. **Rescans are refused below `pruneheight`** (`getblockchaininfo`); the command says so. Poll mode (NL-1094) needs nothing extra: blocks reach the same `StageBlockAsync`. |
| D-SP9 | Where prevouts come from | `getblock <hash> 3`; REST `/rest/spenttxouts/<hash>.bin`; `getrawtransaction` per prev tx | **`Auto`: probe at start in that order and use the first that works; `SilentPayments:PrevoutSource` forces one.** Details in §3.5. The acceptance backend is Bitcoin Core 31.1. Other backend compatibility is outside this proof scope. |
| D-SP10 | LND gRPC exposure | none; read-only visibility; `SendCoins` to `sp1` | **`SendCoins` accepts an `sp1…`/`tsp1…`/`sprt1…` address when `SilentPayments:Send` is on (an extension; LND itself refuses it). `NewAddress` stays as it is: LND's `AddressType` enum has no SP value, and a static address does not fit "new address" semantics. `ListUnspent` (walletrpc) and `GetTransactions` show SP UTXOs as `TAPROOT_PUBKEY` outputs.** `SendOutputs` (raw scripts) cannot express SP and stays unchanged. |
| D-SP11 | Splice-out, sweeps, channel closes and dual-funded opens to an SP address | allow; refuse | **Refuse with a clear error ("pay the wallet, then `withdraw` to the silent payment address").** An SP output depends on the private keys of every eligible input. Splice and dual-fund transactions carry the peer's inputs, and their P2TR keys are unknown to us. Interactive-tx also lets inputs change after `tx_add_output`. A MuSig2 (taproot channel) funding input is eligible, but its key is shared. Sweeps spend P2WSH or NUMS-internal-key P2TR outputs, which are not eligible, so they have no eligible input at all. A cooperative close output is negotiated before signing and spends the shared funding key. |
| D-SP12 | Dust and minimum outputs | | **Send:** refuse an SP output below `max(330 sat, SilentPayments:MinSendSat)` (default 546). **Receive:** an output below `SilentPayments:MinReceiveSat` (default 1,000 sat) is matched and counted, but it is not added to the wallet (metric `dust_ignored`). As the BIP warns, it still advances `k`. Operators may set the minimum to 0. |
| D-SP13 | Coin selection around SP coins | free mixing; avoid mixing | **`SilentPayments:AvoidMixing` (default `true`): `FeeInputSelector`/`WalletSpendService` prefer solutions without SP coins, or with exactly one, and never mix SP coins of different labels unless the amount needs it. When they must mix, they log the linkage at information level.** Spending SP outputs together links them (§6). |
| D-SP14 | Rescan and RPC budget | | **A rescan runs in the background with its own checkpoint (§3.6), at most `SilentPayments:RescanBlocksPerSecond` (default unlimited), and `spstatus` reports progress. Live scanning must keep the per-block budget of §3.7 or log a warning.** |

## 2. BIP 352 as it applies to us

Notation: `G` is the generator, `n` the curve order, and `hash_tag(x) = SHA256(SHA256(tag) || SHA256(tag) || x)`. `ser32` is big-endian, `ser256` is a 32-byte big-endian integer, and `serP` is a 33-byte compressed point. The tags are `BIP0352/Inputs`, `BIP0352/SharedSecret` and `BIP0352/Label`.

**Eligible transaction (receiver):**
- it has at least one P2TR output; a rescan may consider only unspent ones;
- it has at least one eligible input (below);
- it spends **no** output of SegWit version > 1. A v2..v16 prevout makes the whole transaction ineligible.

**Eligible inputs and their public key.** Only compressed or x-only keys count. Anything else is skipped without failing.
- **P2TR** (`5120{x}`): the **output key** from the prevout's scriptPubKey (even Y), for both key-path and script-path spends. Exception: a script-path spend whose control block's internal key equals the NUMS point `H = 50929b74c1a04954b78b4b6035e97a5e078a5a0f28ec96d547bfee9ace803ac0` is skipped. An annex (last witness item starting `0x50` with more than one item) is dropped before the key-path/script-path check.
- **P2WPKH** (`0014{h}`): the last witness item, if it is a 33-byte compressed key.
- **P2SH-P2WPKH** (`a914{h}87` with scriptSig `160014{h'}`): the last witness item, compressed.
- **P2PKH** (`76a914{h}88ac`): the key found by scanning the scriptSig from the back with a 33-byte window whose HASH160 equals `h`. This handles malleated scriptSigs. Find it the way `reference.py` `get_pubkey_from_input` does, but do it efficiently.
- Everything else (P2WSH, P2SH multisig, bare scripts, uncompressed keys) is ignored. It can still fund the transaction.

**Sender** (inputs fixed first):
1. `a = Σ a_i` over the eligible inputs. Negate a P2TR key whose point has odd Y first. Sum **all** keys before checking: the intermediate sum may be zero (vector "Input keys intermediate sum is zero but final sum is non-zero"). If `a = 0`, fail.
2. `outpoint_L` = the lexicographically smallest 36-byte outpoint (`txid` in serialized byte order, then `vout` as 4 bytes little-endian) over **all** inputs of the transaction, not only the eligible ones. This is a byte comparison, not a vout-integer comparison (vector "Outpoint ordering byte-lexicographically vs. vout-integer").
3. `input_hash = hash_Inputs(outpoint_L || serP(A))` with `A = a·G`. If it is 0 or ≥ n, fail.
4. Group recipients by `B_scan`. A group larger than `K_max = 2323` fails. For each group: `ecdh = input_hash·a·B_scan`, and `k` counts up from 0 across the group's `B_m` (one `k` per output, never reused across labels). `t_k = hash_SharedSecret(serP(ecdh) || ser32(k))` (0 or ≥ n fails). `P = B_m + t_k·G`, then output `5120 || xonly(P)`.
5. Every derived output must be in the final transaction. The inputs must not change after derivation. Sign with SIGHASH_DEFAULT/ALL, never ANYONECANPAY.

**Receiver** (per eligible transaction):
1. `A = Σ A_i`. Skip the transaction if `A` is the point at infinity.
2. Compute `input_hash` as above; 0 or ≥ n fails the transaction.
3. `ecdh = input_hash·b_scan·A`. Equivalently, multiply `b_scan` by the tweaked point `input_hash·A`; that 33-byte point is exactly what a tweak index serves (SP-X1).
4. For `k = 0, 1, …` while `k < K_max`:
   - compute `P_k = B_spend + t_k·G` and compare it to the remaining x-only P2TR outputs;
   - if no output matches, do the label check: for each output `o`, compute `o − P_k` and `−o − P_k` (the output is x-only, so try both Y parities) and look the result up in the precomputed label set `{label_m·G}`;
   - on a match, record `(outpoint, P, t_k, m?)`, remove the output and continue with `k+1`;
   - stop when nothing matches.
   - A match that wallet policy later ignores (dust, D-SP12) **still** advances `k`.
5. **Spend key** of a found output: `d = b_spend + t_k (+ label_m) mod n`, where `label_m = hash_Label(ser256(b_scan) || ser32(m))`. It is a **raw** BIP340 key-path spend. There is **no** BIP86/BIP341 tweak: the output key `P` is the key itself. If `P` has odd Y, negate `d` before signing. NBitcoin's `SignTaprootKeySpend` on an `ECPrivKey` built from `d` with no merkle-root tweak does this; verify it against the vectors' `signature` field: `reference.py` signs `msg = SHA256("message")` with `aux = SHA256("random auxiliary data")`.
6. Address: `B_m = B_spend + label_m·G` (none for the plain address). Encoding is bech32m:
   - HRP `sp` on mainnet, `tsp` on testnet, testnet4, signet and Mutinynet, `sprt` on regtest (Bitcoin Core `chainparams.cpp` `silent_payments_hrp`);
   - data = version 0 (`q`) followed by the 66 bytes `serP(B_scan) || serP(B_m)` in 5-bit groups.
   - Length is 116 (mainnet), 117 (`tsp`) or 118 (`sprt`) characters. Decoders accept up to **1023** characters.
   - Versions: v0 must have exactly 66 data bytes. v1..v30 read the first 66 and ignore the rest. v31 fails. On sending we accept only v0 and tell the user that higher versions are unsupported; accepting v1..v30 forward-compatibly is allowed by the BIP and can be done later.

## 3. Architecture on our layers

```
Domain                 SilentPaymentAddress (value object), SilentPaymentAddressCodec (bech32m, BCL),
                       ISilentPaymentCrypto (port), ISilentPaymentKeySource (port), SilentPaymentInputClassifier
                       (pure: prevout + scriptSig + witness -> key bytes/kind), models SilentPaymentOutput,
                       SilentPaymentLabel, SilentPaymentScanState, SilentPaymentsOptions
Infrastructure.Bitcoin SilentPayments/Bip352.cs (maths on NBitcoin.Secp256k1 GE/GEJ/Scalar, as Bip327),
                       SilentPaymentCrypto : ISilentPaymentCrypto, SecureKeyManager (scan/spend keys),
                       BlockPrevoutSource (getblock 3 | REST | getrawtransaction), SilentPaymentScanner,
                       BlockchainMonitorService stage, WalletSpendService SP send, LocalLightningSigner SP inputs
Persistence            SilentPaymentOutputs, SilentPaymentLabels, SilentPaymentScanState; Utxos address FK nullable
Application            SilentPaymentRescanService (hosted), accounting labels, IPC client handlers in Daemon
Daemon/Client/IPC      getspaddress (52), splabels (53), sprescan (54), spstatus (55); withdraw accepts sp addresses
LndGrpc                SendCoins to sp; ListUnspent/GetTransactions visibility
```

All of this follows CLAUDE.md "Layers & dependency rules":
- Domain stays BCL-only.
- secp256k1 and NBitcoin code lives in Infrastructure.Bitcoin.
- New abstractions go in Domain.

### 3.1 Domain

- `src/NLightning.Domain/Bitcoin/SilentPayments/`:
  - `SilentPaymentAddress` (`readonly record struct`: `byte Version`, `CompactPubKey ScanKey`, `CompactPubKey SpendKey`, `string Hrp`). Mind the value-object `?:` null trap (CLAUDE.md gotchas).
  - `SilentPaymentAddressCodec`: a BCL bech32m codec with a 1023-character limit and lower-case output. It refuses mixed case and validates the HRP against a `BitcoinNetwork` (`sp`, `tsp` or `sprt`). It checks the 33-byte prefixes (02/03) but not that the point is on the curve; the Infrastructure side does that through `ISilentPaymentCrypto.IsValidPoint` at parse time. Domain already has a checksum-less bech32 for BOLT 12 (`Domain/Offers/Encoding/Bolt12Bech32.cs`), which cannot be reused; write a small polymod with the bech32m constant `0x2bc830a3`, or move a shared one into Domain.
- `ISilentPaymentCrypto` (pure maths port, Domain/Bitcoin/SilentPayments/Interfaces):
  - `TryGetInputPublicKey(prevoutScript, scriptSig, witness, out key)`, where `key` is 33 bytes, x-only keys lifted to even Y;
  - `TrySumPublicKeys(keys, out sum)`;
  - `ComputeInputHash(smallestOutpoint, sumKey)`;
  - `DeriveOutputs(SenderInput[] inputs, recipients)`;
  - `ComputeLabelTweak(...)`, which needs the scan key and is therefore on `ISilentPaymentKeySource`.
- `ISilentPaymentKeySource` (implemented by `SecureKeyManager`). Keys never leave it, mirroring `ISecureKeyManager.ComputeNodeSharedSecret`:
  - `CompactPubKey ScanPubKey`, `CompactPubKey SpendPubKey`;
  - `ComputeScanSharedSecret(ReadOnlySpan<byte> tweakedInputKey, Span<byte> point33)`, which returns `b_scan·(input_hash·A)` as a compressed point;
  - `GetLabelTweak(uint m, Span<byte> scalar32)` and `GetLabelPoint(uint m)`.
  - The spend private key is derived on demand by the signer only, as deposit keys are (`GetDeposit*KeyAtIndex`).
- `SilentPaymentInputClassifier`: the eligibility rules of §2 in pure byte logic (script templates, annex, NUMS control block, segwit-version check). It is unit-tested from the vectors' `vin` objects without secp256k1. The key-validity checks go through the port.
- Models: `SilentPaymentOutputModel` (outpoint, output x-only key, tweak `t_k` 32 bytes, `uint? Label`, amount, block height/hash, spent-by txid), `SilentPaymentLabelModel` (m, name, created height), and `SilentPaymentScanState` (birthday, live-from height, rescan cursor height/hash, prevout source in use).
- `UtxoModel` gains `SilentPaymentOutputModel? SilentPayment`. Exactly one of `WalletAddress` and `SilentPayment` is set.

### 3.2 Infrastructure.Bitcoin: maths

- `src/NLightning.Infrastructure.Bitcoin/Crypto/SilentPayments/Bip352.cs`: `internal static`, a line-by-line port of `reference.py` (`create_outputs`, `scanning`, `get_input_hash`, label handling, `K_max`). It works on NBitcoin.Secp256k1 4.0.3's `GE`/`GEJ`/`Scalar`, following the precedent of our own MuSig2 port `Crypto/Musig2/Bip327.cs` (NL-912, NL-1091).
- Primitives available in NBitcoin.Secp256k1 4.0.3 (checked in the package):
  - `ECPubKey.TryCombine` (n-ary point sum; it fails on the point at infinity, which the vectors exercise);
  - `ECPubKey.GetSharedPubkey(ECPrivKey)` (unhashed ECDH point, already used in `Onion/SphinxKeyGenerator.cs:170` and `Crypto/Functions/Ecdh.cs:26`);
  - `ECPrivKey.TweakAdd`/`TweakMul` and the pubkey tweak equivalents;
  - `Scalar` arithmetic with `Negate`/`CondNegate`;
  - `MultGen`, and batch multiplication (`ecmult_multi`/`MultBatch`).
  - Our `ISecp256K1Math` (`AddPubKeys` 2-ary, `MultiplyPubKey`, `AddPrivKeys`) is too narrow. Do not route BIP 352 through it; keep the maths in `Bip352` and expose only `ISilentPaymentCrypto`.
- Constant time: the sender's `a` and the receiver's `b_scan` are secrets. Use the constant-time multiplications (`ECPubKey.GetSharedPubkey`, `MultGen`), not the variable-time `ecmult` with a secret scalar. Public-only operations (sums of input keys, `P_k` comparisons, label subtraction) may use the variable-time paths.
- Hashing: tagged SHA-256. Reuse `WipingSha256` (`Crypto/Musig2/WipingSha256.cs`) wherever secrets are hashed (`ser256(b_scan)` in labels, `serP(ecdh)`).
- Zero every secret buffer: `a`, `ecdh`, `t_k` while it still sits in a buffer, `d`.

### 3.3 Keys: SecureKeyManager

- `src/NLightning.Infrastructure.Bitcoin/Managers/SecureKeyManager.cs` derives from the master it already holds in locked memory (`_secureMasterKeyPtr`, `GetMasterKey()` lines ~1026-1043):
  - scan key `m/352'/coin'/0'/1'/0`;
  - spend key `m/352'/coin'/0'/0'/0` (D-SP3).
- **Scan private key:** derived once at start when `Receive` is on and kept in its own locked buffer (as `_secureNodeKeyPtr`). The scanner needs it for every block. It is used only inside `ComputeScanSharedSecret` and `GetLabelTweak`.
- **Spend private key:** derived on demand for signing, wiped after.
- **Public keys:** cached.
- **Key file:** no format change. Both keys derive from the existing master, v1/v2 included (D-SP4). Add the paths to `KeyConstants` (`SilentPaymentPurpose = 352`).

### 3.4 Send flow

The current flow (`src/NLightning.Infrastructure.Bitcoin/Wallet/WalletSpendService.cs`):
1. `ParseAddress` (line ~312);
2. `WithdrawLockedAsync`;
3. `FeeInputSelector.ReserveAsync` (line ~239);
4. the transaction is built (lines ~500-511, RBF sequence `0xFFFFFFFD`);
5. `SignWalletTransaction(tx, reservationId, [])` (line ~514);
6. a `BroadcastPurpose.WalletSend` row through `IChainBroadcaster.SaveAndPublishAsync`.

With an SP destination:
1. **Parse:** `SilentPaymentAddressCodec` before `BitcoinAddress.Create`. The error text names the HRP expected for the node's network.
2. **Estimate:** the destination weight is a P2TR output (43 vB). Reserve with a placeholder `5120 || 00…` script. `EstimateOutputFeeAsync` and the send-all amount work unchanged.
3. **Reserve inputs** through `IFeeInputSelector` under these SP rules:
   - every candidate is P2WPKH, BIP86 P2TR or an SP output. Today the selector only picks P2WPKH and P2TR wallet coins, all eligible, all with private keys we hold;
   - at least one eligible input;
   - no SegWit v>1 input;
   - D-SP13 mixing preferences.
   - Add a `SelectionPolicy` parameter rather than a second selector.
4. **Derive** with the key material of exactly the reserved inputs, the change output decided (D-SP6), and the input set frozen. The signer computes `a`, because private keys stay in the signer. Add `ILightningSigner.ComputeSilentPaymentOutputs(Guid reservationId, IReadOnlyList<SilentPaymentAddress> recipients, IReadOnlyList<(TxId, uint)> allInputs)`. It:
   - checks that every input belongs to the reservation;
   - derives each `a_i` exactly as `DeriveWalletPrevOut` does (`LocalLightningSigner.cs:1637`). For a BIP86 coin, `a_i` is the **tweaked** private key that controls the output key. For an SP coin, `a_i` is `d`;
   - negates odd-Y taproot keys;
   - returns only the output scripts.
5. **Build** with the outputs in a random order (BIP 69 is not used today; keep the existing order and shuffle only the SP and change outputs). Sign with `SIGHASH_DEFAULT` for P2TR and `ALL` for P2WPKH, as now. Assert that the signed transaction's input set equals the one used for derivation, and fail closed otherwise.
6. **Persist** the `WalletSend` row as today, with the recipient SP address in its label/metadata (`BroadcastTransactions` needs no schema change if the address goes into the existing `Label`/`Tags`; otherwise add a nullable `Destination` column in NL-1260).
7. **Change to our own SP address** (`ChangeToSilentPayment`): derive it with `m = 0` and insert the output into `SilentPaymentOutputs`/`Utxos` when the transaction confirms. The confirmed-broadcast path already knows the row: `BlockchainMonitorService.ConfirmedBroadcasts.cs`. The scanner would also find it on rescan.

**RBF and abandonment.** Our wallet sends are rebroadcast unchanged, which is safe. Any future fee bump of a wallet send that adds or removes inputs must re-derive (BIP 352 functional test "Ensure the silent payment address is re-derived if inputs are added or removed during RBF"). NL-1257 adds a guard test: a bump path that changes the inputs of a transaction carrying an SP output must call the derivation again. An abandoned send (NL-294) whose inputs are reused gets a fresh derivation automatically.

**Multiple recipients.** `withdraw` pays one address. `IWalletSpendService.SendOutputsAsync` takes scripts, so SP recipients need a new `SendAsync(IReadOnlyList<WalletRecipient>)` where `WalletRecipient` is a script **or** an SP address. This lets several SP outputs to the same `B_scan` (shared `k`) and mixed recipients go in one transaction, and the BIP vectors with several recipients drive it in-process.

### 3.5 Receive: prevouts, scanning, storage

**Where the scan runs.** `BlockchainMonitorService.StageBlockAsync` (lines ~1245-1325) stages one block in one unit of work:
1. watched txs;
2. `StageWalletMovements` (lines ~1368-1453);
3. accounting;
4. watched spends;
5. depths;
6. state;
7. header ring.

A new `StageSilentPaymentsAsync(block, height, uow)` runs **before** `StageWalletMovements`. That way, when a found output is spent in the same block, the spend is matched by the existing input loop (`TrySpendUtxo`). The block's outputs are added through the same `uow.AddUtxo` path, with `UtxoModel.SilentPayment` set. ZMQ, Poll, the tip backstop and the catch-up all reach `ProcessNewBlockAsync` (line ~580), so every notification mode is covered.

**Prevouts** (`IBlockPrevoutSource`, Domain port, Infrastructure.Bitcoin implementation next to `BitcoinChainService`). Given a block, it returns, for each transaction that has a P2TR output, the prevout scriptPubKeys of all its inputs. It never fetches prevouts for transactions without a P2TR output.
- **(1) `getblock <hash> 3`** (Bitcoin Core ≥ 25): every `vin` carries `prevout {scriptPubKey.hex, value}`, read from the block's undo data. **No `-txindex` needed** (verified 2026-10-07 on the signet node's Core 31.1 with no indexes: every input of block 325300 carried its `prevout`). It fails for a block whose data is pruned. Parse it with our own JSON reader, streamed, as `GetBlockTxIdsAsync` streams `getblock 1` (`BitcoinChainService.cs:350`). Read `scriptPubKey.hex` only, never `asm` (NL-1097). Cost: roughly 3-5x the raw block in JSON per block.
- **(2) REST `GET /rest/spenttxouts/<hash>.bin`** (Bitcoin Core ≥ 30, PR #32540, merged 2025-06-27): the block's spent outputs per transaction, in binary from undo data. It needs `-rest`. BlindBit Oracle v2 uses it. Verified on pinned Core 31.1 regtest with and without `-txindex`; exact binary format and the prune refusal proof are recorded in §8. It is the cheapest source: about 200 KB against 13 MB of JSON per mainnet block.
- **(3) `getrawtransaction <prev txid>`** per distinct previous transaction not created earlier in the same block. On Core it needs `-txindex` (or the transaction in the mempool). It is the slowest, a fallback for small or test chains.
- **In-block shortcut:** a prevout created by an earlier transaction of the same block comes from the block itself in every source.
- `Auto` probes (1) on the current tip at start, then (2), then (3). `spstatus` reports the source in use. When none works, `Receive` refuses to start, with a message naming `-txindex`/`-rest`. A per-block failure (pruned block, RPC error) retries with the monitor's retry policy. A persistent failure halts like any other block-processing failure (`chainstatus`, NL-216), because skipping a block would silently lose payments.

**Scanner** (`Infrastructure.Bitcoin/Wallet/SilentPayments/SilentPaymentScanner.cs`):
1. Filter to eligible transactions: a P2TR output, at least one eligible input, no SegWit v>1 prevout.
2. Compute `A`, `input_hash` and `input_hash·A` per transaction. Public data only, so this is parallel across transactions (`Parallel.For` over the block's transactions, bounded by `Environment.ProcessorCount`) and runs **before** the unit of work is opened, so no lock is held during the ECC work.
3. Call `ComputeScanSharedSecret`, one constant-time multiplication per eligible transaction.
4. Run the `k` loop with the label lookup (a `Dictionary<byte[33] → m>` of precomputed label points, change label always present).
5. Yield `SilentPaymentOutputModel`s.

**Storage** (NL-1260):
- `SilentPaymentOutputs`: PK `(TransactionId, Index)`. Columns: `OutputKey` 32, `Tweak` 32, `Label` uint?, `AmountSats`, `BlockHeight`, `BlockHash`, `SpentByTransactionId?`, `Ignored` (dust, D-SP12).
  - Rows are **never deleted on spend**, only on a reorg of their creating block, so rollback can restore a spent output (below).
- `Utxos`:
  - the `(AddressIndex, IsAddressChange, AddressType)` FK to `WalletAddresses` becomes **nullable**;
  - a nullable one-to-one navigation to `SilentPaymentOutputs` is added on the same `(TransactionId, Index)` key;
  - a check constraint requires exactly one of the two.
  - So `UtxoMemoryRepository`, `FeeInputSelector`, balances, `walletbalance`, the anchors reserve (`IAnchorReserveService`, which counts confirmed P2TR outputs) and LND `ListUnspent` see SP coins with no parallel code path.
- `SilentPaymentLabels`: `M` PK, `Name` unique, `CreatedAtHeight`.
- `SilentPaymentScanState`: one row with `BirthdayHeight`, `LiveFromHeight`, `RescanCursorHeight?`, `RescanCursorHash?` and `RescanTargetHeight?`.
- Migration `AddSilentPayments` for all three providers (CLAUDE.md recipe: `add_migration.sh`, compiled models regenerated, `HasPendingModelChanges` false; extend `ChannelRoundTripTests`-style round trips for the new entities in `Integration.Tests/Persistence/`).
- **Every `Utxos` reader that assumes `WalletAddress != null` must be found and handled.** Grep `WalletAddress` in `FeeInputSelector.cs` (lines ~244-261, 326-343, which rebuild the script from `WalletAddress.Address`), `LocalLightningSigner.cs` (~821), `WalletPsbtService`, LndGrpc `WalletKitService` and `AnchorReserveService`. For an SP coin, the script is `5120 || OutputKey`.

**Reorgs:**
- `StageWalletRollbackAsync` (lines ~1775-1810) removes deposits above the fork. It also deletes the `SilentPaymentOutputs` rows created above the fork in the same save. The replacement blocks are scanned again as they connect.
- `FindWalletOutputsUnspentAgainAsync` (lines ~1708-1747) restores outputs spent in disconnected blocks. Its candidate filter `MayBeWalletInput` (line ~1751) already admits any single 64/65-byte witness (a taproot key-path spend), which covers SP coins. The "must map to a watched address" step must also accept a `SilentPaymentOutputs` row, which is why rows survive their spend.
- A reorg deeper than the 100-header ring halts processing as today (`HeaderRingSize = 100`, line ~177).

### 3.6 Rescan and restore

`SilentPaymentRescanService` (Application, hosted, started after the chain monitor like `ImportedTapscriptTracker` (`Infrastructure.Bitcoin/Wallet/Imports/ImportedTapscriptTracker.cs`, NL-1197), whose shape it copies):
1. Walk `RescanCursorHeight → LiveFromHeight − 1` with `GetBlockAsync(height)` and the same prevout source.
2. Scan and save found outputs, their spends, and accounting events, with the cursor, in one save per block or per batch of N blocks.
3. Rewind the cursor on a reorg event (`OnBlockDisconnected`).
4. Stop at `LiveFromHeight`, where the live stage already covers the chain.

On a reorg the cursor never passes the live height, so live and rescan never double-scan one block.

**Spends during a rescan.** An SP output found by the rescan may have been spent later, perhaps by us before a restore. The rescan tracks spends of its found outputs in later blocks it scans: it keeps the found outpoints in memory and checks each block's inputs. When the cursor reaches live, it asks `gettxout` once for every found output still unspent, and marks the rest spent.

Commands:
- `sprescan --from-height <h>`: below `pruneheight` it is refused with the prune height in the message (D-SP8). It also takes `--labels <N>` to scan N extra unnamed labels (D-SP5).
- `spstatus`: shows birthday, live height, cursor, source, found/ignored counts and the last block's scan time.

Restore story:
- A node rebuilt from a v3 mnemonic, or restored with `restorechanbackup` on a new key file of the same seed, gets back its SP funds with `sprescan --from-height <birthday>`.
- The birthday is not in the static channel backup. Add it to `exportchanbackup` metadata when SP is on, as an optional TLV in the backup codec version in force; that is a separate small follow-up, noted in NL-1265.

### 3.7 Performance budgets (acceptance numbers for NL-1262)

- **Live, per mainnet-sized block** (about 4,000 transactions, up to about 2,000 eligible): prevouts ≤ 1.5 s with source (1), ≤ 0.3 s with (2); scan maths ≤ 1.0 s at p95 on the reference machine (the Mac mini M-series the cluster runs on). The total is reported as `nlightning.silentpayments.block_scan_ms` (a histogram on a new `Meter("NLightning.SilentPayments")`).
  - The budget is soft: going over logs a warning and never skips the block.
  - Measure first with a `tools/` benchmark (the pattern of `tools/NLightning.SphinxBenchmark`, NL-083) on the vectors and on synthetic blocks of 2,000 eligible transactions. The managed NBitcoin.Secp256k1 constant-time multiplication costs a few hundred µs, which sets the floor; parallelism across transactions is how we meet the budget.
- **Adversarial block:** `K_max` bounds the `k` loop per transaction at 2,323 iterations, so a block full of outputs to one scan key is O(N·K_max) point additions. The benchmark includes the BIP's K_max vector shape scaled to a full block, and it must finish in under 60 s. Document the measured number in §8.
- **Rescan:** 1 week of mainnet (about 1,000 blocks) in ≤ 30 min with source (2), and the number with (1). Mutinynet or signet at least 5x faster.

### 3.8 Accounting

Found outputs stage `WalletReceived = 30` through the existing `CollectWalletReceived` (`BlockchainMonitorService.Accounting.cs:230`), because they arrive through the same `AddUtxo` path. Spends stage `WalletOutputSpent = 32` and `WalletSent = 31` as today. Reorgs write `Reversal` rows (A1 rules).

New in the event payload:
- source `silent_payment`;
- the label's name (`m ≥ 1`) as the accounting label, so `accountingreport` can group receipts by SP label;
- `m = 0` change classified as a self-transfer.

A rescan that finds historical outputs writes their events with the block's height and time. The sealer orders them by commit order, so they appear late in the ledger, which A1 already allows for imported history (`OpeningBalance` precedent). If the A1 dedup key would treat a rescan finding as a duplicate of a live finding, check the key includes the outpoint. The financial profile (A3) values them at the block time.

### 3.9 IPC and client (next free `ClientCommand` is 52; `src/NLightning.Domain/Client/Enums/ClientCommand.cs` ends at `CancelHoldInvoice = 51`)

| Value | Verb | Request | Response |
|---|---|---|---|
| 52 | `getspaddress [--label <name>]` | key 0 label name? | key 0 address, key 1 label m?, key 2 `recoverable_elsewhere` |
| 53 | `splabels` | paging keys like the other lists | names, m, address, received totals |
| 54 | `sprescan --from-height <h> [--labels <n>]` | keys 0/1 | key 0 accepted, key 1 target height, key 2 refusal reason |
| 55 | `spstatus` | — | scan state, source, counters |

- `withdraw` (25) takes an SP address in its existing `Address` key 0. No contract change is needed.
- Follow CLAUDE.md: a new `ClientCommand` value per wire change, an `IIpcCommandHandler` registered in `NodeServiceExtensions`, `MessagePackSerializer` AOT formatters (`MessagePackAotReadinessTests`), and the client verb and printer in `src/NLightning.Client`.
- `getaddress` (4) is unchanged. `info` adds the SP address when `Receive` is on.
- The drain gate (`Daemon/Services/Ipc/IpcRouting.cs`) refuses `sprescan` while the node drains (NL-591).

### 3.10 LND gRPC

`src/NLightning.LndGrpc`:
- `SendCoins` (`Services/LightningService.Operations.cs:312`) dispatches `WithdrawClientRequest` with the address string, so an SP address passes through once `withdraw` accepts it. Answer `INVALID_ARGUMENT` with our text when `Send` is off.
- `NewAddress` is unchanged (D-SP10).
- `walletrpc.ListUnspent` and `lnrpc.GetTransactions` (`LightningService.Transactions.cs:35`, from the accounting feed) show SP coins. Their address is the bech32m P2TR address of the output key, never the SP address, because that is what is on chain.
- Mind NL-1253: SP coins are not imported tapscripts, so no double count can arise. Add a test that pins it.
- `walletrpc` PSBT funding (`FundPsbt`) may select SP coins. `FinalizePsbt` signs them through the same signer path (NL-1263).

## 4. Waves and tasks

### 4.0 Order

```
NL-1255 (codec) ──┐
NL-1256 (maths) ──┼─> NL-1257 (send) ─> NL-1258 (send surfaces)
                  │
NL-1259 (keys) ───┴─> NL-1260 (schema) ─> NL-1261 (prevouts) ─> NL-1262 (scanner) ─┬─> NL-1263 (spend SP coins)
                                                                                   ├─> NL-1264 (accounting)
                                                                                   ├─> NL-1265 (rescan)
                                                                                   └─> NL-1266 (labels, change)
NL-1257..NL-1266 ─> NL-1267 (proofs) ─> NL-1268 (light source, optional), NL-1269 (LND/splice visibility), NL-1270 (mainnet)
```

Sizes:
- **S**: up to 1 agent-day;
- **M**: 2-3 days;
- **L**: 4-6 days;
- **XL**: over a week.

NL-1260 owns the single migration. Later tasks that need a column add it in a migration of their own.

### Wave SP-C: shared core

**NL-1255 SP-C1: silent payment address codec (S)**
- Domain `SilentPaymentAddress` and `SilentPaymentAddressCodec` as in §3.1. HRPs per network, with Mutinynet and other custom signets on `tsp`.
- Acceptance: every address in the vectors (`receiving[].expected.addresses`, `sending[].given.recipients[].address`) decodes to the vector's scan/spend keys and re-encodes identically. These are all mainnet `sp1q…`. Hand-made `tsp`/`sprt` encodings round-trip.
- Refusals: wrong HRP for the network, a bech32 (not bech32m) checksum, mixed case, more than 1023 characters, v0 with a length ≠ 66, v31, a bad prefix byte. v1..v30 decode the first 66 bytes when the opt-in flag is set (unused at first).
- Tests: `test/NLightning.Domain.Tests/Bitcoin/SilentPayments/SilentPaymentAddressCodecTests.cs`.

**NL-1256 SP-C2: BIP 352 maths and every vector byte-exact (M)**
- `Infrastructure.Bitcoin/Crypto/SilentPayments/Bip352.cs`, `SilentPaymentCrypto : ISilentPaymentCrypto`, and the Domain `SilentPaymentInputClassifier`.
- Vendor the vector file unchanged as `test/NLightning.Infrastructure.Bitcoin.Tests/Crypto/SilentPayments/Vectors/send_and_receive_test_vectors.json`, with its commit and SHA-256 in a README line. Load it as a `TheoryData` per test case.
- Acceptance:
  - **sending:** for every case, the set of derived x-only outputs equals one of the sets in `expected.outputs`. That field is a list of every valid output set, because the recipient order changes the outputs when one receiver gets several labels; `reference.py` uses set equality against any of them. The intermediates `input_private_key_sum`, `input_pub_keys` and `shared_secrets` must match too. Failures (zero sum, K_max exceeded, no valid inputs) produce no outputs and the documented error.
  - **receiving:** `input_pub_key_sum`, `tweak` (= `input_hash·A`, what a tweak index serves) and `shared_secret` match. The found outputs' `pub_key` and `priv_key_tweak` equal the vector's. The BIP340 signature over `SHA256("message")` with `aux = SHA256("random auxiliary data")` (as `reference.py`) equals `signature` byte for byte, using `d = spend_priv_key + priv_key_tweak`.
  - The classifier extracts the right keys from every `vin` shape: malleated P2PKH, uncompressed skipped, NUMS script path, annex, invalid P2SH.
- Constant-time review checklist (§3.2) in the PR.
- Tests: `test/NLightning.Infrastructure.Bitcoin.Tests/Crypto/SilentPayments/Bip352VectorTests.cs`, plus `Domain.Tests` classifier tests over the same `vin` data.

### Wave SP-S: send

**NL-1257 SP-S1: pay a silent payment address from `withdraw` (L)**
- `WalletSpendService` changes per §3.4:
  - the SP parse;
  - placeholder-script reservation;
  - the selector's SP `SelectionPolicy` (eligible inputs only, at least one, no SegWit v>1, D-SP13);
  - `ILightningSigner.ComputeSilentPaymentOutputs`;
  - P2TR change (D-SP6);
  - D-SP12 dust refusal;
  - the frozen-input assertion;
  - the `WalletRecipient` multi-recipient `SendAsync`.
- `SilentPaymentsOptions` with `Enabled`/`Send`/`AllowMainnet` and validation (D-SP1, D-SP2), in the config template (`NodeConfigurationExtensions`) and `--check-config`.
- Acceptance:
  - in-process with a regtest-like `UtxoMemoryRepository`: the BIP sending vectors replayed through `SendAsync` with the vectors' private keys injected as wallet coins, giving byte-exact outputs;
  - `withdraw tsp1…` on regtest pays an output that `Bip352` scanning with the receiver keys finds;
  - send-all to an SP address;
  - refusals: Send off, mainnet without `AllowMainnet`, HRP for the wrong network, below the dust minimum, no eligible input available;
  - the RBF re-derivation guard test (§3.4).
- Tests: `Infrastructure.Bitcoin.Tests/Wallet/WalletSpendServiceSilentPaymentTests`, `Daemon.Tests` withdraw handler, `Client` parse.

**NL-1258 SP-S2: send surfaces: LND `SendCoins` and the client (S)**
- `SendCoins` with an SP address (D-SP10).
- The client `withdraw` usage text names SP addresses.
- `listaccountingevents`/`GetTransactions` show the SP payment with its label.
- Acceptance: `LndGrpc.Tests` `SendCoins` to `sprt1…` returns a txid whose transaction has the derived output; `INVALID_ARGUMENT` when Send is off.

### Wave SP-R: receive

**NL-1259 SP-R1: keys and `getspaddress` (M)**
- `SecureKeyManager`: D-SP3 paths; scan key in locked memory at start; `ISilentPaymentKeySource`; v1/v2 behaviour (D-SP4).
- IPC 52 `getspaddress` (unlabeled address) and the `info` field.
- Acceptance:
  - a v3 key file made from the BIP39 mnemonic `abandon … about` gives the scan/spend public keys that an independent implementation derives at `m/352'/1'/0'/{1',0'}/0`. Pin the values by computing them with `reference.py`-compatible tooling (secp256k1lab plus a BIP32 script, under `scripts/silent-payments/`) and commit the script;
  - the scan private key is never returned by any interface (a reflection test over `ISecureKeyManager`/`ISilentPaymentKeySource`);
  - `getspaddress` on regtest returns `sprt1q…` and the same address after a restart.

**NL-1260 SP-R2: schema `AddSilentPayments` (M)**
- Entities, configurations and DbSets per §3.5: `SilentPaymentOutputs`, `SilentPaymentLabels`, `SilentPaymentScanState`; the nullable `Utxos` FK, the one-to-one and the check constraint.
- The migration for Postgres, SQLite and SQL Server; compiled models.
- Repositories in `Infrastructure.Repositories/Database/Bitcoin/`; `IUnitOfWork` members (wrappers must forward them; follow the pattern of the CLAUDE.md note on `AccountingEventDbRepository`).
- Audit every `Utxos` reader for `WalletAddress != null` (list in §3.5) and make each one handle SP coins.
- Acceptance:
  - SQLite round trip in `Integration.Tests/Persistence/`;
  - the Postgres round trip in the cluster `postgres` suite;
  - `HasPendingModelChanges` false on all three providers;
  - a seeded pre-migration database migrates with its UTXOs intact.

**NL-1261 SP-R3: block prevout source (M)**
- `IBlockPrevoutSource` with the three sources and `Auto` probing (D-SP9, §3.5). Streamed JSON for `getblock 3`. The REST binary parser. The `getrawtransaction` fallback with an in-block shortcut and a bounded LRU of previous transactions.
- `pruneheight` awareness.
- Acceptance:
  - unit tests on captured Core 31.1 regtest answers for each source (record them with an `Explicit` capture test, as `ClnBolt12CaptureTests` does);
  - a cluster live test on a Core 31.1 regtest pod: all three sources give identical prevouts for a block with P2PKH, P2SH-P2WPKH, P2WPKH, P2TR key and script path, and P2WSH inputs. Source (3) needs `-txindex` on that pod;
  - a second Core 31.1 node with `-txindex=0` gives identical prevouts through sources (1) and (2); `Auto` fallback selection is covered by hermetic unsupported-source responses;
  - a pruned regtest (`-prune=550` after mining past it) refuses the old block with the "pruned" error.
- Record the REST verification (D-SP9) in §8.

**NL-1262 SP-R4: scanner in the chain monitor (L)**
- `SilentPaymentScanner` and `StageSilentPaymentsAsync` per §3.5: parallel pre-pass; constant-time ECDH through the key source; the label dictionary; K_max; D-SP12 dust ignore that still advances `k`; insertion through `uow.AddUtxo` with `SilentPayment` set; the `FindWalletOutputsUnspentAgainAsync` and `StageWalletRollbackAsync` extensions for reorgs.
- The metric and the warning budget (§3.7). The benchmark tool under `tools/`.
- Acceptance:
  - **receiving vectors through the scanner:** each vector's `vin` and outputs are wrapped in a synthetic transaction in a synthetic block and fed to `StageBlockAsync` with a fake prevout source. The found UTXOs equal `expected.outputs`, including the "Recipient ignores unrelated outputs" and "non-SP outputs for ourselves" cases. A wallet BIP86 output in the same transaction is still found by `StageWalletMovements`;
  - ZMQ and Poll: the monitor tests run both modes (`NLTG_CHAIN_NOTIFICATIONS=Poll` pattern, NL-1094);
  - reorgs: a found output disappears with its block and is found again in the replacement. An SP output spent in a disconnected block is restored. Both use the existing chain-monitor reorg harness;
  - a restart between blocks neither loses nor duplicates outputs;
  - budget numbers recorded in §8.

**NL-1263 SP-R5: spend silent payment outputs (M)**
- `LocalLightningSigner.DeriveWalletPrevOut` (line ~1637) and `SignWalletTransaction` accept SP coins. The key is `d = b_spend + t_k (+ label)`, negated for odd Y; the script is `5120 || OutputKey`; there is no BIP86 tweak. The script check (line ~821) compares against `OutputKey`.
- `FeeInputSelector` candidates include SP coins (§3.5). Channel funding (`LockUtxosToSpendOnChannel`), the interactive-tx wallet contributor, anchors CPFP fee inputs and `walletrpc` PSBT signing all work with SP coins, because SP coins are ordinary `Utxos` rows.
- SP coins as **inputs of another SP send**: their `a_i = d` (NL-1257's derivation).
- Acceptance:
  - in-process: receive to SP, then spend the coin in `withdraw` to a BIP86 address, as a fee input, in a channel funding, and to another SP address; each verified by NBitcoin's interpreter;
  - the vectors' spend signatures reproduced through the signer path (not only `Bip352`);
  - D-SP13 mixing preference tests.

**NL-1264 SP-R6: accounting (S)**
- Per §3.8: source and label on `WalletReceived`; change classified as a self-transfer; reversal on reorg; rescan findings.
- Acceptance: `Application.Tests` accounting projections for an SP receive, an SP spend, a reorg reversal and a rescan finding; `reconcile` shows no drift after each.

**NL-1265 SP-R7: rescan and restore (M)**
- `SilentPaymentRescanService`, `SilentPaymentScanState`, IPC 54 `sprescan` and 55 `spstatus`, the birthday (D-SP7), the pruned refusal (D-SP8), the spend tracking of §3.6, the `RescanBlocksPerSecond` limit.
- Note the backup-metadata follow-up (§3.6).
- Acceptance:
  - regtest in-process: node A receives three SP payments (one labeled) over 50 blocks and spends one. A fresh node from the same key file, with an empty database, runs `sprescan --from-height <birthday>` and ends with the same unspent set and accounting balances;
  - a reorg during the rescan rewinds the cursor;
  - the rescan and live scanning never scan one block twice (asserted by a counter);
  - pruned refusal message.

**NL-1266 SP-R8: labels and change (S)**
- IPC 52 `--label`, IPC 53 `splabels`, `MaxLabels`, `RecoveryLabelCount`, the change label always scanned, `ChangeToSilentPayment` (D-SP5, D-SP6) with insertion at confirmation (§3.4 step 7).
- Acceptance:
  - the vectors' label cases through the scanner (even and odd parity, the large label integer, labeled and unlabeled outputs to the same recipient);
  - a labeled receipt carries its name into accounting;
  - with `ChangeToSilentPayment`, the change of an SP send is a `m = 0` coin that is spendable after confirmation and found again by `sprescan`.

### Wave SP-T: proofs

**NL-1267 SP-T1: regtest e2e between two NLightning nodes and interop with a reference wallet (L)**
- **Cluster e2e** (new suite `silentpayments`, or a class in an existing bitcoind-only suite; `scripts/run-cluster.sh`; one namespace). Two `NLightningTestNode`s on Core 31.1:
  1. A pays B's `sprt1` address, labeled and unlabeled;
  2. B finds both, in ZMQ mode and in Poll mode;
  3. B spends them in a channel open to A;
  4. a two-block reorg mid-way;
  5. B is wiped and restored by `sprescan`;
  6. accounting reconciles on both.
- **Interop:** a reference wallet on the same regtest, packaged as an image `nltg-spike-spwallet:<commit>`. Recommended: a small CLI over the Rust `silent-payments` crates (the `rust-silentpayments` lineage, plus SPDK, which builds on it), because it runs headless in a pod.
  - The reference wallet pays our address, and we find it.
  - We pay its address, and its full-node scanner finds it.
  - Bitcoin Core's own wallet support, if a release carries it by then, is an alternative: PR #35301 (BIP 352 logic in `common/`) merged 2026-09-23; wallet sending is #35302 and receiving #32966, both open at the time of writing.
  - Sparrow 2.5+ and Cake Wallet need a GUI or an Electrum/Frigate server, so they suit only a manual signet check. Record that manual check in §8 if done.
- Acceptance: the suite green 3 times in a row on the cluster (`-n 3`); interop both directions green once.

### Wave SP-X: extras

**NL-1268 SP-X1: optional light tweak-index source (M, optional)**
- `IBlockTweakSource`, for nodes whose bitcoind cannot serve prevouts (pruned rescans without undo data). It fetches per block the 33-byte `input_hash·A` per eligible transaction from:
  - **(a) a BlindBit Oracle** (v2: gRPC `StreamComputeIndex`/`StreamBlockScanDataShort`, deprecated HTTP `/tweaks/:height`; it needs an unpruned Core ≥ 30 with REST);
  - **(b) Bitcoin Core's index**, if one lands. PR #28241 (`-bip352index`, `getsilentpaymentblockdata`) was closed in 2025, so none exists today.
- **Trust model:** the server learns nothing secret. We still verify each candidate match against the block we already have (the outputs). A lying server can only hide payments, so the source is opt-in and never the default.
- Privacy: the source sees which blocks we ask for; go through Tor when `Node:Tor` is on, as the price client does (NL-677).
- Acceptance: on regtest with a BlindBit container, a pruned node rescans a range below its prune height and finds the same outputs as an unpruned run.

**NL-1269 SP-X2: LND gRPC visibility, and refusing SP for splice-out, sweeps and closes (S)**
- `ListUnspent`/`GetTransactions`/`ListAddresses` show SP coins (D-SP10).
- `spliceout --address sp1…` (`WalletSpliceOutDestination.ResolveAsync`), `closechannel` upfront and close scripts, sweep destinations and `openchannel` dual-fund change refuse SP addresses with the D-SP11 text.
- Acceptance: `LndGrpc.Tests` for the three RPCs including the NL-1253 non-double-count pin; Application tests for each refusal.

**NL-1270 SP-X3: mainnet decision and canary (S + owner time)**
- An owner decision on D-SP2 and the defaults of D-SP1 (`Enabled` on every network?).
- A mainnet canary with an amount the owner names (memory: "ask amount"):
  - send to and receive from an independent wallet (Sparrow 2.5+ or Cake);
  - a labeled receipt;
  - a spend;
  - a rescan from the birthday on the canary node, using source (2) on an unpruned node.
- The plan's §8 records the run.
- Acceptance: the record plus the owner's decision. Turning the gate open is one commit that changes the default and the CLAUDE.md sentence.

## 5. Test strategy

1. **Spec vectors.** The 28 cases of BIP 352 1.1.1 run three ways:
   - through the maths (NL-1256);
   - through the send service (NL-1257);
   - through the scanner inside `StageBlockAsync` (NL-1262).
   - Plus the classifier over every `vin` (NL-1256). The vector file is vendored unchanged and its SHA-256 is asserted in a test, so an update is deliberate.
2. **Functional tests the BIP lists:**
   - taproot inputs without key-path keys are excluded from coin selection (our selector only picks coins we hold, so pin it with a test);
   - re-derivation on input change;
   - malleated P2PKH, script-path inputs, every input type.
3. **In-process wallet tests:** `Infrastructure.Bitcoin.Tests` and `Application.Tests` with the chain-monitor harnesses (stepped clocks, `SilentZmqEndpoint` from `test/NLightning.Tests.Utils/Mocks/`, never a fixed ZMQ port: NL-310), the SQLite persistence harnesses, crash injection between the scan stage and the save (the unit of work makes it atomic; prove it).
4. **Cluster:** the NL-1267 suite through `scripts/run-cluster.sh` (CLAUDE.md "Running the integration suites"; flake rule: rerun only the failed class); the NL-1261 live prevout tests (Bitcoin Core 31.1, including a node without txindex); the Postgres round trip in `postgres`.
5. **Benchmarks:** `tools/NLightning.SilentPaymentBenchmark`: per-block scan time and the adversarial K_max block (§3.7). They are not part of CI; their numbers go into §8.
6. **Standard cycle:** net10.0 only (`-f net10.0`), net11.0 compile check, `dotnet format`, no new warnings, Release and Release.Native builds (crypto code is in Infrastructure.Bitcoin, which does not switch backends, but build both anyway).

## 6. Security and privacy

- **Scan key.**
  - It must be online and in memory whenever `Receive` is on. Its compromise reveals every incoming SP payment and its amount (past and future), but cannot spend.
  - Keep it in a locked buffer in `SecureKeyManager`, never in a managed array longer than one call. No interface returns it. It never goes to a remote scanner, a tweak server or a log.
  - There is no "export scan key" command. Watch-only export can be a later, separate decision.
- **Spend key.** Derived on demand and wiped, like deposit keys.
- **Tweaks.** `SilentPaymentOutputs.Tweak` (`t_k`) is stored in the database. With the spend public key it identifies our outputs. With the spend private key it spends them. Alone it spends nothing, but it is as privacy-sensitive as the address index of an ordinary wallet coin.
  - Database at-rest protection is the operator's, as today (`SECURITY_REVIEW.md`).
  - Add an SR entry for it in `docs/agents/SECURITY_REVIEW.md` in NL-1260.
- **Key file.** No change to the file or its versions. SP keys derive from the master already held. v1/v2 nodes are not recoverable by other wallets (D-SP4).
- **Linking.**
  - Spending several SP outputs in one transaction links them to one owner (common-input heuristic), which undoes the unlinkability SP gives. Mixing SP coins with BIP86/P2WPKH coins links them too.
  - D-SP13 makes the selector avoid it by default and log when it must. Channel funding and fee inputs follow the same preference.
  - Labels never separate identities: every labeled address shares `B_scan`.
- **Change fingerprint.** D-SP6 (P2TR change for SP sends).
- **Sender privacy.** The receiver learns nothing about our other inputs beyond the transaction itself, as with any payment.
- **Never** do Frigate-style remote scanning with our scan key.
- **Dust and spam.** Anyone can pay tiny outputs to our address. D-SP12 ignores outputs below the minimum, so they never become selectable inputs (an output we spend would link to the attacker's transaction). The `k` loop still advances.
- **DoS cost of scanning.**
  - Every eligible transaction on chain costs us one constant-time multiplication, whatever its outputs. A block full of eligible transactions is the normal worst case (§3.7 budget).
  - A transaction with many outputs aimed at us costs up to `K_max` iterations. The BIP bounds it, and the benchmark measures it.
  - Scanning runs on chain data only (no peer input), so an attacker pays mining fees for every unit of our work.
  - The scan never blocks HTLC deadlines: the parallel pre-pass runs outside the unit of work, and block processing is already serialized. Still, a slow scan delays the next block's processing, so the warning budget and the metric are there to see it.
- **Fail closed.**
  - A block whose prevouts cannot be read halts processing, as other block failures do, instead of skipping it and silently losing payments.
  - A send whose inputs changed after derivation is refused before broadcast.

## 7. Open questions and risks

1. **Performance of managed secp256k1.** NBitcoin.Secp256k1 is a managed port. Constant-time multiplications cost several hundred µs each, so 2,000 eligible transactions per block may take about 0.5-1 s even when parallel. Mitigations, in order: parallelism; the variable-time path for public-only maths; batching the label lookups. As a last resort, libsecp256k1 0.8.0's silentpayments module (released 2026-08-03, full-node scanning, PR #1765) through P/Invoke behind the `ISilentPaymentCrypto` port. That would add a native dependency (see `CRYPTO_NATIVE`), so it is an owner decision if needed.
2. **REST `spenttxouts`.** Verified on Core 31.1: `-rest` is required, `-txindex` is unnecessary, and the payload contains ordinary serialized `CTxOut` values. Old pruned blocks cannot be recovered; see §8.
3. **Backend scope.** The owner explicitly selected Bitcoin Core for this implementation and proof. rbitcoin compatibility has not been verified by this work and is not an acceptance requirement.
4. **Core wallet support is still in review** (#35302 send, #32966 receive). Interop proofs rely on Rust tooling until a Core release ships it. If Core picks a different default regtest HRP or path, follow Core and record it here.
5. **The BIP is "Complete" but still versioned** (1.1.0 added K_max in March 2026, 1.1.1 a vector in April 2026). New vectors must be pulled deliberately (pinned SHA, §0).
6. **Schema change to `Utxos`.** Making the address FK nullable touches every wallet reader. The NL-1260 audit is the risk control, and a missed reader would mis-sign or crash on an SP coin. The NL-1263 in-process spends through every path are the safety net.
7. **Accounting dedup on rescan.** Confirm in NL-1264 that the A1 dedup key cannot collapse a rescan finding into a live one, or the reverse after a reorg.
8. **Taproot channels.** A MuSig2 funding output that we spend in a cooperative close is P2TR and eligible, but its key is shared, so we can never be the SP sender in a close (D-SP11). Receiving is unaffected. A peer's close paying to an SP address of ours would need the peer's key material; it is not possible, and not planned.
9. **Backups.** Static channel backups carry no birthday today. Without one the operator must remember it, or rescan from the key file's `HeightOfBirth`.

## 8. Record

### NL-1261: actual Core prevout evidence (2026-10-07)

The owner selected normal Bitcoin Core and removed rbitcoin proof from the scope. The first real cluster preflight used `bitcoin/bitcoin:31.1@sha256:da25cedc66b1daefff9f412ee196c901a899c3fa68a33b20849c3e08b5c40d63`. `SilentPaymentPrevoutClusterTests` passed both explicit facts. It compared `getblock 3`, REST `spenttxouts`, and `getrawtransaction` on seven candidate transactions: P2PKH, P2SH-P2WPKH, P2WPKH, P2TR key path, P2TR script path, P2WSH, and a child spending an earlier output in the same block. Every input amount and script agreed. A second node with no txindex returned identical `getblock 3` and REST prevouts.

REST requires `-rest`. Its actual payload is CompactSize transaction count, including coinbase; per transaction, CompactSize input count; per input, ordinary `CTxOut` serialization: signed int64 little-endian satoshis followed by CompactSize script length and script bytes. Coinbase has zero previous outputs. This is not the compressed on-disk undo `Coin` format. The parser rejects missing/truncated outputs, negative amounts, noncanonical lengths, mismatched counts, and trailing bytes; body reads are asynchronous and cancellable with bounded buffers.

The actual wire capture is retained in `test/NLightning.Infrastructure.Bitcoin.Tests/Wallet/Fixtures/core31-prevouts.json`, extracted from `TestResults/cluster/sp-core-preflight1/sp-core-preflight1-1/output.log` by `scripts/silent-payments/extract-prevout-capture.py`. The fixture records the image digest and SHA256 of each captured wire value:

- raw block: `abc11459e37db6cbd8ccb767ab80171ba9f4fff1f7998817c4b2fe3d28747e4e`;
- original getblock-3 JSON response: `6f35dac0fbf28c77e05fcd114f65f5710335700087a2b10ed4f8d017596215f2`;
- REST bytes: `007f457c53b63d19a4720b8f8f0ac1202b6c3af70dd97ebdca7407f23e1e85c0`.

A separate Core pod with `-prune=550 -fastprune=1`, no txindex, and 1001 generated blocks reported `pruneheight=508`. Core refused old block10 as pruned; the source refused height10 before a recovery cursor could be written. Full silent-payment flow completion is tracked separately: this first preflight proved independent-wallet interoperability in both modes, but the reorg fixture timed out after tip shrink and required an empty replacement branch before the remaining recovery/channel proof could run.

## References

- BIP 352: https://github.com/bitcoin/bips/blob/master/bip-0352.mediawiki (v1.1.1), vectors and `reference.py` in `bip-0352/`.
- Bitcoin Core:
  - PR #35301 "Silent Payments: Implement bip352 (take 2)" (merged 2026-09-23; `common/bip352`, HRPs `sp`/`tsp`/`sprt` in `kernel/chainparams.cpp`);
  - #35302 (sending), #32966 (receiving);
  - #28122 (first attempt, closed 2026-05-12);
  - #28241 (silent payment index, closed 2025-02-20);
  - #32540 (REST `spenttxouts`, merged 2025-06-27).
- libsecp256k1 v0.8.0 silentpayments module (PR #1765, full-node scanning; light-client API PR #1912 open).
- BlindBit Oracle (setavenger/blindbit-oracle v2); Frigate (Sparrow's scanning server); the Rust `silent-payments` crates and SPDK (macgyver13/spdk); Cake Wallet; Sparrow 2.5.0 (SP receive, May 2026); Dana wallet.
- In-repo precedents:
  - `Crypto/Musig2/Bip327.cs` (own crypto port with vectors);
  - `Wallet/Imports/ImportedTapscriptTracker.cs` (backfill with a checkpoint, NL-1197);
  - `Bootstrap/LightningNodeIdBech32.cs` (bech32m beyond 90 characters through NBitcoin's encoder);
  - `LndGrpcOptions`/`CashuPaymentProcessorOptions` (`Enabled` + `AllowMainnet` gating).
