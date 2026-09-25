# Sphinx reference harness (throwaway)

A spec-direct BOLT 4 Sphinx construction written with NBitcoin and BouncyCastle `ChaCha7539Engine`. It reproduces `bolt04/onion-test.json` byte-for-byte, and also probes LNBolt (see `../../LNBOLT_REVIEW.md`).

It is **not** part of the solution. The project file is stored as `harness.csproj.txt` so that tooling doesn't pick it up. To run it:
clone `nbd-wtf/LNUnit` next to it as `../LNUnit`, rename the file to `harness.csproj`, put `onion-test.json` in the parent directory, then run `dotnet run`.

Use it only as a cross-check oracle while implementing the M1/M2 milestones in `../../ONION_ROUTING_PLAN.md`. Do not port code from it.
