# .NET 11 target plan

> Research run on 2026-09-25 against `wip/fafo` @ `b38ec28` with SDK `11.0.100-rc.1.26425.128` on a throwaway copy.
> **Implemented in ABCD wave 4, lane W4-C** (branch `wip/fafo-w4-w4c-dotnet11-s1`, from `b77481f`); see "As built" first.
> The research sections below are kept as they were written, with notes where the implementation differs.

## As built (W4-C)

User decision: **drop .NET 9 entirely**; multi-target `net10.0;net11.0`, with net11.0 gated on the SDK.

| Piece | As built |
|---|---|
| Target frameworks | `src/Directory.Build.props` and `test/Directory.Build.props` (keep the two in sync) set `TargetFrameworks=net10.0;net11.0` when the building SDK is 11 or newer, else `TargetFramework=net10.0`. Only one of the two is ever set: the SDK multi-targets only when `TargetFramework` is empty, so setting both silently builds net10.0 only. No csproj declares a framework any more, except the two Blazor projects (net10.0 only, Release.Wasm, CI). |
| SDK gate | `NETCoreSdkVersion` is **not** defined yet when `Directory.Build.props` is imported, so the props read the SDK version from `MSBuildBinPath` (`<dotnet>/sdk/<version>`) into `NltgSdkVersion`, and set `NltgTargetNet11` to `VersionGreaterThanOrEquals(NltgSdkVersion, 11.0)`. A path that doesn't end in a version (e.g. Visual Studio's MSBuild) builds net10.0 only. `-p:NltgTargetNet11=false` forces net10.0 only on SDK 11. |
| LangVersion | `14` in both props (was `latest` in src and in 8 test csprojs, unset in Daemon.Tests). With `latest`, SDK 11 would compile net10.0 as C# 15. |
| Daemon.Contracts / Daemon.Plugins | Their own `TargetFramework` (net9.0) and `LangVersion` removed; they inherit the src props. Microsoft.Extensions 9.0.9 → 10.0.12. |
| Microsoft.Extensions.* | Every 10.0.5 reference → 10.0.12 (Application, Infrastructure, Daemon, Domain.Tests). EF Core, Npgsql and EFCore.NamingConventions are **not** touched here (lane W4-A owns them). |
| IDE0031 (SDK 11 `dotnet format`) | The 4 sites now use the C# 14 null-conditional event assignment `x?.E += h` / `x?.E -= h`: `Bolt11/Models/Invoice.cs` (2), `Application/Node/Managers/PeerManager.cs` (the `_channelUpdateService` subscription), `Integration.Tests/Docker/Abcd/ChannelMessageRecorder.cs`. |
| `global.json` | `version 10.0.100`, `rollForward latestMajor`, `allowPrerelease true`: SDK 10 works, and the newest installed SDK (11 rc) is used when present. After 11 GA: `allowPrerelease false`. Caveat: `latestMajor` + prereleases will also pick a future SDK 12 preview if one is installed. |
| CI | `dotnet.yml`, `pr.yml`, `dotnet.native.yml`, `pr.native.yml` install SDK 10.0.x and then 11.0.x (`dotnet-quality: preview`, drop at GA), so build, format and tests run under SDK 11 on both frameworks (TRX and coverage files per TFM; ReportGenerator merges them). The Wasm jobs keep SDK 10 only (Blazor projects are net10.0; with SDK 10 the src projects build net10.0 only). `combined-report`/`gh-pages` unchanged (they only run report/doc tools). |
| EF scripts | `src/NLightning.Infrastructure.Persistence/scripts/{add,remove}_migration.sh` pass `--framework "$Framework"` (default `net10.0`, override with `EF_FRAMEWORK`) to every `dotnet build` and `dotnet ef` call. Without it, under SDK 11 `dotnet ef` fails with "The project targets multiple frameworks". |
| Docs | README prerequisites/badge, CONTRIBUTING (CI runs format under SDK 11), `test/CLAUDE.md`, `src/NLightning.Daemon/CLAUDE.md`, `src/NLightning.Transport.Ipc/CLAUDE.md`. |

