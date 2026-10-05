# C# 15 pilot: SDK 11, unions, net10.0 kept

> Pilot NL-1086, branch `wip/c15pilot` from `wip/fafo` at `6ad5f292` (2026-10-05). Owner's question: "compiling always
> on .NET 11 (SDK 11) with polyfills is fine if it leans things out; I want an idea of the pros and cons."

## Toolchain

- **SDK 11.0.100-rc.1.26425.128** (`/usr/local/share/dotnet`, the same build as the user-local `~/.dotnet11`). The
  release metadata (`dotnetcli.blob.core.windows.net/.../11.0/releases.json`, read 2026-10-05) lists rc.1 (2026-09-08)
  as the latest release, support phase "go-live": no rc.2 or GA yet, so nothing new was installed.
- Compiler: the Roslyn shipped in that SDK (`sdk/11.0.100-rc.1.26425.128/Roslyn/bincore/csc.dll`), **`LangVersion 15`
  is accepted** (no `preview` needed).
- Runtimes used by the tests: Microsoft.NETCore.App 10.0.12 and 11.0.0-rc.1.26425.128 (and AspNetCore of both).

## What changed in the build

| File | Change |
|---|---|
| `global.json` | `version 11.0.100-rc.1.26425.128`, `rollForward latestMajor`, `allowPrerelease true`. SDK 10 can no longer build the repo. (`11.0.100` alone does not work while only rc builds exist: `11.0.100-rc.1` sorts below `11.0.100`.) |
| `src/Directory.Build.props`, `test/Directory.Build.props` | `LangVersion 15`; `TargetFrameworks net10.0;net11.0` always (the SDK-version gate `NltgSdkVersion` is removed, not left inert: with SDK 11 required it could only ever be true); `-p:NltgTargetNet11=false` still builds net10.0 only; `WarningsAsErrors CS8509` (a switch expression that misses a union case or enum value fails the build; the tree had 0 of them). |
| `src/Shared/Polyfills/UnionPolyfills.cs` | `internal sealed class System.Runtime.CompilerServices.UnionAttribute : Attribute` (`AttributeUsage(Class \| Struct, AllowMultiple = false, Inherited = false)`) and `internal interface IUnion { object? Value { get; } }`: the exact shape of the net11.0 types (read by reflection from `System.Runtime` 11.0.0-rc.1). Compiled only into projects with `NltgUnionPolyfill=true` and only for net10.0 (an `ItemGroup` in `src/Directory.Build.props`); Domain is the only such project. |
| `src/NLightning.Domain/NLightning.Domain.csproj` | `NltgUnionPolyfill=true`. Domain stays BCL-only, no project or package reference changed. |
| `.github/workflows/*` | Every workflow that runs `dotnet` in the repo installs SDK 11 (`dotnet-quality: preview`): the Wasm, gh-pages and combined-report jobs gained the step (they had SDK 10 only and would fail on `global.json`); the Wasm build passes `-p:NltgTargetNet11=false`. Not run (CI is not exercised from here). |

How the polyfill behaves:
- **net11.0** compiles without the file and binds the runtime's public types; there is nothing to clash with, so no
  CS0436/CS1685. On net10.0 the internal copies are the only ones.
