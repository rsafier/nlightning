# NLightning.SphinxBenchmark

A performance harness, not product code: times the BOLT 4 sphinx hot path (onion build and peel) of
`NLightning.Infrastructure.Bitcoin` (NL-083).

## What it measures

For a fixed 5-hop route with 200-byte payloads and the default 1300-byte hop payloads:

1. **Per-op latency** — min/p50/mean/p90/max in microseconds (single-threaded) of `Construct` (build-5),
   `ConstructWithSharedSecrets` (build-5+secrets), peeling the first layer of the 5-hop packet (peel-1-of-5, the
   per-HTLC node work) and peeling all 5 layers (peel-5).
2. **Managed allocations** — bytes/op via `GC.GetAllocatedBytesForCurrentThread` over 1,000 ops, for the same
   scenarios.
3. **Raw peel throughput** — ops/second over a 10 s sampling window (after a 1 s warmup) for peel-1-of-5, at
   1 thread and at the machine's core count (pooling must not serialize concurrent callers).

Peeling never modifies its input packet, so the same pre-peeled packets are reused for every iteration.

## Run

```sh
dotnet run --project tools/NLightning.SphinxBenchmark -c Release
```

Options: `--latency-iterations <n>` (default 2,000) and `--throughput-seconds <n>` (default 10). Everything is
deterministic (fixed node, session and payload bytes), so two runs compare directly. A sink accumulates every
result and is printed at the end so the JIT cannot remove the benchmarked work.