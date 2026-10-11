namespace NLightning.Integration.Tests.Benchmark;

using System.Collections.Concurrent;
using System.Diagnostics;
using Docker.Abcd;
using Docker.Utils;
using Domain.Bitcoin.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Payments.Enums;
using Fixtures.Tor;

/// <summary>
/// The payment throughput benchmark against real daemon loops: three <see cref="NLightningTestNode"/>s (each the
/// daemon's full service graph — real TCP transport, BOLT 8, serialization, SQLite persistence, real timers — on a
/// private regtest bitcoind in Docker), channels Alice–Bob and Bob–Carol, and BOLT 11 payments through the daemon's
/// client handlers (<c>createinvoice</c>/<c>payinvoice</c>). Alice pays Bob's invoice directly (one channel) and
/// Carol's over the one-hop route, serially (C=1) and with 8 concurrent payers (C=8), first with one channel per
/// pair and then with four, each measured over a 10 s sampling window: average time-to-resolution (wall clock, pay
/// request to settled response) and completed payments per second.
/// </summary>
/// <remarks>
/// Explicit (<c>Category=Benchmark</c>): run it with
/// <c>dotnet test test/NLightning.Integration.Tests -c Release -f net10.0 --filter "FullyQualifiedName~PaymentThroughputE2ETests" -- xUnit.Explicit=only</c>
/// (needs a running Docker; the chain host is the Tor fixture's standalone bitcoind recipe). Compare against the
/// in-process <c>PaymentThroughputBenchmark</c> in Application.Tests, which measures the same matrix without
/// transport, persistence or daemon loops.
/// </remarks>
public sealed class PaymentThroughputE2ETests(ITestOutputHelper output) : IAsyncLifetime
{
    private const int WindowSeconds = 10;
    private const int WarmupPayments = 5;
    private const uint FeeRatePerKw = 2_500;
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(10_000);
    private static readonly LightningMoney s_channelCapacity = LightningMoney.Satoshis(10_000_000);
    private static readonly TimeSpan s_usableTimeout = TimeSpan.FromMinutes(2);

    private readonly TorChainHost _chain = new(TorChainHost.CreateClient(), $"nltg-bench-{Guid.NewGuid():N}",
                                               $"nltg-bench-{Guid.NewGuid():N}");
    private NLightningTestNode _alice = null!;
    private NLightningTestNode _bob = null!;
    private NLightningTestNode _carol = null!;

    /// <summary>The invoice-creation gate: one invoice service per node, signing every invoice.</summary>
    private readonly SemaphoreSlim _invoiceGate = new(1, 1);

    [Fact(Explicit = true)]
    [Trait("Category", "Benchmark")]
    public async Task DirectAndOneHopThroughputOnRealNodes()
    {
        List<(string Scenario, WindowResult Window)> results = [];

        // One channel per pair
        await OpenChannelsAsync(1);
        results.Add(("direct C=1 x1ch", await RunWindowAsync(direct: true, concurrency: 1)));
        results.Add(("direct C=8 x1ch", await RunWindowAsync(direct: true, concurrency: 8)));
        results.Add(("routed C=1 x1ch", await RunWindowAsync(direct: false, concurrency: 1)));
        results.Add(("routed C=8 x1ch", await RunWindowAsync(direct: false, concurrency: 8)));

        // Four channels per pair; the initiators' first deposits were spent by the phase-1 fundings, so top every
        // wallet up with a fresh confirmed utxo before the second wave of opens (the accepter's anchors-reserve
        // check counts confirmed, unreserved utxos only)
        await _alice.FundWalletAsync(LightningMoney.Satoshis(200_000_000), AddressType.P2Wpkh, CancellationToken.None);
        await _bob.FundWalletAsync(LightningMoney.Satoshis(200_000_000), AddressType.P2Wpkh, CancellationToken.None);
        await _carol.FundWalletAsync(LightningMoney.Satoshis(200_000_000), AddressType.P2Wpkh, CancellationToken.None);
        await OpenChannelsAsync(3);
        results.Add(("direct C=1 x4ch", await RunWindowAsync(direct: true, concurrency: 1)));
        results.Add(("direct C=8 x4ch", await RunWindowAsync(direct: true, concurrency: 8)));
        results.Add(("routed C=1 x4ch", await RunWindowAsync(direct: false, concurrency: 1)));
        results.Add(("routed C=8 x4ch", await RunWindowAsync(direct: false, concurrency: 8)));

        output.WriteLine("");
        output.WriteLine($"{"scenario",-20} {"payments",-10} {"p/s",-10} {"avg ms",-10} {"p50 ms",-10} {"p95 ms",-10} {"failed",-8}");
        foreach (var (scenario, window) in results)
            output.WriteLine(
                $"{scenario,-20} {window.Completed,-10} {window.PaymentsPerSecond,-10:F1} {window.AverageMs,-10:F2} {window.MedianMs,-10:F2} {window.P95Ms,-10:F2} {window.Failed,-8}");
    }