- **Consumers in other assemblies need nothing**: Application, Infrastructure and the tests match on Domain's unions on
  net10.0 with no polyfill of their own (the compiler recognizes the union by the attribute in Domain's metadata). Proven
  in a two-project probe and by the pilot itself (Application's resolvers and handler, Tests.Utils' views).
- Rule: two assemblies that both carry the polyfill must not see each other's internals (`InternalsVisibleTo`), or
  CS0436 reports the duplicate type. Today only Domain has it, and Domain's IVT goes to Domain.Tests only.

## Conversions

Line counts exclude blank and comment lines (`//`, `///`), measured per file against the parent commit.

### 1. `ReestablishPlan` (Domain `Channels/Reestablish`, commit `45ae7fb4`)

Before: an outcome enum plus a record whose fields only make sense for some outcomes.

```csharp
public enum ReestablishOutcome { Resume, Fail, DataLoss }
public sealed record ReestablishPlan(ReestablishOutcome Outcome, IReadOnlyList<ReestablishStep> Steps,
    string? RequirementId = null, string? Reason = null, bool MustBroadcast = false, TxId? PeerSpliceLocked = null)
{ /* Resume(...), Failed(...), LostData(...) factories */ }
```

After:

```csharp
public union ReestablishPlan(ReestablishPlan.Resume, ReestablishPlan.Fail, ReestablishPlan.DataLoss)
{
    public sealed record Resume(IReadOnlyList<ReestablishStep> Steps, TxId? PeerSpliceLocked = null);
    public sealed record Fail(string RequirementId, string Reason, IReadOnlyList<ReestablishStep> Steps,
                              bool MustBroadcast = false);
    public sealed record DataLoss(string Reason) { public const string RequirementId = "B2-RE-23"; }
}
```

The planner returns `new ReestablishPlan.Fail(...)` / `new ReestablishPlan.Resume(steps, peerSpliceLocked)` (no more
`with { PeerSpliceLocked = ... }`). `ChannelReestablishMessageHandler` gets the steps from an exhaustive switch
expression and fails/persists from a `switch` statement over the cases; `PersistDataLossAsync` takes the `DataLoss` case.

| File | Lines before → after |
|---|---|
| `ReestablishPlan.cs` | 34 → 23 (−11) |
| `ReestablishPlanner.cs` | 189 → 190 (+1) |
| `ChannelReestablishMessageHandler.cs` | 365 → 381 (+16) |
| product total | 588 → 594 (+6) |
| tests: `Tests.Utils/Channels/ReestablishPlanView.cs` (new) + 1 `using` in 3 test files | +44 |

### 2. `ResolutionAction` (Domain `Onchain`, commit `c0cdf599`)

Before: a kind enum (7 values) and one record with five optional fields, each valid for one or two kinds.

```csharp
public sealed record ResolutionAction(ResolutionActionKind Kind, string RequirementId, SweepSpendKind? SpendKind = null,
    bool OnSecondLevel = false, uint? WaitUntilHeight = null, uint? DeadlineHeight = null, byte[]? Preimage = null);
// consumer
switch (action.Kind) {
    case ResolutionActionKind.Wait:
        waitUntil = waitUntil is { } earlier ? Math.Min(earlier, action.WaitUntilHeight!.Value) : action.WaitUntilHeight;
```

After: seven case records carrying only their own data (non-nullable where the planner always sets it).

```csharp
public union ResolutionAction(ResolutionAction.Wait, ResolutionAction.BroadcastHtlcTimeoutTx,
    ResolutionAction.BroadcastHtlcSuccessTx, ResolutionAction.Sweep, ResolutionAction.RaiseFulfilled,
    ResolutionAction.RaiseFailed, ResolutionAction.AlertLostFunds)
{
    public sealed record Wait(string RequirementId, uint UntilHeight);
    public sealed record BroadcastHtlcSuccessTx(string RequirementId, uint DeadlineHeight, byte[] Preimage);
    public sealed record Sweep(string RequirementId, SweepSpendKind SpendKind, bool OnSecondLevel = false,
                               uint? DeadlineHeight = null, byte[]? Preimage = null);
    // ...
}
// consumer
switch (action) {
    case ResolutionAction.Wait wait:
        waitUntil = waitUntil is { } earlier ? Math.Min(earlier, wait.UntilHeight) : wait.UntilHeight;
```

`LocalCommitResolver`'s four anchors HTLC helpers now take `ResolutionAction.BroadcastHtlcSuccessTx? success` instead of
a whole action (the timeout transaction never had a deadline or preimage: the type now says so).
`OutputResolutionPlan.Has/Get(kind)` were used by tests only and moved to the test view.

| File | Lines before → after |
|---|---|
| `ResolutionActionKind.cs` (deleted) | 11 → 0 |
| `OutputResolutionPlan.cs` | 15 → 21 (+6) |
| `OutputResolutionPlanner.cs` | 285 → 281 (−4) |
| `LocalCommitResolver.cs` | 1056 → 1055 (−1) |
| `RemoteCommitResolver.cs` | 911 → 909 (−2) |
| `RevokedCommitResolver.cs` | 777 → 776 (−1) |
| product total | 3055 → 3042 (−13) |
| tests: `Tests.Utils/Onchain/ResolutionActionView.cs` (new) + 1 `using` | +61 |

Null-forgiving reads removed: 4 (`WaitUntilHeight!.Value` twice, `Preimage!` twice); nullable property patterns
`{ Kind: RaiseFulfilled, Preimage: { } preimage }` → `case RaiseFulfilled fulfilled`: 3.

### 3. `StfuReceiveResult` (Domain `Channels/Quiescence`, commit `a943dd2e`)

Before: `readonly record struct StfuReceiveResult(QuiescenceState Next, QuiescenceViolation? Violation)`, built as
`new StfuReceiveResult(state, QuiescenceViolation.SecondStfu)` / `new StfuReceiveResult(next, null)`.

After: `public union StfuReceiveResult(QuiescenceState, QuiescenceViolation);` over the two **existing** types (no
case records); the rules `return QuiescenceViolation.SecondStfu;` or `return next;`, and the service tests
`if (QuiescenceRules.Receive(...) is QuiescenceViolation violation) throw ...`.

Lines: 0 net in product code (2 + 124 + 476 unchanged; each return line is shorter). The "state unchanged on a
violation" contract is now structural: a violation carries no state. Cost: the enum case is **boxed** (24 bytes per
violation, measured), harmless on this error path but a reason not to use value-type cases on hot paths.

