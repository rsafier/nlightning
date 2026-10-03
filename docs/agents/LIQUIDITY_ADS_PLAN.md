# Liquidity ads plan (NL-850)

Status: **done** (2026-10-03, NL-850 fixed at `10b8ba84`): both roles in the dual-funded open, its RBF, the splice and the splice RBF, with restarts, the lease guard, accounting and the operator surface; proven in-process (NLightning ↔ NLightning, both roles) and against Eclair 0.14.3 as seller (Docker, buyer role only). Owner decision 2026-10-03 to build liquidity ads (BOLT PR #1153 as Eclair 0.14.3 speaks it) instead of the LSPS family. Tasks LA0..LA7 below; the record section at the end tracks what was built and the follow-ups (NL-851..NL-859).

## Context

The owner decided to build **only liquidity ads**: no LSPS0/1/2/5. Liquidity ads are peer-to-peer and live inside the protocol. A seller advertises rates. A buyer asks for inbound liquidity inside `open_channel2`, `tx_init_rbf` or `splice_init`. The seller contributes the amount and signs its commitment to the rate. The fee moves from the buyer's balance to the seller's in the new commitment, with no extra transaction output.

NLightning already has the hard parts:
- dual-funded opens with RBF in both roles (`Channels/DualFunding/`);
- splicing with RBF (`Channels/Splicing/`);
- the interactive-tx driver and wallet contributor;
- the gossip graph.

Interop target: **Eclair 0.14.3**, already a Docker fixture (`test/Docker/eclair`, `EclairFixture`). Its public API can only **sell**: `open`, `rbfopen` and `splicein` hardcode `requestFunding_opt = None`. So the proofs are:
- NLightning buying from an Eclair seller (Docker);
- NLightning ↔ NLightning in both roles (in-process).

## Wire format (as Eclair 0.14.3 implements #1153)

- **One temporary TLV tag, 1339**, used everywhere:
  - `request_funding` in `open_channel2`, `tx_init_rbf`, `splice_init`;
  - `provide_funding` in `accept_channel2`, `tx_ack_rbf`, `splice_ack`;
  - `option_will_fund` rates in `init` and in `node_announcement`.
- **No feature bit.** Support is detected from the peer's `init` TLV.
- **`funding_rate`:** `u32 min_funding_amount_sat | u32 max_funding_amount_sat | u16 funding_weight | u16 funding_fee_basis | u32 funding_fee_base_sat | u32 channel_creation_fee_sat`.
- **`request_funding`:** `requested_sats` (u64 sat), then `funding_rate`, then `payment_details` (TLV-encoded; `from_channel_balance` = type 0, empty).
- **`will_fund`:** `funding_rate`, then a u16-length `funding_script`, then a 64-byte signature.
- **`will_fund_rates`:** u16 count + rates, then a u16-length `payment_types` bitfield. Bit 0 is `from_channel_balance`; bits 128-130 are Eclair's on-the-fly types.
- **Fees:**
  - mining fee = `funding_weight × funding_feerate_perkw / 1000`;
  - service fee = base + `channel_creation_fee` (new channel only) + `min(requested, contributed) × basis / 10,000`.
  - Total = mining + service. It is deducted from the buyer's next balance and added to the seller's.
- **Signature:** ECDSA, `Crypto.sign(SHA256("liquidity_ads_purchase" || funding_rate || funding_script), nodeKey)`. This fits `ILightningSigner.SignNodeMessage` / `VerifyNodeMessage` as they are.
- **Checks:**
  - Seller (`validateRequest`): the payment type is supported, the rate is one of ours, and `min ≤ requested ≤ max`.
  - Buyer (`validateRemoteFunding`): the signature, `remote contribution ≥ requested`, and the same rate.
  - #1153: dropping `request_funding` in an RBF attempt after requesting it before MUST fail the negotiation.
  - The seller SHOULD keep the channel open for about a month. Nothing enforces this; the buyer's only recourse is blacklisting.

## Decisions (defaults the plan follows)

- **D-L1:** speak Eclair 0.14.3's wire exactly: tag 1339 as one constant, switched in one place when #1153 merges.
- **D-L2:** only `from_channel_balance`. The other payment types are parsed, never offered and never accepted.
- **D-L3:** selling is off until rates are configured (`Node:LiquidityAds:FundingRates` is empty by default). Buying is always available. Same on every network.
- **D-L4 (lease):**
  - Our cooperative `closechannel` of a sold channel inside 4,032 blocks is refused unless `--force`; force closes for safety are never blocked.
  - The buyer records each purchase. `listliquiditypurchases` shows a seller that closed early.
- **D-L5 (griefing guard):** at most `MaxConcurrentSales` (per peer and node-wide) sale negotiations hold wallet inputs at once. Requests from peers without a channel are rate-limited.

## Tasks

Order: LA0 → LA1 → LA2 → (LA3 ∥ LA4) → LA5 → LA6 → LA7. LA3 owns the single migration.

### LA0: groundwork and capture
- **Docs and ledger:** plan doc `docs/agents/LIQUIDITY_ADS_PLAN.md` (this content plus a record section) and a ledger epic (next free NL id) in `docs/agents/ISSUES.md`.
- **Eclair capture:** an `Explicit` capture test against Eclair configured as a seller. It records:
  - Eclair's `init` rates TLV and its `node_announcement`;
  - Eclair's `provide_funding` answering a hand-built `request_funding` from our node.
  - Pattern: `ClnBolt12CaptureTests` plus a recording `IMessageSerializer` decorator.
  - Output: `Tests.Utils/Vectors/LiquidityAdsEclairVectors.cs`.
  - It also confirms what `funding_script` is (expected: the new funding output's P2WSH script) and the exact `eclair.liquidity-ads.funding-rates` config syntax.
- **Fixture:** `EclairFixture.BuildConfig` (around :222-240) gains optional seller rates (`liquidity-ads.funding-rates`, `payment-types = ["from_channel_balance"]`).

### LA1: Domain model, codecs and rules
New folder `src/NLightning.Domain/LiquidityAds/` (BCL-only):
- `FundingRate`, `RequestFunding`, `WillFund`, `WillFundRates`, `LiquidityPaymentType`, and `LiquidityAdsCodec` (strict, canonical, byte-exact against the LA0 vectors).
- `LiquidityAdsRules`:
  - `ComputeFees(rate, requested, contributed, feeratePerKw, isNewChannel)`;
  - `ValidateRequest(ourRates, request)`;
  - `ValidateWillFund(request, willFund, remoteContribution, verify)`;
  - `SignedData(rate, fundingScript)`.
- Tests in `test/NLightning.Domain.Tests/LiquidityAds/`: round trips, the Eclair vectors, the fee table, every refusal.

### LA2: wire plumbing
- **TLV classes and converters:** `RequestFundingTlv`, `ProvideFundingTlv` and `WillFundRatesTlv` (one class per meaning; the factory is keyed by type), registered in `TlvConverterFactory`, with a sample in `TlvStreamSerializerTests.CreateSampleTlvs`.
- **Known-type sets** and typed reads in the serializers under `src/NLightning.Infrastructure.Serialization/Messages/Types/`: `OpenChannel2`, `AcceptChannel2`, `TxInitRbf`, `TxAckRbf`, `SpliceInit` and `SpliceAck` (restructure their early return when TLV 2 is absent), and `Init`. Add the TLV last in each `Extension`.
- **Builders:**
  - `MessageFactory` (`CreateOpenChannel2Message` :445, `CreateAcceptChannel2Message` :514, `CreateTxInitRbfMessage` :280, `CreateTxAckRbfMessage` :316, `CreateSpliceInitMessage`/`CreateSpliceAckMessage` :775/:786, `CreateInitMessage` :42) take optional request/provide/rates arguments.
  - `InteractiveTxDriver` (:219, :612) and `SpliceService.Rbf.WithContribution` (:898-913) must carry the TLV through, not drop it.
- **Init:** we send our rates when we sell (`FeatureOptions.GetInitTlvs` or the factory). The peer's rates are kept as `IPeerService.LiquidityRates`, read in `PeerService` around :631-686.
- **node_announcement:**
  - `NodeAnnouncementService` (:135-137) puts our `option_will_fund` into `ExtraData`;
  - `AnnouncementFields.Matches` (:194-200) includes the rates, so a rate change re-announces;
  - a Domain helper reads other nodes' rates from `GraphNode.RawAnnouncement`.
- **Proof:** serializer round trips per message, including the Eclair captures.

### LA3: seller (accepter side) and persistence
- **Options:** `Node:LiquidityAds` (`FundingRates[]`, `PaymentTypes`, `MaxConcurrentSales`, `MaxPerPeer`, `LeaseBlocks` 4032), validated, plus the daemon config template.
- **Dual-fund accepter** (`DualFundedOpenService.AcceptAsync` :522, `GetAcceptContribution` :509, `StartAccepterAsync` :747):
  - on `request_funding`, validate it;
  - contribute the requested amount (`InteractiveTxContributionRequest.WalletAmount`);
  - sign `will_fund` over the funding script;
  - answer `provide_funding`.
  - If the request is invalid, or the wallet cannot fund it: `error` for an open (Eclair's behavior), `tx_abort` for RBF and splice.
  - RBF: `DecideRbfAsync` :1037 re-validates against the new attempt and re-signs.
- **Splice accepter:** `SpliceService.HandleSpliceInitAsync` :274 (today it contributes 0 at :315/:324/:336), reserving the contribution like `ReserveWalletContributionAsync` :1291. Its RBF path is `HandleTxInitRbfAsync` (`SpliceService.Rbf.cs:263`).
- **Fee transfer in balances**, for both roles, leaving the shares and contributions themselves unchanged:
  - `DualFundedOpenService.CreateChannel` :1594-1610, `ApplyAttemptFunding` :1697, `OnFundingConfirmedAsync` :1381;
  - the splice `ChannelFunding` deltas in `CreateSpliceCommitmentSignedAsync` :560-566;
  - the reserve check `SpliceRules.CheckTxComplete`/`KeepsReserve` (:464-510) counts the fee.
- **Persistence** (migration `AddLiquidityPurchases`, all 3 providers):
  - table `LiquidityPurchases`: channel id, funding txid, role, requested, contributed, rate, payment type, mining/service fee, signature, funding script, lease start height, created; since the integration review (NL-871, migration `AddLiquidityPurchaseMaxFee`) also the buyer's own fee limit, which an RBF repeating the purchase keeps.
  - Restart paths that assume balance = share use it: `GetOrLoadAsync` :1292, `TryGetSignedAttempt` :1468.
  - The repository is on `IUnitOfWork`, with a throwing default and test wrappers forwarding it.
- **Lease guard:** `ChannelCloseService`/coordinator refuses our cooperative close of a sold channel inside the lease unless forced.
- **Griefing guard:** D-L5.

### LA4: buyer (initiator side)
- **Requests:** `DualFundedOpenRequest`, `SpliceRequest` and bump requests gain `LiquidityRequest(amountSat, rate?, maxFeeSat)`. The rate defaults to the cheapest compatible rate in the peer's `init`, or else in its `node_announcement`.
- **Sending and checking:**
  - `request_funding` goes on `open_channel2` (`OpenAsync`), `splice_init` (`SpliceService.StartAsync` :122/:261) and every RBF (`BumpAsync`). An RBF must keep requesting (#1153).
  - The answer is checked with `ValidateWillFund`: signature through `VerifyNodeMessage`, amount, rate, and fee ≤ `maxFeeSat`. On failure: `tx_abort` (or `error` before the interactive tx), and the result reports why.
  - The purchase is saved in the same save as our `commitment_signed`.
- **IPC/CLI:**
  - `OpenChannelIpcRequest` keys 9/10, `SpliceInIpcRequest` keys 3/4 and `BumpOpenIpcRequest` keys 3/4 carry `RequestFundingSat`/`MaxLiquidityFeeSat`.
  - Client flags `--request-inbound <sat> [--max-liquidity-fee <sat>]` on `openchannel --dual-fund`, `splicein` and `bumpopen`.
  - New `ClientCommand` 46 `liquidityads` with sub-actions:
    - `sellers`: rates from the graph and from connected peers' `init`;
    - `purchases`: bought and sold, lease status;
    - `rates`: ours.
  - Next free command becomes 47.

### LA5: accounting
- **Event kinds** in the free 21-29 range: `LiquidityFeePaid` / `LiquidityFeeEarned` (amounts = mining + service, with both parts in the details), staged in the save that stores the purchase. Keys are tied to channel + funding txid; an RBF that replaces the attempt reverses the old one.
- **Posting rules:** Channels ↔ the new expense/income roles. Financial chart defaults: `expenses:fees:liquidity`, `income:liquidity`.
- **Fee-share fixes:**
  - `ChannelAccountingEvents` `ChannelFunded` (:129-131) uses the session's contribution, never a balance skewed by the fee;
  - `SpliceLocked` (:247-300, `GetLocalFeeShareMsat`) subtracts the liquidity fee from the delta.
- **Proof:** BooksSimulator nets, reconcile clean, reports list liquidity fees.

### LA6: proofs
- **In-process** (`DualFundHarness`, `SpliceHarness(realEngine: true)`), NLightning ↔ NLightning in both roles:
  - buying at open, at RBF (re-purchase, fee at the new feerate) and at a splice;
  - balances after the fee transfer;
  - payments each way;
  - restarts at every step;
  - every refusal: bad signature, short amount, wrong rate, fee above the maximum, a request dropped in RBF, unsupported payment type, the griefing caps;
  - the lease guard.
- **Docker** (`Docker/Interop/Eclair/EclairLiquidityAdsTests`, `Category=Interop.Eclair`), with Eclair configured as seller, NLightning:
  - (a) sees Eclair's rates in `init`;
  - (b) buys at `openchannel --dual-fund --request-inbound`: Eclair contributes ≥ requested, and our balances and Eclair's both reflect the fee;
  - (c) buys through `splicein --request-inbound`;
  - (d) buys again at `bumpopen` (RBF);
  - (e) a payment each way on each;
  - (f) refuses a rate it did not ask for (hand-crafted through the capture hook).
  - Run with `scripts/run-interop.sh eclair`.

### LA7: integration
- Docs: plan record, root `CLAUDE.md`, `src/NLightning.Application/CLAUDE.md`, `BOLT_COVERAGE.md`.
- `SECURITY_REVIEW.md`: UTXO griefing, the unenforced lease, signature scope.
- Config template, ledger, and a full non-Docker run plus the Eclair interop suite.

## Verification

- Every task: `dotnet build -c Release`, `dotnet format --verify-no-changes`, non-Docker tests on net10.0, and `HasPendingModelChanges` false on all 3 providers after LA3.
- LA1/LA2: byte-exact against the captured Eclair vectors.
- LA6: the in-process suites green, and `scripts/run-interop.sh eclair` green with the new class (Eclair 0.14.3 as seller).
- End to end:
  1. Configure Eclair as seller.
  2. `nltg openchannel <eclair> 500000 --dual-fund --request-inbound 400000`.
  3. Check `listchannels`: our balance = 500k − fee, remote = ≥ 400k + fee.
  4. `nltg liquidityads purchases` shows the purchase.
  5. `nltg accounting report fees` shows the liquidity fee.

## Record

- 2026-10-03: plan written (LA0). Eclair 0.14.3 facts checked against its sources (`LiquidityAds.scala`, `ChannelTlv.scala`, `SetupAndControlTlv.scala`, `RoutingTlv.scala`, `InteractiveTxBuilder.scala`, `reference.conf`, `api/handlers/Channel.scala`): tag 1339 for every TLV, ECDSA signature over `SHA256("liquidity_ads_purchase" || funding_rate || funding_script)` by the node key, fee moved in the next balances (`nextLocalBalance = ... - liquidityFee` for the buyer), config `eclair.liquidity-ads { funding-rates = [...], payment-types = ["from_channel_balance"], lock-utxos-during-funding = true }`, no feature bit (on-the-fly funding 560/562 is separate), and the public API never buys (`requestFunding_opt = None`).
- LA1 built: Domain `LiquidityAds/` (`FundingRate`, `RequestFunding`, `WillFund`, `WillFundRates`, `LiquidityPaymentDetails`, `LiquidityFees`, `LiquidityAdsCodec`, `LiquidityAdsRules`, `LiquidityAdsConstants.TlvType` 1339). Byte-exact against Eclair 0.14.3's own test vectors (`Tests.Utils/Vectors/LiquidityAdsEclairVectors`: the init rates with an unknown type 211, tx_init_rbf/tx_ack_rbf/splice_init/open_channel2/accept_channel2 TLV values, the fee table of `LiquidityAdsSpec`); `LocalLightningSigner.SignNodeMessage` over `SignedData` reproduces Eclair's two `will_fund` signatures exactly (`Infrastructure.Bitcoin.Tests/Signers/LocalLightningSignerLiquidityAdsTests`). The capture task of LA0 is no longer needed for the codec (Eclair's own vectors cover it); the Docker suite (LA6) is the live interop proof.
- LA2 built (`19f2733f`, `53668ca4`): `RequestFundingTlv`/`ProvideFundingTlv`/`WillFundRatesTlv` (one class per meaning, tag 1339) with strict converters, read and written on `init`, `open_channel2`, `accept_channel2`, `tx_init_rbf`, `tx_ack_rbf`, `splice_init`, `splice_ack` (byte-exact round trips of Eclair's `LightningMessageCodecsSpec` messages, `Serialization.Tests/Messages/LiquidityAdsMessageTests`); undecodable rates in `init` are dropped (odd, advisory), a malformed request or answer fails the message. `IMessageFactory` builders take the request/answer, the RBF and splice builders carry them through, `Node:LiquidityAds` (`LiquidityAdsOptions`: `FundingRates`, `MaxConcurrentSales` 4, `MaxSalesPerPeer` 1, `LeaseBlocks` 4032, `MaxFeeSat`), our rates in `init` and in our `node_announcement` (`NodeAnnouncementRates`, a rate change re-announces; Eclair's announcement vector parsed and its signature checked), `IPeerService.LiquidityRates`.
- LA3 persistence built (`c23e2bc1`): `LiquidityPurchaseModel` (Pending/Active/Replaced/Closed, lease from the confirmation, `ClosedEarly`), `IUnitOfWork.LiquidityPurchaseDbRepository`, table `LiquidityPurchases` (migration `AddLiquidityPurchases` on all three providers, unique per channel and funding attempt, no FK so a record outlives its channel).
- LA5 built (`074ca1f1`): `AccountingEventKind.LiquidityFeePaid` 21 / `LiquidityFeeEarned` 22 (amount = the change of our channel bucket), roles `LiquidityFees` (`expenses:fees:liquidity`) and `LiquidityIncome` (`income:liquidity`), financial chart and lot kinds, the channels/peers report columns, `ChannelAccountingEvents.RecordLiquidityPurchaseAsync`/`RecordLiquidityPurchaseReplacedAsync` and the `liquidityFeeMsat` of `ChannelFunded`/`SpliceLocked`. Decision: the fee event is staged with the funding confirmation's `ChannelFunded` (open) or the lock's `SpliceLocked` (splice), so reconcile never sees the fee before the channel balance carries it, and a replaced RBF attempt never booked one.
- LA3/LA4 groundwork (`21674bc8`): `LiquidityRequest` on the open, splice and bump requests, the purchase on their results, and `Application/LiquidityAds/LiquidityAdsService` (seller sale slots for the D-L5 caps, `will_fund` signed by the node key, the seller's cheapest compatible rate from its `init` or announcement, the answer checked with our fee limit). The flows (dual-funded open, splice, IPC/CLI, lease guard, Eclair Docker) are the next lanes.
- LA3/LA4 splice flows (`9875f08c`): `SpliceService.Liquidity.cs` and `SpliceLiquidity` (memory only, on the attempt's `SpliceNegotiation`). Buyer = the sender of `splice_init`/`tx_init_rbf` with `request_funding`; seller = the side that answers `provide_funding` in `splice_ack`/`tx_ack_rbf` and contributes exactly the requested amount from its wallet (`ReserveSaleContributionAsync`, reserved like a splice-in; we pay only for our own inputs and outputs as interactive-tx non-initiator). The fee (mining + service, no channel creation fee for a splice) lives in the `ChannelFunding` balance deltas: the buyer's delta is its contribution less the fee, the seller's its contribution plus the fee, so both commitments on the new funding, the lock and the on-chain resolution carry it while the shared funding's shares stay the contributions; `GetContributions` takes the fee back out with the attempt's purchase row (a resumed negotiation, an RBF rebuilt from the latest attempt). New rule **LA-RES-01** (`SpliceRules`, `SpliceTxCompleteFacts.LiquidityFeeMsat`): the buyer must keep its reserve of the new capacity after paying the fee (checked before we ask, `CheckBuyerCanPay`, by the seller on `splice_init`, and at `tx_complete`). One purchase row per attempt (kind `Splice`/`SpliceRbf`) staged in the save of our splice `commitment_signed`; the lock's save marks the locked attempt's row Active and its siblings' Replaced and books the fee next to `SpliceLocked`.
- LA3/LA4 dual-funded flows (`9eed6bd6`): `Channels/DualFunding/DualFundLiquidity` (`ApplyFee`/`RemoveFee`/`GetBalanceViolation`), `DualFundLiquidityRequest`, `DualFundLiquidityAccounting`. The buyer's `request_funding` goes on `open_channel2` (`OpenAsync`) or `tx_init_rbf` (`BumpAsync`); the answer is checked (`CheckWillFund`: `ValidateWillFund` with our fee limit, then the balances) before anything is funded. The seller (`AcceptAsync`, `DecideLiquidityRbfAsync`) validates the request and takes a sale slot (`LiquidityAdsService.TryStartSale`), contributes exactly the requested amount and signs `will_fund` over the funding output's script. The first commitment's balances are the shares with the fee moved (`ApplyFee`); on restart they are rebuilt from the purchase row (`RemoveFee` gives the shares back, `GetOrLoadAsync`, `TryGetSignedAttempt`, `ApplyAttemptFunding` for whichever attempt confirms). Purchases are staged in the commitment step's save (`RecordPurchase`, kind `ChannelOpen`/`OpenRbf`); at the funding confirmation `DualFundLiquidityAccounting.StageFundingConfirmedAsync` marks the confirmed attempt's row Active (lease from that height), the other attempts' pending rows Replaced, and stages `LiquidityFeePaid`/`LiquidityFeeEarned` in the `ChannelFunded` save (the late path `ChannelAccountingEvents.RecordLateChannelFundedAsync` for a Failed/OnchainResolving channel too). An abandoned first attempt (never signed) marks its pending purchase Closed.
- Refusals, both flows: the buyer refuses with an `error` before the interactive tx (an open whose `accept_channel2` lacks `provide_funding` or carries a bad one) and with `tx_abort` after it (splice, RBF); the seller refuses an open with an `error` (Eclair's behavior) and a splice or RBF with `tx_abort`. **RBF after a purchase must request again** (#1153): our bump repeats the latest attempt's purchase on its own (same amount and rate, the fee at the new feerate) unless a new request is given, and a peer's `tx_init_rbf` without `request_funding` after a purchase gets `tx_abort` (`InvalidRbfMissingLiquidityPurchase`). Only the buyer bumps a sale (our `bumpsplice` of a splice we sold in is refused with `LA-RBF-01`, our `bumpopen` of an open attempt we sold in likewise), and `Onchain/Fees/SpliceAutoBumper` skips splices we sold in (`d517fa63`). Griefing caps (D-L5): `MaxConcurrentSales` 4 node-wide and `MaxSalesPerPeer` 1, in memory, released when the negotiation ends; no separate rate limit for peers without a channel.
- LA4 operator surface and the lease guard (`9be1f1f0`): `openchannel <peer> <amount> --request-inbound <sat> [--max-liquidity-fee <sat>]` (OpenChannel request keys 9/10, response key 3 `Purchase`; implies a v2 open, refused with `--v1` or a push amount), `splicein ... --request-inbound <sat> [--max-liquidity-fee <sat>]` (`SpliceInIpcRequest` keys 3/4, `SpliceIpcResponse` key 6), `bumpopen ... [--request-inbound <sat> [--max-liquidity-fee <sat>]]` (`BumpOpenIpcRequest` keys 3/4, response key 2; without it the previous attempt's purchase is repeated), `bumpsplice` (no liquidity option: it repeats the purchase), `closechannel --force` (`CloseChannelIpcRequest` key 4), and `ClientCommand.LiquidityAds = 46`: `nltg liquidityads rates|sellers|purchases [--role buyer|seller] [--status pending|active|replaced|closed] [--skip <n>] [--limit <n>]` (alias `liquidity-ads`; `rates` ours or that we do not sell, `sellers` connected peers' `init` rates then `node_announcement`s, `purchases` newest first with the lease status; next free command 47). Lease guard (D-L4, `Application/LiquidityAds/LiquidityLeases`): our `closechannel` of a channel with a seller purchase whose lease is in force (Pending, or Active before `LeaseStartHeight + LeaseBlocks`) is refused unless `--force` (`ChannelCloseService.ThrowIfLeaseInForceAsync`); the peer's closes and `forceclosechannel` are never blocked; `StageChannelClosedAsync` marks the channel's Pending/Active purchases Closed (`ClosedEarly` inside the lease, logged as a warning) in the save that closes the channel (`ChannelManager` for a mutual close, `OnchainResolutionExecutor` for an on-chain one). The daemon's config template writes `Node:LiquidityAds` (no rates: selling off).
- LA6 proofs. In-process: `Application.Tests/Channels/DualFunding/DualFundLiquidityAdsTests` (buy in the open, booked at the confirmation; RBF repeats the purchase at the new feerate or buys another amount; the first attempt confirming after a bump; restarts with a lost `commitment_signed`, between attempts and during an RBF attempt), `DualFundLiquidityAdsRefusalTests` (every refusal, the caps), `DualFundLiquidityTests`, `Channels/Splicing/SpliceLiquidityAdsTests` (buy, use and lock; the buyer's bump repeats or buys more; the fee limit; the seller's bump refused; a dropped request; restarts on each node and with both `commitment_signed` lost; bad requests and tampered answers; a buyer that cannot pay; a peer that sells nothing), `LiquidityAds/LiquidityAdsServiceTests`, `LiquidityLeasesTests`, the Domain `LiquidityAds/` tests, `Serialization.Tests/Messages/LiquidityAdsMessageTests` (byte-exact against Eclair's `LightningMessageCodecsSpec`), `Integration.Tests/BOLT7/LiquidityAdsNodeAnnouncementVectorTests`, `Infrastructure.Bitcoin.Tests/Signers/LocalLightningSignerLiquidityAdsTests` (Eclair's `will_fund` signatures reproduced). Docker (`9778b4b2`, `10b8ba84`): `Docker/Interop/Eclair/EclairLiquidityAdsTests` against a second Eclair 0.14.3 configured as seller (`EclairFixture.GetSellerAsync`, container `nltg-eclair-seller`, `SellerRates` 10k-5M sat, weight 400, 500 sat + 100 bp, 1,000 sat channel creation fee), **5/5 green**: (a) its rates in its `init` and in `liquidityads sellers`; (b) the open with `--request-inbound` refused, then a plain open; (c) a splice purchase (`splicein 100000 --request-inbound 300000`): balances on both ends, purchase row Active, payments both ways; (d) `bumpsplice` re-buying (a `SpliceRbf` purchase at the higher feerate) and locking, payments both ways; (f, `Explicit`) a capture of Eclair's `provide_funding` in `splice_ack`, whose `will_fund.funding_script` is the splice output's script. The live init rate vector is `LiquidityAdsEclairVectors.LiveInitRates`.
- **Eclair policy finding (changed proofs (b) and (d)):** Eclair 0.14.3 without an open-channel interceptor plugin does **not** sell in a new channel (`OpenChannelInterceptor.checkLiquidityAdsRequest`: "We don't honor liquidity ads for new channels: node operators should use plugin for that"; only on-the-fly-funding wallets get it): its `accept_channel2` carries no `provide_funding`, so our buyer refuses the open (the answer is `Missing`). It does sell in `splice_init` and in a splice RBF by default. So (b) proves the refusal and a plain open afterwards instead of a purchase at the open, and (d) proves the re-purchase at `bumpsplice` instead of `bumpopen`; buying at the open and at `bumpopen` are proven in-process only. The Eclair image could not be built here through `docker build` (curl inside the build does not trust the agent proxy's CA, NL-858); it was built from a host-downloaded release zip with the same sha256.
- LA7 (this record, the root and layer `CLAUDE.md`s, `test/CLAUDE.md`, `BOLT_COVERAGE.md`, `SECURITY_REVIEW.md` SR-28..SR-31, the ledger). Full non-Docker run on net10.0 at the merge: 13,866 passed, 4 skipped (Domain 4339, Application 3738, Integration 1067, Infrastructure.Bitcoin 1617 (2 skipped), Daemon 1358, Infrastructure 663 (2 skipped), Serialization 633, Bolt11 343, Testing.Lnd 108); Docker `EclairLiquidityAdsTests` 5/5; the whole `Category=Interop.Eclair` suite in one run: 31 passed, 2 `Explicit` not run, 1 failed (`EclairCloseTests`' simple close by Eclair timed out waiting for our Closed state; the class passes 4/4 alone, NL-859).
- **Status: done** (NL-850 fixed at `10b8ba84`). Follow-ups: NL-851 (the late funding confirmation path with a purchase, `RecordLateChannelFundedAsync` for Failed/OnchainResolving, has no test), NL-852 (purchases of RBF attempts that never confirm stay Pending until the funding confirmation marks them Replaced; an abandoned first attempt's are marked Closed), NL-853 (`bumpsplice` has no `--request-inbound` to change the amount), NL-854 (a seller dual-fund RBF whose share must grow beyond the earlier inputs is refused with `tx_abort` instead of adding fresh inputs), NL-855 (the accepter buying in its own dual-fund RBF is allowed but untested), NL-856 (a peer buying in an RBF of a splice we also spliced in replaces our contribution with exactly the requested amount; unchecked against Eclair), NL-857 (no NLightning-as-seller proof against another implementation: Eclair's API never buys, CLN and LND do not speak #1153; the seller role is proven in-process only), NL-858 (the `test/Docker/eclair` Dockerfile's curl fails behind a TLS-intercepting proxy), NL-859 (the full Eclair category run's simple-close flake).
