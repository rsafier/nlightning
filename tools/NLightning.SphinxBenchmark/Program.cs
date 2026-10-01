using System.Diagnostics;
using NBitcoin;

namespace NLightning.SphinxBenchmark;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Onion;

/// <summary>
/// Measures the BOLT 4 sphinx hot path of NLightning.Infrastructure.Bitcoin:
/// 1. per-op latency (min/p50/mean/p90/max in microseconds, single-threaded) and managed allocations
///    (<see cref="GC.GetAllocatedBytesForCurrentThread"/>) of building a 5-hop onion and of peeling one layer /
///    a whole 5-hop route, and
/// 2. raw peel throughput in ops/second over a sampling window, at 1 core and at the machine's core count.
/// </summary>
/// <remarks>
/// Everything is deterministic (fixed node, session and payload bytes), so two runs compare directly. Results are
/// accumulated into a sink and printed at the end so the JIT cannot eliminate the benchmarked calls. No external
/// benchmark dependency; <see cref="Stopwatch"/> is the clock.
/// </remarks>
public static class Program
{
    private const int Hops = 5;
    private const int PayloadLength = 200;
    private const int LatencyWarmup = 500;
    private const int AllocationIterations = 1_000;

    private static readonly byte[] s_associatedData = Enumerable.Repeat((byte)0x42, 32).ToArray();

    private static long s_sink;

    public static int Main(string[] args)
    {
        var latencyIterations = ReadOption(args, "--latency-iterations", 2_000);
        var throughputSeconds = ReadOption(args, "--throughput-seconds", 10);

        Console.WriteLine("NLightning sphinx onion benchmark");
        Console.WriteLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim()}");
        Console.WriteLine($"Processors: {Environment.ProcessorCount}, Server GC: " +
                          $"{System.Runtime.GCSettings.IsServerGC}, .NET: {Environment.Version}");
        Console.WriteLine($"Route: {Hops} hops x {PayloadLength}-byte payloads, " +
                          "hop payloads of OnionConstants.HopPayloadsLength");
        Console.WriteLine();

        // Arrange: a fixed route. The same packet is peeled repeatedly (peeling never modifies its input), so every
        // iteration does exactly the work one node does for one HTLC.
        var service = new SphinxService(new Secp256K1Math());
        var nodeKeys = new PrivKey[Hops];
        var hops = new List<OnionHop>(Hops);
        for (var i = 0; i < Hops; i++)
        {
            var key = new PrivKey(GetDeterministicKey(i));
            nodeKeys[i] = key;
            hops.Add(new OnionHop(new CompactPubKey(new Key(key.Value.ToArray()).PubKey.ToBytes(true)),
                                  Enumerable.Repeat((byte)(i + 1), PayloadLength).ToArray()));
        }

        var sessionKey = new PrivKey(GetDeterministicKey(99));
        var packet = service.Construct(hops, sessionKey, s_associatedData);

        // Pre-peel once so every hop position has its packet (the peel chain is deterministic).
        var packets = new OnionPacket[Hops];
        packets[0] = packet;
        for (var i = 1; i < Hops; i++)
            packets[i] = service.Peel(packets[i - 1], s_associatedData, nodeKeys[i - 1]).NextPacket!.Value;
        Console.WriteLine($"Sanity: the full peel recovers every payload: " +
                          $"{PeelRecoversAllPayloads(service, packets, nodeKeys)}");
        Console.WriteLine();

        var scenarios = new (string Name, Action Op)[]
        {
            ("build-5", () => s_sink += service.Construct(hops, sessionKey, s_associatedData).Length),
            ("build-5+secrets",
             () => s_sink += service.ConstructWithSharedSecrets(hops, sessionKey, s_associatedData).Packet.Length),
            ("peel-1-of-5", () => s_sink += service.Peel(packets[0], s_associatedData, nodeKeys[0]).Payload.Length),
            ("peel-5", () => PeelAll(service, packets, nodeKeys))
        };

