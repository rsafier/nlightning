# Cashu (ecash) integration plan

Branch: `wip/cashu`. Epic: NL-900. Written 2026-10-03.

NLightning already does everything a Cashu mint needs from a Lightning backend:
- receive BOLT 11 and BOLT 12;
- pay with MPP, retries and fee limits;
- an on-chain wallet;
- Tor-routed HTTP;
- built-in accounting.

This plan adds ecash in waves. The first waves make NLightning a backend that the reference mint can run on. The later ones bring ecash into the node itself.

## 1. State of Cashu and CDK (October 2026)

### CDK (`cashubtc/cdk`, Rust, v0.18.x)

**Crates**
- `cashu`: protocol types.
- `cdk`: wallet and mint library.
- `cdk-mintd`: the mint daemon.
- `cdk-cli` and `cdk-mint-cli`.
- `cdk-axum`: the HTTP API.
- `cdk-signatory`: a remote gRPC blind signer, so the mint keys live outside the mint process.
- `cdk-payment-processor`: see below.
- Lightning backends `cdk-lnd`, `cdk-cln` and `cdk-ldk-node`.
- Storage on sqlite, postgres or redb.

**Recent releases**
- 0.18: the mint configuration lives in the mint database. "lightning" was renamed "payment backends". KeysetService and QuoteService management APIs were added. Also NUT-16, deterministic NUT-12 nonces and NUT-18 constraints.
- 0.17: NUT-30 on-chain mint and melt.
- 0.12: BOLT 12 end to end. `cdk-ldk-node` runs a mint and a Lightning node in one binary, which is the shape this plan reaches from the other side.

**Bindings**
`cdk-ffi` (UniFFI) has bindings for Python, Swift and Kotlin only. There is **no C# binding**, so a .NET node either speaks the CDK wire contracts or implements the NUTs itself.

### The payment processor contract

This is the seam C1 plugs into. `crates/cdk-payment-processor/src/proto/payment_processor.proto` defines `service CdkPaymentProcessor`. `cdk-mintd` uses it with `ln_backend = "grpcprocessor"`.

| RPC | Meaning for the backend |
|---|---|
| `GetSettings` | unit, and which methods (bolt11 / bolt12 / onchain / custom) and options it supports |
| `CreatePayment` | create an incoming request (a mint quote): an invoice, an offer or an address |
| `GetPaymentQuote` | price an outgoing payment (a melt quote): amount and fee reserve |
| `MakePayment` | pay (a melt), with an optional partial amount (NUT-15 MPP) and a max fee |
| `CheckIncomingPayment` | what was received for a request id |
| `CheckOutgoingPayment` | the state of an outgoing payment |
| `WaitPaymentEvent` | server stream of `payment_received` / `payment_successful` / `payment_failed` |

Key enums:
- `PaymentIdentifier` types: `PAYMENT_HASH`, `OFFER_ID`, `LABEL`, `CUSTOM_ID`, `QUOTE_ID`, `PAYMENT_ID`.
- `QuoteState` values: `UNPAID`, `PAID`, `PENDING`, `UNKNOWN`, `FAILED`, `ISSUED`.

`cashubtc/cdk-payment-processors` ships processors for Bark, LDK Server, LNbits and Spark, plus a template. Only LDK Server does BOLT 12, and only Bark does on-chain.

### NUTs

NUTs 00-06 are mandatory. The optional ones that matter here:

| NUT | Topic |
|---|---|
| 07 | Token state check |
| 08 | Overpaid fees |
| 09 | Restore |
| 10 / 11 | Spending conditions / P2PK |
| 12 | DLEQ |
| 13 | Deterministic secrets |
| 14 | HTLCs. SHA-256 hashlock, the same hash as a Lightning payment hash, so ecash can be tied atomically to an LN HTLC. |
| 15 | MPP |
| 17 | WebSockets |
| 18 | Payment requests. Transports: nostr, HTTP POST, in-band. |
| 20 | Signed mint quotes |
| 21 / 22 | Clear and blind auth |
| 23 | BOLT 11 |
| 24 | HTTP 402 |
| 25 | BOLT 12 |
| 26 | Bech32m payment requests |
| 27 | Nostr mint backup |
| 28 | P2BK |
| 29 | Batched mint |
| 30 | On-chain |

### Ecosystem

- Orchard: one console for a mint and its Bitcoin and LN infrastructure.
- Numo: Android POS.
- Routstr: pay-per-request AI.
- Hashpool: ecash mining shares.

## 2. NLightning seams

