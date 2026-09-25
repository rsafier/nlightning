# NLightning Integration Tests

Spec-vector and end-to-end tests that exercise several layers together.

| Folder | What it covers |
|---|---|
| `BOLT3/` | BOLT 3 Appendix B (funding tx), C (commitment txs, byte-for-byte), D (per-commitment secrets / shachain), E (key derivation) and F (anchor commitment txs). Vectors live in `test/NLightning.Tests.Utils/Vectors/Bolt3Appendix*`. The HTLC second-stage vector test is skipped until the HTLC tx builder exists (NL-056). |
| `BOLT4/` | Onion vectors from `lightning/bolts` (`Vectors/*.json`, source in `Vectors/README.md`): Sphinx construct/peel, blinded payment and blinded onion-message chains, hop payloads, DI registration. |
| `BOLT8/` | [BOLT 8 Appendix A](https://github.com/lightning/bolts/blob/master/08-transport.md#appendix-a-transport-test-vectors) transport vectors: initiator, responder, message encryption and an end-to-end handshake. |
| `BOLT11/` | BOLT 11 spec invoice vectors (valid ones in `Vectors/ValidInvoices.txt`, invalid ones inline) and tagged-field integration tests. |
| `Persistence/` | Repository and unit-of-work tests on SQLite `:memory:` with the real migrations, plus model/migration consistency checks for all three providers. Runs in CI. |
| `Docker/` | Local-only tests that need Docker: bitcoind + three LND nodes (`AbcNetworkTests`, `ChannelOpeningFlowTests`) and Postgres/SQL Server containers (`PostgresTests`, `SqlServerTests`). CI excludes them with `--filter 'FullyQualifiedName!~Docker'`. |

Run everything except Docker:

```bash
dotnet test test/NLightning.Integration.Tests -c Release --filter 'FullyQualifiedName!~Docker'
```

Run the Docker tests (they force-remove containers named miner/alice/bob/carol/postgres/sqlserver):

```bash
dotnet test test/NLightning.Integration.Tests -c Release --filter 'FullyQualifiedName~Docker'
```