### Totals and exhaustiveness

| Module | Product lines | Exhaustive switch expressions gained | Catch-all arms removed |
|---|---|---|---|
| Reestablish | +6 | 1 (handler's steps) | 0 (the old `switch (plan.Outcome)` had none, Resume fell through) |
| On-chain actions | −13 | 0 | 0 |
| Quiescence | 0 | 0 | 0 |

The honest result: **the type declarations get leaner and safer, but consumer code does not shrink**, because the
product code consumes these values in `switch` **statements** with `when` guards (the resolvers, the handler), and C# 15
checks exhaustiveness only for switch **expressions**. A union removes nullable fields and `!`, not branches. The test
views (+105 lines) exist only to keep the existing assertions byte-identical; writing new tests against the cases
(`Assert.IsType<ReestablishPlan.Fail>(plan.Value)`) would not need them.

### CS8509 demonstration

Deleting the `ReestablishPlan.DataLoss => []` arm of the handler's steps switch (Application, consuming Domain's union
across assemblies, on net10.0 through the polyfill and on net11.0):

```
ChannelReestablishMessageHandler.cs(168,26): warning CS8509: The switch expression does not handle all possible values
of its input type (it is not exhaustive). For example, the pattern
'NLightning.Domain.Channels.Reestablish.ReestablishPlan.DataLoss' is not covered.
```

With the pilot's `WarningsAsErrors CS8509` that is a build error. Reverted. A `switch` statement missing a case gets
**no** diagnostic (checked in a probe).

## Rough edges found