| Need | Where |
|---|---|
| Create an invoice | `IInvoiceService.CreateInvoiceAsync` (Domain `Payments/Interfaces`), with `SourceLabels` |
| Invoice settled | `HtlcSwitch.SettleInvoiceAsync`, inside the fulfill's (or mark's) save. Once per invoice, MPP included. |
| Pay | `IPaymentService.PayInvoiceAsync(bolt11, amount, PayInvoiceOptions)` returns `PayInvoiceResult`. `PaymentModel` has Preimage, Fee and Status. |
| Fee quote | `IRouteQueryService.QuoteRouteAsync(payee, amount, maxFee, finalCltv)` (`getroute`) |
| BOLT 12 | `IOfferService` (receive), `IOfferPaymentService.PayOfferAsync` (pay) |
| On-chain | `IWalletSpendService` (withdraw), `GetUnusedAddressAsync` |
| BDHKE math | `ISecp256K1Math` (point add/multiply). Hash-to-curve and DLEQ are to be added in Infrastructure.Bitcoin. |
| Custom messaging | `IOnionMessageHandler` (payload types ≥ 64), keysend custom records |
| Mint HTTP | `TorHttpHandler`, `HttpResponseLimits`, `HttpUrlPolicy` (template: `AddAccountingPriceSources`) |
| Accounting | `PaymentAccountingEvents.TryStage`, `AccountingEventKind`, `AccountingPostingRules` (there is no liabilities role yet) |
| External API | Named-pipe IPC only. **No gRPC or HTTP server before C1.** |

Before C0 there was no "invoice paid" or "payment finished" notification, only polling. There are no hold invoices.

## 3. Ideas, ranked by value ÷ effort

1. **NLightning as a CDK payment processor (C1).** Stock `cdk-mintd` runs its mint on our node.
   - BOLT 11, BOLT 12 and on-chain in one backend; no shipped processor has all three.
   - Interop with the reference mint.
   - No Rust in our tree.
2. **Payment event stream (C0).** In-process events after commit, and a `waitinvoice` long-poll.
   - Needed by C1.
   - Unblocks NWC, LNURL and webhooks later.
3. **Docker proof (C2).**
   - A `cdk-mintd` container on our processor and `cdk-cli` as the wallet.
   - The run: mint (we receive), melt to LND (we pay), BOLT 12 melt, restart with quotes pending.
4. **Native C# Cashu wallet (C3).**
   - NUT-00..05, 07, 09, 11, 12, 13, 17 and 23, byte-exact against the NUT test vectors.
   - `nltg cashu receive <token>` melts a token into one of our own invoices: ecash becomes channel liquidity.
   - `nltg cashu send <amount>` mints from our node and prints a token.
   - Proofs go in their own table and their own accounting bucket.
5. **Hold invoices and NUT-14 atomic swaps (C4).**
   - A hold-invoice decorator around `IHtlcSwitch`, following the `DustExposureHtlcSwitch` pattern, with a CLTV guard against the deadline monitor.
   - Ecash locked to the payment hash gives trust-minimized LN↔ecash swaps between a node and a mint.
6. **Ecash over Lightning transports.**
   - NUT-18 payment-request replies carried as a custom onion-message type, an "in-band LN transport" that could be proposed as a NUT.
   - A P2PK/P2BK-locked token in a keysend custom record ("tip in ecash").
7. **Mint-operator liquidity tooling.**
   - A view of mint float against channel balances.
   - Melt fee quotes that use mission-control success probabilities.
   - Splicing and dual funding for melt capacity.
8. **Embedded C# mint**, as `cdk-ldk-node` is to LDK.
   - Keysets, blind signing, the spent-secret DB, the NUT HTTP and WebSocket API, and a liabilities role in the books.
   - Large and security-heavy. Deferred until C1/C2 show demand; C1 with a `cdk-mintd` sidecar already gives "one box runs the mint".

## 4. Waves

| Wave | Content | Issue | Status |
|---|---|---|---|
| C0 | `IPaymentEventSource` / `IPaymentEventPublisher` (Domain), `PaymentEventHub` (Application), published after commit by `HtlcSwitch` (invoice settled) and `PaymentService` (payment succeeded/failed); `waitinvoice` (IPC 47) | NL-901 | in progress |
| C1 | `NLightning.Cashu.PaymentProcessor`: the `CdkPaymentProcessor` gRPC service on Kestrel in the daemon, behind `Cashu:PaymentProcessor` (BOLT 11) | NL-902 | built, unit-proven over real gRPC; Docker proof is C2 |
| C2 | Docker proof against `cdk-mintd` + `cdk-cli` | NL-903 | open |
| C3 | Native Cashu wallet | NL-904 | open |
| C4 | Hold invoices + NUT-14 | NL-905 | open |

