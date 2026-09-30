using System.Diagnostics;
using NBitcoin;

namespace NLightning.Bolt11Benchmark;

using Bolt11.Models;
using Bolt11.Models.TaggedFields;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Models;
using Domain.Protocol.Constants;
using Domain.Protocol.ValueObjects;

/// <summary>
/// Times the BOLT 11 invoice encode/decode of NLightning.Bolt11:
/// 1. per-invoice latency (min/p50/mean/p90/max in microseconds, single-threaded), and
/// 2. raw throughput in ops/second over a sampling window, at 1 core and at the machine's core count.
/// </summary>
/// <remarks>
/// Everything is deterministic (fixed keys, hashes and route hints), so two runs are comparable. "Generate" means
/// constructing the invoice AND encoding it, because <c>Invoice.ToString(Key)</c> caches its string — measuring the
/// cached return would say nothing about encoding cost. Results are accumulated into a sink and printed at the end
/// so the JIT cannot eliminate the benchmarked calls. No external benchmark dependency; Stopwatch is the clock.
/// </remarks>
public static class Program
{
    private const int LatencyIterations = 2_000;
    private const int LatencyWarmup = 500;

    private static long _sink;

    public static int Main(string[] args)
    {
        var latencyIterations = ReadOption(args, "--latency-iterations", LatencyIterations);
        var throughputSeconds = ReadOption(args, "--throughput-seconds", 10);
        var network = BitcoinNetwork.Mainnet;

        Console.WriteLine("NLightning.Bolt11 invoice benchmark");
        Console.WriteLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim()}");
        Console.WriteLine($"Processors: {Environment.ProcessorCount}, Server GC: " +
                          $"{System.Runtime.GCSettings.IsServerGC}, .NET: {Environment.Version}");
        Console.WriteLine($"\"generate\" = construct the invoice and encode it (ToString(Key) caches, so the " +
                          "benchmark never measures the cached string)");
        Console.WriteLine();

        // Arrange: a fixed set of invoices from minimal to heavy. Deterministic keys and hashes, so two runs compare.
        var key = new Key(Convert.FromHexString(
            "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20"));
        var paymentHash = new uint256(Convert.FromHexString(
            "0203040506070809000102030405060708090001020304050607080900010203"));
        var paymentSecret = new uint256(Convert.FromHexString(
            "1111111111111111111111111111111111111111111111111111111111111111"));
        var hopKey = new CompactPubKey(Convert.FromHexString(
            "02" + "3132333435363738393031323334353637383930313233343536373839303132"));
        var fallback = BitcoinAddress.Create(
            "bc1pptdvg0d2nj99568qn6ssdy4cygnwuxgw2ukmnwgwz7jpqjz2kszse2s3lm", Network.Main);
        RoutingInfo Hint(int i) =>
            new(hopKey, new ShortChannelId(870127, (uint)(1200 + i), 1), 1_000, 250, 40);

        var samples = new (string Name, Func<Invoice> Create)[]
        {
            ("minimal", () => Invoice.InSatoshis(1_000, "m", paymentHash, paymentSecret, network)),
            ("typical", () => Invoice.InSatoshis(50_000, new string('x', 40), paymentHash, paymentSecret, network)),
            ("long-description", () => Invoice.InSatoshis(120_000, new string('d', 600), paymentHash,
                                                          paymentSecret, network)),
            ("with-fallback", () => Invoice.InSatoshis(10_000, "fallback", paymentHash, paymentSecret, network)),
            ("1-route-hint", () => Invoice.InSatoshis(25_000, "one hint", paymentHash, paymentSecret, network)),
            ("8-route-hints", () => Invoice.InSatoshis(250_000, "eight hints", paymentHash, paymentSecret, network)),
        };
        samples[3].Create = Enhance(samples[3].Create, i => i.FallbackAddresses = [fallback]);
        samples[4].Create = Enhance(samples[4].Create, i => i.RoutingInfos = [Hint(1)]);
        samples[5].Create = Enhance(samples[5].Create, i =>
        {
            var hints = new RoutingInfoCollection();
            for (var h = 1; h <= 8; h++)
                hints.Add(Hint(h));
            i.RoutingInfos = hints;
        });

        var encoded = new string[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            encoded[i] = samples[i].Create().ToString(key);
            Console.WriteLine($"  {samples[i].Name,-16} {encoded[i].Length,4} chars");
        }

        Console.WriteLine();