### Verification (macOS arm64; SDK 10.0.103 in `~/.dotnet`, SDK 11.0.100-rc.1.26425.128 + runtimes 10.0.12 (NETCore and AspNetCore) in a user-local `~/.dotnet11`, built from the real path)
- SDK 11, Release and Release.Native (`--no-incremental`): 0 errors, 25 projects × 2 frameworks, only the 5 known CS86xx warnings (NL-171), each once per framework.
- SDK 11, `dotnet test --no-build --filter 'FullyQualifiedName!~Docker'`: **4879 × 2** (net10.0 and net11.0) pass in Release and in Release.Native, no skips.
- SDK 10, Release and Release.Native: 0 errors, net10.0 only, the same 5 warnings; the same tests pass on net10.0 (4879) in both configurations. One run of `ThreeNodeSwitchTests.Given_BobRestartsAfterDownstreamFulfill_When_Replayed_Then_UpstreamFulfilledExactlyOnce` failed its lock audit; it flakes the same way (1 of 10) on unmodified `wip/fafo`, so it is not a TFM issue.
- `dotnet format --verify-no-changes --exclude "**/BlazorTests/**"`: passes under SDK 10 and under SDK 11.
- `python3 scripts/check-sln-configs.py`: OK (no change needed).
- `dotnet ef` (10.0.5, scratch tool path) under SDK 11 with `--framework net10.0`: `has-pending-model-changes` reports no changes for Postgres, Sqlite and SqlServer; a probe `migrations add` + `remove` on Sqlite works and the Designer `ProductVersion` stays `10.0.5`. Gotcha seen: running `migrations remove` right after `add` **without rebuilding** the provider project removed the previous real migration instead of the probe (EF loads the stale provider assembly, NL-233); the scripts build first, so run them rather than bare `dotnet ef`.
- No project targets net9.0 (`grep -r net9.0 --include=*.csproj` is empty).

### Developer notes
- With SDK 11 installed, `dotnet run --project src/NLightning.Daemon` (and the xunit v3 `dotnet run --project test/...` runner) needs `-f net10.0` or `-f net11.0`. The net10.0 output stays at `bin/<Config>/net10.0/`, so `.vscode/launch.json` is unchanged.
- An SDK-11-only root (as in `~/.dotnet11`) needs the .NET 10 runtime (NETCore **and** AspNetCore; a test project loads AspNetCore 10) to run the net10.0 tests. Set `DOTNET_ROOT` to that root.
- Run the Docker tests one framework at a time (`-f net10.0`), because both would use the fixed container names. Not run on net11.0 yet.

### Still open
- Docker suite on net11.0, NativeAOT publish, Wasm on SDK 11.
- Package bumps owned by W4-A: EF Core 10.0.12, Npgsql 10.0.3, Npgsql.EFCore 10.0.3.
- After GA: `allowPrerelease: false` in `global.json`, drop `dotnet-quality: preview` in CI. Revisit EF 11 when EFCore.NamingConventions 11 ships.
- The SDK 11 rc.1 symlinked-path `CopyToOutputDirectory` regression (below) was not reproduced here (built from the real path only); still worth reporting upstream.
- Behaviour differs per TFM: `IHost.RunAsync` throws on a failed BackgroundService on net11.0 (exit 1) but not on net10.0 (exit 0).

---

## Status of .NET 11
- **RC1** (`11.0.0-rc.1`, 2026-09-08). Support phase: go-live. GA is expected at .NET Conf in November 2026.
- **STS** (24 months). **.NET 10 is LTS** (EOL 2028-11-14).
- **.NET 9 reaches EOL on 2026-11-10.** `NLightning.Daemon.Contracts` and `NLightning.Daemon.Plugins` targeted net9.0 (moved to the repo targets in W4-C).
- EF Core 11, Microsoft.Extensions 11 and Npgsql.EFCore 11 are all at rc.1.