    public async ValueTask InitializeAsync()
    {
        await _chain.StartAsync();
        // C=8 keeps up to 8 HTLCs in flight on one channel; the default max accepted HTLCs (5) would refuse the rest
        _alice = await NLightningTestNode.CreateAsync(_chain.Bitcoin, "bench-alice",
                                                      configureNodeOptions: o => o.MaxAcceptedHtlcs = 48);
        _bob = await NLightningTestNode.CreateAsync(_chain.Bitcoin, "bench-bob",
                                                    configureNodeOptions: o => o.MaxAcceptedHtlcs = 48);
        _carol = await NLightningTestNode.CreateAsync(_chain.Bitcoin, "bench-carol",
                                                      configureNodeOptions: o => o.MaxAcceptedHtlcs = 48);
        await _alice.StartAsync(CancellationToken.None);
        await _bob.StartAsync(CancellationToken.None);
        await _carol.StartAsync(CancellationToken.None);
        await _alice.ConnectToAsync(_bob, CancellationToken.None);
        await _bob.ConnectToAsync(_carol, CancellationToken.None);

        // Payer and forwarder balances; carol and bob only receive on their channels
        await _alice.FundWalletAsync(LightningMoney.Satoshis(200_000_000), AddressType.P2Wpkh, CancellationToken.None);
        await _bob.FundWalletAsync(LightningMoney.Satoshis(200_000_000), AddressType.P2Wpkh, CancellationToken.None);
        // The accepter of an anchors channel keeps the anchors reserve on chain, so carol needs funds to accept
        await _carol.FundWalletAsync(LightningMoney.Satoshis(200_000_000), AddressType.P2Wpkh, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _alice.DisposeAsync();
        await _bob.DisposeAsync();
        await _carol.DisposeAsync();
        await _chain.RemoveAsync();
        _invoiceGate.Dispose();
    }

    private async Task OpenChannelsAsync(int count)
    {
        try
        {
            await OpenChannelsCoreAsync(count);
        }
        catch (Exception ex)
        {
            output.WriteLine($"channel open failed: {ex.GetBaseException().Message}");
            foreach (var node in new[] { _alice, _bob, _carol })
                output.WriteLine($"--- {node.Name} log tail ---\n" + string.Join("\n", node.NodeLog.TakeLast(60)));
            throw;
        }
    }

    private async Task OpenChannelsCoreAsync(int count)
    {
        var opened = new List<(NLightningTestNode Owner, ChannelId ChannelId)>();
        for (var i = 0; i < count; i++)
        {
            var ab = await _alice.OpenChannelAsync(new OpenChannelClientRequest(_bob.Address, s_channelCapacity)
            {
                PushAmount = LightningMoney.Satoshis(100_000),
                FeeRatePerKw = LightningMoney.Satoshis(FeeRatePerKw)
            }, CancellationToken.None);
            opened.Add((_alice, ab.ChannelId));
            var bc = await _bob.OpenChannelAsync(new OpenChannelClientRequest(_carol.Address, s_channelCapacity)
            {
                PushAmount = LightningMoney.Satoshis(100_000),
                FeeRatePerKw = LightningMoney.Satoshis(FeeRatePerKw)
            }, CancellationToken.None);
            opened.Add((_bob, bc.ChannelId));
        }

        var deadline = DateTime.UtcNow + s_usableTimeout;
        while (true)
        {
            var usable = await Task.WhenAll(opened.Select(async pair =>
            {
                var (owner, channelId) = pair;
                var channel = await owner.GetChannelAsync(channelId, CancellationToken.None);
                return channel.IsUsable();
            }));
            if (usable.All(u => u))
                return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"the new channels did not become usable: {string.Join("; ", opened.Select(p => p.ChannelId))}");
            await Task.Delay(500);
        }
    }

    private async Task<WindowResult> RunWindowAsync(bool direct, int concurrency)
    {
        var payer = _alice;
        var payee = direct ? _bob : _carol;

        for (var i = 0; i < WarmupPayments; i++)
            await PayOnceAsync(payer, payee, CancellationToken.None);

        var latencies = new ConcurrentBag<double>();
        var completed = 0;
        var failed = 0;
        var firstError = (string?)null;
        using var windowCts = new CancellationTokenSource();
        var windowToken = windowCts.Token;

        var start = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            while (!windowToken.IsCancellationRequested)
            {
                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    var (status, reason) = await PayOnceAsync(payer, payee, windowToken);
                    stopwatch.Stop();
                    if (status == PaymentStatus.Succeeded)
                    {
                        latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
                        Interlocked.Increment(ref completed);
                    }
                    else if (status != PaymentStatus.InFlight)
                    {
                        // InFlight is the window's cut: the payments still on the wire when sampling ends
                        Interlocked.Increment(ref failed);
                        firstError ??= $"a payment ended {status}: {reason}";
                    }
                }
                catch (OperationCanceledException) when (windowToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    firstError ??= ex.Message;
                }
            }
        })).ToArray();

        var window = Task.Delay(TimeSpan.FromSeconds(WindowSeconds), CancellationToken.None);
        await Task.WhenAny(window, Task.WhenAll(workers));
        var elapsed = start.Elapsed;

        windowCts.Cancel();
        try { await Task.WhenAll(workers); } catch { /* failures already counted */ }

        if (firstError is not null)
            output.WriteLine($"[{(direct ? "direct" : "routed")} C={concurrency}] first failure: {firstError}");

        return new WindowResult(completed, failed, elapsed, [.. latencies]);
    }

    /// <summary>One invoice on <paramref name="payee"/>, paid by <paramref name="payer"/>; the settlement status.</summary>
    private async Task<(PaymentStatus Status, string? Reason)> PayOnceAsync(NLightningTestNode payer,
                                                                        NLightningTestNode payee,
                                                                        CancellationToken cancellationToken)
    {
        await _invoiceGate.WaitAsync(cancellationToken);
        InvoiceInfoClientResponse invoice;
        try
        {
            invoice = await payee.CreateInvoiceAsync(s_amount, "bench", cancellationToken);
        }
        finally
        {
            _invoiceGate.Release();
        }

        var payment = await payer.PayInvoiceAsync(invoice.Bolt11!, cancellationToken, 30);
        return (payment.Status, payment.FailureReason);
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