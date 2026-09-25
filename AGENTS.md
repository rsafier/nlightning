# AGENTS.md

Canonical agent instructions live in `CLAUDE.md` (repo root) and `docs/agents/`. Read those first; this file is a pointer for non-Claude agents (Codex etc.).

NLightning: C# .NET 10 Lightning Network node (SDK pinned in `global.json`). Commands below match `.github/workflows/dotnet.yml`:

- Build: `dotnet build --configuration Release -p:MSBuildWarningsAsMessages=MSB4121`
- Format check (CI gate, must pass): `dotnet format --verify-no-changes --exclude "**/BlazorTests/**"`
- Unit tests: `dotnet test --no-build --configuration Release --filter 'FullyQualifiedName!~Docker'`
- Single test: `dotnet test test/<Project>/<Project>.csproj --no-build -c Release --filter "FullyQualifiedName~<Name>"`
- Application/Daemon tests (not discovered by `dotnet test`): `dotnet run --project test/NLightning.Application.Tests` (same for `test/NLightning.Daemon.Tests`)

Known: `PeerAddressTests.Given_HttpAddress_*` needs live DNS; `Release.Wasm` builds only on linux-x64; tests in the `Docker` namespace need a Docker daemon.