        // 1. Per-op latency, single-threaded.
        Console.WriteLine($"Per-op latency over {latencyIterations} iterations each (single-threaded):");
        Console.WriteLine("  scenario       min/p50/mean/p90/max (us)");
        foreach (var (name, op) in scenarios)
            Console.WriteLine($"  {name,-14} {Format(LatencyUs(op, latencyIterations))}");

        Console.WriteLine();

        // 2. Managed allocations per op, single-threaded (GC.GetAllocatedBytesForCurrentThread).
        Console.WriteLine($"Managed allocations over {AllocationIterations} ops each (single-threaded):");
        Console.WriteLine("  scenario       bytes/op");
        foreach (var (name, op) in scenarios)
            Console.WriteLine($"  {name,-14} {AllocatedBytesPerOp(op),12:N0}");

        Console.WriteLine();

        // 3. Raw peel throughput over a sampling window, 1 core vs all cores (pooling must not serialize callers).
        var cores = Environment.ProcessorCount;
        Console.WriteLine($"Peel throughput, {throughputSeconds} s sampling window (after a 1 s warmup):");
        Console.WriteLine("  configuration       ops/s         total ops   threads");
        foreach (var threads in new[] { 1, cores })
        {
            var (opsPerSecond, total) =
                Throughput(service, packets, nodeKeys, threads, throughputSeconds);
            Console.WriteLine($"  peel-1-of-5@{threads + (threads == 1 ? " thread" : " threads"),-10} " +
                              $"{opsPerSecond,12:N0}   {total,13:N0}   {threads}");
        }

        Console.WriteLine();
        Console.WriteLine($"sink: {Interlocked.Read(ref s_sink)} (kept so the JIT cannot remove the work)");
        return 0;
    }

    private static void PeelAll(SphinxService service, OnionPacket[] packets, PrivKey[] nodeKeys)
    {
        for (var i = 0; i < Hops; i++)
            s_sink += service.Peel(packets[i], s_associatedData, nodeKeys[i]).Payload.Length;
    }

    private static bool PeelRecoversAllPayloads(SphinxService service, OnionPacket[] packets, PrivKey[] nodeKeys)
    {
        var current = packets[0];
        for (var i = 0; i < Hops; i++)
        {
            var peeled = service.Peel(current, s_associatedData, nodeKeys[i]);
            if (peeled.Payload.Length != PayloadLength || peeled.Payload.Span.IndexOfAnyExcept((byte)(i + 1)) >= 0)
                return false;

            if (i == Hops - 1)
                return peeled.IsFinal;

            if (peeled.NextPacket is not { } next)
                return false;

            current = next;
        }

        return true;
    }

    private static byte[] GetDeterministicKey(int seed)
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++)
            key[i] = (byte)(seed * 7 + i + 1);

        return key;
    }

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

    private static long AllocatedBytesPerOp(Action operation)
    {
        for (var i = 0; i < LatencyWarmup; i++)
            operation();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < AllocationIterations; i++)
            operation();

        return (GC.GetAllocatedBytesForCurrentThread() - before) / AllocationIterations;
    }

    private static (double OpsPerSecond, long Total) Throughput(SphinxService service, OnionPacket[] packets,
                                                                PrivKey[] nodeKeys, int threads,
                                                                double runSeconds)
    {
        using var startSignal = new ManualResetEventSlim(false);
        var threadsOut = new Task<long>[threads];

        for (var t = 0; t < threads; t++)
        {
            threadsOut[t] = Task.Run(() =>
            {
                startSignal.Wait();

                // Warmup (JIT, pool), then the measured window.
                RunWindow(service, packets, nodeKeys, TimeSpan.FromSeconds(1));
                return RunWindow(service, packets, nodeKeys, TimeSpan.FromSeconds(runSeconds));
            });
        }

        startSignal.Set();
        Task.WaitAll(threadsOut);

        long total = 0;
        foreach (var task in threadsOut)
            total += task.Result;

        s_sink += total;
        return (total / runSeconds, total);
    }

    private static long RunWindow(SphinxService service, OnionPacket[] packets, PrivKey[] nodeKeys,
                                  TimeSpan duration)
    {
        long count = 0;
        var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            s_sink += service.Peel(packets[0], s_associatedData, nodeKeys[0]).Payload.Length;
            count++;
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