# NLightning.Bolt11Benchmark

A performance harness, not product code: times the BOLT 11 invoice encode/decode of `NLightning.Bolt11`.

## What it measures

1. **Per-invoice latency** — for six sample invoices (minimal, typical, long description, with taproot fallback,
   1 route hint, 8 route hints), the min/p50/mean/p90/max in microseconds of `Invoice.Decode` and of generating
   (constructing + `ToString(Key)`) an invoice, single-threaded.
2. **Raw throughput** — ops/second over a 10 s sampling window (after a 1 s warmup) for decode and generate, at
   1 thread and at the machine's core count.

`Invoice.ToString(Key)` caches its encoded string, so "generate" always constructs a fresh invoice before
encoding — the cached return would say nothing about encoding cost.

## Run

```sh
dotnet run --project tools/NLightning.Bolt11Benchmark -c Release
```

Options: `--latency-iterations <n>` (default 2,000) and `--throughput-seconds <n>` (default 10). Everything is
deterministic (fixed keys, hashes and route hints), so two runs compare directly. A sink accumulates every result
and is printed at the end so the JIT cannot remove the benchmarked work.