### C0 design

**Event types** (`Domain/Payments/Events/`)
- `InvoiceSettledEvent(PaymentHash, Amount, OccurredAt)`
- `PaymentSucceededEvent(PaymentHash, Amount, Fee, Preimage, OccurredAt)`
- `PaymentFailedEvent(PaymentHash, Reason, OccurredAt)`

**When events are raised**
- Each event is raised only after the save that made it true has committed. A subscriber that reads the database on an event sees the new state.
- A save that fails raises nothing.

**Subscribers**
- Each subscriber gets a bounded queue.
- On overflow the oldest event is dropped and the subscription is flagged `Overflowed`. The consumer then resynchronizes from the database.
- Events are memory-only. A consumer that reconnects (`cdk-mintd` after a restart) asks for state with the Check RPCs, as the CDK contract already expects.

**`waitinvoice <payment_hash> [--timeout <s>]`**
- Subscribes first, then reads the invoice, then waits. A settle between the read and the wait is never missed.
- Answers with the invoice when it is no longer `Open`, or with its current state on timeout.
- The timeout defaults to 60 s, with a maximum of 3,600 s.

### C1 design

**Project**
- `src/NLightning.Cashu.PaymentProcessor`, referencing Domain and Application.
- Holds the vendored proto, pinned to the CDK release it was taken from, and the service implementation.
- Exposes `AddCashuPaymentProcessor(configuration)` and a hosted Kestrel server.

**Configuration (`Cashu:PaymentProcessor`)**

| Key | Default |
|---|---|
| `Enabled` | `false` |
| `ListenAddress` | `127.0.0.1` |
| `Port` | `50051` |
| `TlsDirectory` | — |
| `Unit` | `sat` |
| `AllowMainnet` | `false` |
| `MaxFeePercent` | — |
| `InvoiceExpirySeconds` | — |

**Startup checks**
- A non-loopback listen address without TLS is refused at start.
- Mainnet is refused unless `AllowMainnet`.

**Mapping**

