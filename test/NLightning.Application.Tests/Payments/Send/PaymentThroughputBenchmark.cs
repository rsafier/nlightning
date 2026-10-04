namespace NLightning.Application.Tests.Payments.Send;

using System.Collections.Concurrent;
using System.Diagnostics;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Harness;

/// <summary>
/// An e2e payment-throughput benchmark over the production payment stack (real onions, real signatures, real
/// <see cref="Application.Payments.PaymentService"/> and invoice service, in-memory message FIFOs): Bob pays Carol's
/// invoice directly (one channel) and David's over the Bob–Carol–David route (one hop), serially (C=1) and with 8
/// concurrent payers (C=8), each measured over a 15 s sampling window: average time-to-resolution (invoice accepted
/// to fulfillment, wall clock) and completed payments per second.
/// </summary>
/// <remarks>
/// Explicit (<c>Category=Benchmark</c>): run it with
/// <c>dotnet test test/NLightning.Application.Tests -c Release -f net10.0 --filter "FullyQualifiedName~PaymentThroughputBenchmark" -- xUnit.Explicit=only</c>.
/// The nodes run on the real clock (<see cref="PaymentHarnessTopology.UseRealClock"/>) so latencies are wall clock;
/// the harness pumps from a single loop, so the message layer stays serialized. Channel capacity is raised so a
/// window cannot exhaust a balance (each payment moves 10 sat plus fees).
/// </remarks>
public class PaymentThroughputBenchmark(ITestOutputHelper output)
{
    private const int WindowSeconds = 15;
    private const int WarmupPayments = 10;
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(10_000);
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    [Fact(Explicit = true)]
    [Trait("Category", "Benchmark")]
    public async Task DirectAndOneHopPaymentThroughput()
    {
        List<(string Scenario, WindowResult Window)> results = [];
        results.Add(("direct C=1 (Alice-Bob)", await RunWindowAsync(payeeIsDirect: true, concurrency: 1)));
        results.Add(("direct C=8 (Alice-Bob)", await RunWindowAsync(payeeIsDirect: true, concurrency: 8)));
        results.Add(("routed C=1 (Alice-Bob-Carol)", await RunWindowAsync(payeeIsDirect: false, concurrency: 1)));
        results.Add(("routed C=8 (Alice-Bob-Carol)", await RunWindowAsync(payeeIsDirect: false, concurrency: 8)));

        output.WriteLine("");
        output.WriteLine($"{"scenario",-28} {"payments",-10} {"p/s",-10} {"avg ms",-10} {"p50 ms",-10} {"p95 ms",-10} {"failed",-8}");
        foreach (var (scenario, window) in results)
            output.WriteLine(
                $"{scenario,-28} {window.Completed,-10} {window.PaymentsPerSecond,-10:F1} {window.AverageMs,-10:F2} {window.MedianMs,-10:F2} {window.P95Ms,-10:F2} {window.Failed,-8}");
    }