| Area | Finding |
|---|---|
| `dotnet format` (SDK 11) | Parses and formats union declarations and case patterns; the only finding in the pilot was an ordinary IDE0007 (`var`) on new code. `--verify-no-changes` passes. |
| IDE | Not testable here (no C# language server in this environment; Rider/VS not tried). Rider and VS need their C# 15 updates; until then expect red squiggles on `union`. |
| Analyzers / warnings | Release, Release.Native and the AOT analyzer builds of the daemon (`-r osx-arm64 -p:PublishAot=true`, `-f net11.0` and `-f net10.0`): 0 warnings. The union struct is a plain struct with one `object` field: nothing for the trimmer to flag. |
| `default(TUnion)` | Its `Value` is null. An "exhaustive" switch expression over it throws `SwitchExpressionException` at run time and the compiler says nothing (no nullable warning either). Never leave a union field defaulted; the pilot's `Get(kind)` test helper returns a default union when nothing matches. |
| `ToString()` | Prints the union's **type name** (`"U"`), not the case: log `plan.Value` or the case, never the union. |
| Equality | `Equals` works by value through the boxed case (records compare structurally), but **no `==` operator** (CS0019). |
| Value-type cases | Boxed on every assignment (enum case: 24 bytes). Class/record cases cost nothing extra. |
| `as` | `union as Case` does not compile; use `union.Value as Case` or a pattern. |
| Nullable unions | `TUnion?` is `Nullable<TUnion>`; `switch` with a `null` arm works; `!` does not unwrap it. |
| Pattern matching across projects | Works on both TFMs: `case ReestablishPlan.Fail fail`, property patterns (`Sweep { OnSecondLevel: true } sweep`), `or` patterns. |
| Implicit conversions | Each case type converts to the union: `return QuiescenceViolation.SecondStfu;`, collection expressions `[Alert(...)]` of union elements, and `cond ? new Sweep(...) : WaitFor(...)` all compile. |
| Union body | Nested case records and members are allowed inside `union X(...) { }`. |
| C# 14 extension members | Combine well with unions (the test views are `extension(ReestablishPlan plan) { ... }` blocks). |
| MessagePack / EF / config binder | Not crossed: none of the converted types is persisted, sent over IPC or bound from configuration. A union would need a custom MessagePack formatter (the AOT formatter gate would catch a missing one) and is not an EF value type; keep unions inside the Domain/Application boundary. |
| Build time | See "Gates". |
| SDK 10 | Gone as a build host: `dotnet` on PATH must be SDK 11 (this machine's default `~/.dotnet` is SDK 10.0.103 and fails on `global.json`); every script that calls `dotnet` (`run-cluster.sh`, the EF migration scripts, `aot-smoke.sh`) inherits that. |

## Gates

All on `a943dd2e` (the three conversions), macOS arm64, SDK 11.0.100-rc.1.26425.128.

| Gate | Result |
|---|---|
| `dotnet build NLightning.sln -c Release --no-incremental` (net10.0 + net11.0) | 0 warnings, 0 errors |
| `-c Release.Native --no-incremental` | 0 warnings, 0 errors |
| AOT analyzer build `src/NLightning.Daemon -c Release.Native -r osx-arm64 -p:PublishAot=true`, `-f net11.0` and `-f net10.0` | 0 warnings each |
| Real NativeAOT publish + smoke, `DOTNET=/usr/local/share/dotnet/dotnet scripts/aot-smoke.sh --framework net11.0` and `net10.0` | links (daemon 99/100 MB, client 15 MB), 7/7 smoke checks each; the ILCompiler's own trim/AOT warnings are the known NL-752 set, none names a converted type |
| `dotnet format --verify-no-changes --exclude "**/BlazorTests/**"` | clean (~105 s) |
| `python3 scripts/check-sln-configs.py` | OK, 36 projects |
| Non-Docker suite, `dotnet test -c Release --no-build --filter 'FullyQualifiedName!~Docker&FullyQualifiedName!~SqlServer' --blame-hang-timeout 5m` | **net10.0**: 16,561 tests, 16,552 passed, 8 skipped, 1 failed: `ClassificationEngineTests` "label regex matches", the known regex-timeout flake NL-729 (53/53 alone). **net11.0**: 16,547 passed, 8 skipped, 6 failed, all pre-existing (they fail the same way at `6ad5f292`, built with the same SDK and C# 14): 5 decimal-parsing tests (NL-1087: .NET 11 rc.1's `Convert.ToDecimal(double)` is exact) and `CashuPaymentProcessorHostTests.Given_MutualTls_*` (NL-1088). |
| Cluster smoke `scripts/run-cluster.sh --matrix taproot,lnd,onchain` (with SDK 11 on PATH; the runner builds and runs net10.0) | all green in 621 s: `lnd` 60/60, `onchain` 33/33 (+2 `Explicit` not run), `taproot` 10/10; no rerun |
| Build time, `--no-incremental` Release of the whole solution, both TFMs, warm compiler server, alternating runs | `6ad5f292` (C# 14): 23 s, 28 s; pilot (C# 15 + unions): 24 s, 22 s. No measurable difference. |

Per project (net10.0 = net11.0 totals): Domain 4,789, Application 4,310, Infrastructure.Bitcoin 1,991, Daemon 1,616,
Integration 1,192, Serialization 802, Infrastructure 738, Testing.Cluster 674, Bolt11 343, Testing.Lnd 106.

## Pros and cons

| | Pros | Cons |
|---|---|---|
| Types | "Exactly one of" is in the type: no enum + nullable fields, no `!`, no "only for kind X" doc comments; invalid combinations cannot be built. | Each module needs its case records; small declarations grow a little (on-chain plan +6). |
| Consumers | Switch expressions over a union are checked (CS8509, made an error); adding a case breaks every expression that misses it. | Switch **statements** (most of our dispatch, with `when` guards and `await`s) are not checked, so the safety only arrives where code is written as expressions. Consumer line counts did not drop. |
| Interop | Pattern matching across assemblies and both TFMs works; net10.0 needs only two internal types in one shared file. | `default` unions, `ToString`, no `==`, boxing of value-type cases: four new traps to document. Unions must stay out of MessagePack/EF/config. |
| Tooling | SDK 11 rc.1 compiler, `dotnet format`, trim/AOT analyzers all clean. | Requires SDK 11 everywhere (developers, CI, every script) while 11 is still rc ("go-live"); IDE support unverified; GA expected at .NET Conf, November 2026. |
| Tests | Unchanged assertions passed through thin extension views. | The views (+105 lines) are dead weight unless tests are rewritten against the cases. |

## Recommendation

**Adopt at GA, not now; keep this branch as the template.** The language feature works and the build costs are small
(two props lines, one polyfill file, SDK 11 in CI), but the gain in this codebase is type precision, not leaner code:
−7 product lines over three modules. Requiring a release-candidate SDK on every developer machine and CI job for that
is not worth it before GA. At GA (with `allowPrerelease: false`) the cost drops to "install SDK 11", and unions become a
good default for **new** result types.

Rollout at GA:
1. Merge the build commit (`cdde21f9`) with `global.json` `11.0.100`, `allowPrerelease false`; drop `dotnet-quality:
   preview` in CI. Keep net10.0 as a target (LTS until 2028-11): net10.0 consumers keep working because a union is an
   ordinary struct plus the internal polyfill, and nothing about it leaks into IPC or the database.
2. Use unions for new code where a result is one-of-several with per-case data, consumed by switch **expressions**.
   Candidates, in order: `SpliceResult` (state + nullable txid/capacity/failure reason), the interactive-tx session
   results, `PaymentService` attempt outcomes, the gossip ingress verdicts. Convert existing types only when touching
   them anyway.
3. Write new tests against the cases (`Assert.IsType<...>(x.Value)`), not through flat views.
4. Docs: root `CLAUDE.md` (Build section: SDK 11 required, LangVersion 15, the polyfill rule and the four traps in
   "Non-obvious gotchas"), `README.md` prerequisites, `docs/agents/NET11_PLAN.md`, `CONTRIBUTING.md`, `test/CLAUDE.md`
   (`dotnet` on PATH must be SDK 11 for `run-cluster.sh`).

Backing out: revert the three conversion commits (each is self-contained: the union, its consumers and its test view),
then the build commit (restores `global.json` 10.0.100, `LangVersion 14`, the SDK gate, removes the polyfill and the
CI steps). No schema, wire or configuration change is involved.