| RPC | Mapping |
|---|---|
| `CreatePayment` | bolt11 → `CreateInvoiceAsync` (label `cashu-mint`); the identifier is `PAYMENT_HASH`. bolt12 and onchain answer `UNIMPLEMENTED` in the first cut. |
| `GetPaymentQuote` | Decode the bolt11 (amount, payee) and quote the route. Fee reserve = max(route fee, the node's default fee limit), and never more than `MaxFeePercent`. The identifier is the invoice's payment hash. |
| `MakePayment` | `PayInvoiceAsync` with `MaxFee` = the request's `max_fee_amount`. Payment states map as Succeeded→PAID, Failed→FAILED, InFlight→PENDING. `total_spent` = amount + fee. `payment_proof` = the preimage. |
| `CheckIncomingPayment` | The invoice by hash: Settled → one `WaitIncomingPaymentResponse` with the received amount, otherwise none. |
| `CheckOutgoingPayment` | The payment by hash. |
| `WaitPaymentEvent` | Streams `PaymentEventHub`: invoices the processor created (label `cashu-mint`) → `payment_received`; payments it made → `payment_successful` / `payment_failed`. |

**Amounts**
- Converted with `LightningMoney`, in the configured unit (`sat` or `msat`).
- A received amount in sat is floored. An amount to pay that is not whole sats is refused for `sat`.

## 5. Open decisions

1. **In-process versus sidecar.**
   - In-process (chosen for C1): direct access to the services and the event hub; one process to run. The cost is Kestrel in the daemon.
   - Sidecar: would need the IPC to grow push events first.
2. **NativeAOT.**
   - The daemon cannot run the node under AOT yet (NL-708).
   - `Grpc.AspNetCore` supports AOT with source-generated protobuf code.
   - The AOT analyzer must stay at 0 warnings in the daemon build.
3. **Exposure.**
   - Default loopback with no TLS, for a mint on the same host.
   - Anything else needs the TLS directory with the CDK layout (`server.pem`/`server.key`, `ca.pem` for mTLS client verification).
4. **Mainnet.** Off unless `AllowMainnet`. A mint is a custodial service: the operator owes the outstanding ecash.
5. **Accounting.**
   - The first cut labels mint invoices and payments `cashu-mint` (A3 labels), so their books show the mint's flows.
   - A liabilities role for outstanding ecash belongs to C3/embedded-mint work.


## 6. C1 as built (NL-902)

**Project:** `src/NLightning.Cashu.PaymentProcessor` (references Application, Bolt11, Domain; `FrameworkReference Microsoft.AspNetCore.App`, `Grpc.AspNetCore.Server` 2.76.0).
- `Protos/payment_processor.proto` is CDK v0.18.1's, byte-identical apart from `option csharp_namespace`. Both server and client are generated; the client serves the tests and the C2 proof.
- `CashuPaymentProcessorOptions`: section `Cashu:PaymentProcessor`, settable properties only (AOT binder).
- `CdkPaymentProcessorService`: the RPC mapping.
- `CashuPaymentProcessorHost`: its own `WebApplication.CreateSlimBuilder` Kestrel instance with HTTP/2 only, started as a hosted service after `NltgDaemonService`. It does nothing while disabled.
- `AddCashuPaymentProcessor(configuration)` is called in `AddNltgNodeServices`; `ValidateOnStart` runs against the node's network. `AddCashuPaymentProcessorHost()` is called in `ConfigureNltgServices`.

**Behavior**
- `GetSettings`: unit `sat` or `msat`; bolt11 `{mpp: false, amountless: true, invoice_description: true}`; bolt12 and onchain unset.
- `CreatePayment` (bolt11): `IInvoiceService.CreateInvoiceAsync` with the label `cashu-mint`. The identifier is `PAYMENT_HASH` hex. `expiry` is the invoice's absolute expiry.
- `GetPaymentQuote` (bolt11):
  - Decodes the invoice for the node's network and refuses an expired one.
  - Takes the amount from the invoice, or from the `amountless` option.
  - Fee reserve = max(`MinFeeReserveMsat` 5,000, amount × `FeeReservePpm` 5,000 / 10^6). These are the node's default fee limit.
  - Both values are rounded up to the unit.
  - Refuses `mpp` options, and a unit other than ours.
- `MakePayment` (bolt11):
  - `PayInvoiceAsync`, with `MaxFee` = `max_fee_amount` (or the reserve), the label, and a wait of `PaymentTimeoutSeconds` (60). Payment states map as Succeeded→PAID, Failed→FAILED, in flight→PENDING.
  - `total_spent` = amount + fee, rounded up. `payment_proof` = the preimage hex.
  - A duplicate hash answers with the stored payment.
  - A bad invoice or amount is `InvalidArgument`.
  - The request's `quote_id` is remembered for the event stream.
- `CheckIncomingPayment`: a Settled invoice gives one payment. The received amount is rounded down. `payment_id` is the hash.
- `CheckOutgoingPayment`: the stored payment, or `UNKNOWN`.
- `WaitPaymentEvent`: one `IPaymentEventSource` subscription per stream.
  - `payment_received` for invoices settled with our label.
  - `payment_successful` / `payment_failed` for payments with a remembered quote id.

**Startup refusals**
- An enabled processor on mainnet without `AllowMainnet`.
- A non-loopback `ListenAddress` without `TlsDirectory`.
- A `TlsDirectory` without `server.pem`/`server.key`.
- A unit other than `sat`/`msat`.

**TLS**
- `ca.pem` in the `TlsDirectory` turns on mTLS: every client certificate must chain to that CA alone.
- `cdk-mintd`'s `tls_dir` holds `ca.pem`, `client.pem` and `client.key`.

**Not yet**
- BOLT 12 (`OfferService`/`IOfferPaymentService`), on-chain (NUT-30) and MPP melts (NUT-15).
- Quote ids for `WaitPaymentEvent` are kept in memory. After a restart the mint learns outcomes through `CheckOutgoingPayment`.

**Tests:** `test/NLightning.Daemon.Tests/Cashu/`. `CdkPaymentProcessorServiceTests` (10) run a real Kestrel server on a free loopback port and call it with the generated client; `CashuPaymentProcessorOptionsTests` cover the startup checks.

### Running a mint on NLightning

`appsettings.json` of the node:

```json
"Cashu": { "PaymentProcessor": { "Enabled": true, "ListenAddress": "127.0.0.1", "Port": 50051, "Unit": "sat" } }
```

`cdk-mintd` `config.toml` (v0.18):

```toml
[payment_backend]
backend = "grpcprocessor"
unit = "sat"

[grpc_processor]
address = "127.0.0.1"
port = 50051
# tls_dir = "/path/to/tls"   # ca.pem, client.pem, client.key (mTLS); then set the node's TlsDirectory too
```