## Verdict: drop-in for the runtime and code, not for tooling
- The branch multi-targeted to `net10.0;net11.0` builds in Release and Release.Native with **no code changes**. Warnings are the same 5 known CS86xx (NL-171); there are no new NU, SYSLIB or IL warnings.
- **All 3934 non-Docker tests pass on both net10.0 and net11.0**, in both Release and Release.Native (4879 at W4-C).
- Not drop-in:
  1. **`dotnet format --verify-no-changes` fails under SDK 11** with 4× `IDE0031`: the analyzer wants the null-conditional event assignment `x?.E += h` (valid C# 14, so it also works on net10). Sites (fixed in W4-C):
     - `src/NLightning.Bolt11/Models/Invoice.cs:194`
     - `src/NLightning.Bolt11/Models/Invoice.cs:525`
     - `src/NLightning.Application/Node/Managers/PeerManager.cs:149`
     - `test/NLightning.Integration.Tests/Docker/Abcd/ChannelMessageRecorder.cs:55`
  2. `global.json` (`10.0.0`, `latestMinor`, no prerelease) blocks SDK 11.
  3. CI must install both SDKs/runtimes.
  4. `dotnet ef` needs `--framework` once projects are multi-targeted.
  5. EF Core 11 can't be used yet (see the package table).

## Required changes (research; see "As built" for what was done)
| File | Change |
|---|---|
| `global.json` | Allow SDK 11 (`11.0.100`, `rollForward: latestFeature`, `allowPrerelease: true` until GA). Alternative: keep it as is and put an SDK-version condition on net11 in the props, so developers on SDK 10 can still build. *(As built: `10.0.100` + `latestMajor` + prerelease, and the SDK condition.)* |
| `src/Directory.Build.props` | `TargetFramework` → `<TargetFrameworks>net10.0;net11.0</TargetFrameworks>`; optionally `Condition="$([MSBuild]::VersionGreaterThanOrEquals('$(NETCoreSdkVersion)','11.0'))"`. Set `LangVersion` to `14` or remove it: with `latest`, SDK 11 compiles the net10 target as C# 15. *(As built: `NETCoreSdkVersion` is empty at that point, see "SDK gate".)* |
| `Daemon.Contracts` / `Daemon.Plugins` csproj | Use the plural `TargetFrameworks` element; otherwise the props value wins. net9.0 → net10.0 (EOL). Microsoft.Extensions 9.0.9 → 10.0.x. *(As built: the element is removed so the props apply.)* |
| 9 test csproj | Plural `TargetFrameworks`, or set it once in `test/Directory.Build.props` (empty today). |
| 4 IDE0031 sites | `a?.Changed += h;` |
| `.github/workflows/*` | `setup-dotnet` with `10.0.x` and `11.0.x` (plus `dotnet-quality: preview` until GA). Coverage/trx files are produced per TFM. |
| `src/NLightning.Infrastructure.Persistence/scripts/{add,remove}_migration.sh` | Add `--framework net10.0` to every `dotnet ef` call. |
| `.vscode/launch.json`, Wasm workflows | Hard-coded `net10.0` paths stay valid while net10 remains a target. |

`scripts/check-sln-configs.py` and `NLightning.sln` need no change (configs, not TFMs).

## Packages
| Package | Decision |
|---|---|
| EF Core 10.0.5 (Relational, Design, Sqlite, SqlServer) | EF 11 packages target **net11 only**. Stay on 10.x, which runs on net11; bump to 10.0.12 (W4-A). |
| Npgsql.EFCore.PostgreSQL 10.0.1 | 10.0.3 (W4-A). Its 11 rc pins EF 11. |
| **EFCore.NamingConventions 10.0.1** | **No 11 release** (depends on EF `[10.0.1, 11.0.0)`), so it blocks EF 11 for Postgres snake_case. |
| Npgsql 10.0.2 | 10.0.3 (W4-A) |
| dotnet-ef 10.0.5 | Stay on 10.x while EF is 10. |
| Microsoft.Extensions.* 10.0.5 | 10.0.12 (done in W4-C). Nine of these packages are in the net11 shared framework. No NU1510 while multi-targeting; drop `Options` and `Logging.Abstractions` if going net11-only. |
| SQLitePCLRaw 2.1.12 | Keep while on EF 10. |
| NBitcoin 9.0.5, NBitcoin.Secp256k1 3.2.0 | Work on net11; the major bumps (10.x / 4.x) are optional and separate. |
| libsodium, BouncyCastle, NetMQ, MessagePack, Serilog, xunit v3, Moq, coverlet, Docker.DotNet, LNUnit | Work unchanged on net11; bumps optional. |

## .NET 11 breaking changes that touch this codebase
- **`IHost.RunAsync` throws when a BackgroundService fails.** Affects `NltgDaemonService` and `src/NLightning.Daemon/Program.cs:142`. On net11 a crash is caught, logged as Fatal and exits 1; on net10 it exits 0. This is an improvement, but behaviour now differs per TFM.
- **Nine Microsoft.Extensions packages are in the shared framework** (Options and Logging.Abstractions in Application and Infrastructure).
- **SDK analyzers:** the IDE0031 format failures above.
- **NamedPipe `CurrentUserOnly` socket mode:** not applicable (`NamedPipeIpcService.cs:90` uses only Asynchronous, with cookie auth).

Checked and not applicable:
- Crypto obsoletions: SYSLIB0064/0065, DSA/HKDF platform changes.
- `Math.Round` digits overloads: `LightningMoney.cs:32` doesn't use them.
- `Nullable.GetUnderlyingType` on custom Type subclasses.
- Also not used: DateOnly/TimeOnly parsing, Zip/Tar/Cbor, TickCount, SslStream AIA, async options validation.
- NativeAOT `lib` prefix: we produce no native library.
- JIT float→small-int saturation: no failing tests.
- `LibraryImport`/`JSImport`: unchanged.
- EF 11 changes (SQL Server compat level 160, `MigrationsNotFound` throws, SqlClient 7): only relevant after moving to EF 11.

## Recommended strategy
Multi-target `net10.0;net11.0`, with net10 as the primary LTS target and EF on 10.x for both. Do **not** go net11-only: 11 is STS and still RC, EF 11 can't serve net10, and NamingConventions has no 11 release.

1. Contracts/Plugins → net10.0, Microsoft.Extensions 10.0.x. Check: `dotnet build -c Release -p:MSBuildWarningsAsMessages=MSB4121`. *(done)*
2. Fix the 4 IDE0031 sites and set `LangVersion` 14. Check: format passes under SDK 10 and SDK 11. *(done)*
3. `global.json` and `TargetFrameworks` in `src/` and `test/` props (optionally SDK-conditioned); plural element in Contracts/Plugins. *(done, SDK-conditioned)*
4. Patch bumps: EF 10.0.12, Npgsql 10.0.3, Extensions 10.0.12. Check: `dotnet list package --vulnerable`. *(Extensions done; EF/Npgsql in W4-A)*
5. EF scripts get `--framework net10.0`. Check: add then remove a probe migration; Designer `ProductVersion` must stay 10.0.x. *(done, Sqlite probe)*
6. Tests:
   - Non-Docker: 3934 × 2 TFMs, in both Release and Release.Native. *(4879 × 2, done)*
   - Docker: **one TFM at a time** (`-f net10.0`, then `-f net11.0`), because the fixed container names collide. *(open)*
7. CI: install both SDKs (preview quality until GA). *(done)*
8. After GA: `allowPrerelease: false`. Revisit EF 11 when EFCore.NamingConventions 11 ships.

## Risks
- Everyone needs SDK 11 (RC until November) unless net11 is SDK-conditioned. *(It is: SDK 10 builds net10.0 only.)*
- Build and test time roughly doubles.
- The Docker suite has to run once per TFM.
- Behaviour differs per TFM (host failure exit code, shared-framework Extensions).
- **SDK 11 rc.1 regression seen:** building through a symlinked path (`/tmp` → `/private/tmp`) silently skipped `CopyToOutputDirectory` items. Test vectors went missing and BOLT3 vectors were flattened into the bin root. Building via the real path was fine, and SDK 10 was fine either way. Worth watching and reporting upstream.

## Not yet verified
Docker suite on net11, NativeAOT publish, Wasm on SDK 11. (`dotnet ef` with multi-targeting: verified in W4-C.)