    private async Task<WindowResult> RunWindowAsync(bool payeeIsDirect, int concurrency)
    {
        // Bob plays Alice, Carol plays Bob, David plays Carol; capacity is raised so no window can drain a balance
        using var harness = new PaymentHarness(new PaymentHarnessTopology(BobUsesGraph: true, UseRealClock: true,
                             BobCarolFundingSatoshis: 100_000_000));
        harness.Bob.GraphView = harness.BuildGraph((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var payer = harness.Bob;
        var payee = payeeIsDirect ? harness.Carol : harness.David;

        await WarmupAsync(harness, payer, payee, concurrency);

        var latencies = new ConcurrentBag<double>();
        var completed = 0;
        var failed = 0;
        var firstError = (Exception?)null;
        using var windowCts = new CancellationTokenSource();
        var windowToken = windowCts.Token;

        // One pump loop drives every node's message exchange; the workers only start and await payments
        var pumpStop = new TaskCompletionSource();
        var pumpTask = PumpLoopAsync(harness, pumpStop.Task);

        var start = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            while (!windowToken.IsCancellationRequested)
            {
                try
                {
                    var invoice = await CreateInvoiceAsync(payee, windowToken);
                    var stopwatch = Stopwatch.StartNew();
                    var result = await payer.PaymentService.PayInvoiceAsync(invoice.Bolt11!, null,
                        new PayInvoiceOptions { Timeout = s_timeout, MaxParts = 1 }, windowToken);
                    stopwatch.Stop();
                    if (result.Payment.Status == PaymentStatus.Succeeded)
                    {
                        latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
                        Interlocked.Increment(ref completed);
                    }
                    else if (result.Payment.Status != PaymentStatus.InFlight)
                    {
                        // InFlight is the window's cut: the payments still on the wire when sampling ends
                        Interlocked.Increment(ref failed);
                        firstError ??= new Exception($"payment ended {result.Payment.Status}: {result.Payment.FailureReason}");
                    }
                }
                catch (OperationCanceledException) when (windowToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    firstError ??= ex;
                }
            }
        })).ToArray();

        var window = Task.Delay(TimeSpan.FromSeconds(WindowSeconds), CancellationToken.None);
        await Task.WhenAny(window, Task.WhenAll(workers));
        var elapsed = start.Elapsed;

        windowCts.Cancel();
        try { await Task.WhenAll(workers); } catch { /* failures already counted */ }
        pumpStop.SetResult();
        await pumpTask;

        if (firstError is not null)
            output.WriteLine($"[{(payeeIsDirect ? "direct" : "routed")} C={concurrency}] first failure: {firstError.Message}");

        return new WindowResult(completed, failed, elapsed, [.. latencies]);
    }

    private static async Task WarmupAsync(PaymentHarness harness, PaymentHarnessNode payer, PaymentHarnessNode payee,
                                          int concurrency)
    {
        var pumpStop = new TaskCompletionSource();
        var pumpTask = PumpLoopAsync(harness, pumpStop.Task);
        for (var i = 0; i < Math.Max(WarmupPayments, concurrency); i++)
        {
            var invoice = await CreateInvoiceAsync(payee, CancellationToken.None);
            await payer.PaymentService.PayInvoiceAsync(invoice.Bolt11!, null,
                new PayInvoiceOptions { Timeout = s_timeout, MaxParts = 1 }, CancellationToken.None);
        }

        pumpStop.SetResult();
        await pumpTask;
    }

    /// <summary>Invoice creation is serialized: the invoice service is one instance per node and signs every invoice.</summary>
    private static readonly SemaphoreSlim s_invoiceGate = new(1, 1);

    private static async Task<InvoiceModel> CreateInvoiceAsync(PaymentHarnessNode payee, CancellationToken ct)
    {
        await s_invoiceGate.WaitAsync(ct);
        try
        {
            return await payee.InvoiceService.CreateInvoiceAsync(s_amount, "bench", null, ct);
        }
        finally
        {
            s_invoiceGate.Release();
        }
    }

    private static async Task PumpLoopAsync(PaymentHarness harness, Task stop)
    {
        var stopCompleted = stop;
        while (!stopCompleted.IsCompleted)
        {
            await harness.PumpAsync();
            await Task.WhenAny(Task.Delay(1), stopCompleted);
        }

        await harness.PumpAsync();
    }

    private sealed record WindowResult(int Completed, int Failed, TimeSpan Elapsed, IReadOnlyList<double> Latencies)
    {
        public double PaymentsPerSecond => Completed / Math.Max(Elapsed.TotalSeconds, WindowSeconds);
        public double AverageMs => Latencies.Count == 0 ? 0 : Latencies.Average();
        public double MedianMs => Percentile(0.5);
        public double P95Ms => Percentile(0.95);

        private double Percentile(double p)
        {
            if (Latencies.Count == 0)
                return 0;
            var sorted = Latencies.OrderBy(x => x).ToArray();
            return sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
        }
    }
}