        // 1. Per-invoice latency, single-threaded.
        Console.WriteLine($"Per-invoice latency over {latencyIterations} iterations each (single-threaded):");
        Console.WriteLine("  invoice            decode min/p50/mean/p90/max (us)        generate min/p50/mean/p90/max (us)");
        for (var i = 0; i < samples.Length; i++)
        {
            var slot = i;
            var decode = LatencyUs(() => _sink += Invoice.Decode(encoded[slot], network).Amount!.Satoshi,
                                   latencyIterations);
            var generate = LatencyUs(() => _sink += samples[slot].Create().ToString(key).Length,
                                     latencyIterations);
            Console.WriteLine($"  {samples[i].Name,-16} {Format(decode)}   {Format(generate)}");
        }

        Console.WriteLine();

        // 2. Raw throughput over a sampling window, 1 core vs all cores.
        var cores = Environment.ProcessorCount;
        Console.WriteLine($"Throughput, {throughputSeconds} s sampling window (after a 1 s warmup):");
        Console.WriteLine("  configuration          ops/s         total ops   threads");
        foreach (var (op, threads) in new (string, int)[]
                 {
                     ("decode", 1), ("decode", cores), ("generate", 1), ("generate", cores)
                 })
        {
            var (opsPerSecond, total) =
                Throughput(op, threads, encoded, samples, key, network, throughputSeconds);
            Console.WriteLine($"  {op + "@" + threads + (threads == 1 ? " thread " : " threads"),-18} " +
                              $"{opsPerSecond,12:N0}   {total,13:N0}   {threads}");
        }

        Console.WriteLine();
        Console.WriteLine($"sink: {Interlocked.Read(ref _sink)} (kept so the JIT cannot remove the work)");
        return 0;
    }

    private static Func<Invoice> Enhance(Func<Invoice> create, Action<Invoice> enhance) => () =>
    {
        var invoice = create();
        enhance(invoice);
        return invoice;
    };

    private static (double Min, double P50, double Mean, double P90, double Max) LatencyUs(
        Action operation, int iterations)
    {
        for (var i = 0; i < LatencyWarmup; i++)
            operation();

        var samples = GC.AllocateUninitializedArray<double>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            var start = Stopwatch.GetTimestamp();
            operation();
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        }

        Array.Sort(samples);
        double Percentile(double p) =>
            samples[Math.Min(samples.Length - 1, (int)Math.Ceiling(p * samples.Length) - 1)];
        return (samples[0], Percentile(0.50), samples.Average(), Percentile(0.90), samples[^1]);
    }

    private static string Format((double Min, double P50, double Mean, double P90, double Max) s) =>
        $"{s.Min,7:F1} {s.P50,7:F1} {s.Mean,7:F1} {s.P90,7:F1} {s.Max,8:F1}";

    private static (double OpsPerSecond, long Total) Throughput(string operation, int threads, string[] encoded,
                                                                (string Name, Func<Invoice> Create)[] samples,
                                                                Key key, BitcoinNetwork network, double runSeconds)
    {
        using var startSignal = new ManualResetEventSlim(false);
        var threadsOut = new Task<long>[threads];

        for (var t = 0; t < threads; t++)
        {
            threadsOut[t] = Task.Run(() =>
            {
                var state = Environment.CurrentManagedThreadId;
                startSignal.Wait();

                // Warmup (JIT, caches), then the measured window.
                RunWindow(operation, encoded, samples, key, network, TimeSpan.FromSeconds(1), ref state);
                var count = RunWindow(operation, encoded, samples, key, network,
                                      TimeSpan.FromSeconds(runSeconds), ref state);
                return count;
            });
        }

        startSignal.Set();
        Task.WaitAll(threadsOut);

        long total = 0;
        foreach (var task in threadsOut)
            total += task.Result;

        _sink += total;
        return (total / runSeconds, total);
    }

    private static long RunWindow(string operation, string[] encoded,
                                  (string Name, Func<Invoice> Create)[] samples, Key key, BitcoinNetwork network,
                                  TimeSpan duration, ref int state)
    {
        long count = 0;
        var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            for (var i = 0; i < encoded.Length; i++)
            {
                state = (state + i) % 31; // touch the data so no call can be folded away
                if (operation == "decode")
                {
                    _sink += Invoice.Decode(encoded[i], network).Amount!.Satoshi;
                }
                else
                {
                    _sink += samples[i].Create().ToString(key).Length & state;
                }

                count++;
            }
        }

        return count;
    }

    private static int ReadOption(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name && int.TryParse(args[i + 1], out var value) && value > 0)
                return value;
        }

        return fallback;
    }
}
