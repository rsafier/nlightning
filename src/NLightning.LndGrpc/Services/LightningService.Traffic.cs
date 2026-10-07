namespace NLightning.LndGrpc.Services;

using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Payments.Enums;
using Domain.Payments.Models;

public sealed partial class LightningService
{
    /// <summary>How long one computation of the per-channel totals is reused (RTL lists the channels on every page).</summary>
    private static readonly TimeSpan s_trafficCacheTime = TimeSpan.FromSeconds(5);

    private const int TrafficPageSize = 1_000;

    private readonly SemaphoreSlim _trafficGate = new(1, 1);
    private (DateTimeOffset At, IReadOnlyDictionary<ChannelId, (ulong SentMsat, ulong ReceivedMsat)> Totals)?
        _trafficCache;

    /// <summary>
    /// LND's <c>total_satoshis_sent</c>/<c>total_satoshis_received</c> per channel (NL-1249), from the stored history:
    /// our succeeded payments (what left through their first channel, fee included; a multi-part payment counts on
    /// the channel its row names), the fulfilled forwards (in on the incoming channel, out on the outgoing one) and
    /// the settled HTLCs of our invoices (in on their channel). The node keeps no per-channel counters, so this reads
    /// the tables (cached for a few seconds).
    /// </summary>
    private async Task<IReadOnlyDictionary<ChannelId, (ulong SentMsat, ulong ReceivedMsat)>> ChannelTrafficAsync(
        IReadOnlyCollection<ChannelModel> channels, CancellationToken cancellationToken)
    {
        await _trafficGate.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (_trafficCache is { } cached && now - cached.At < s_trafficCacheTime)
                return cached.Totals;

            var byScid = new Dictionary<ShortChannelId, ChannelId>();
            foreach (var channel in channels)
            {
                if (channel.ShortChannelId.BlockHeight != 0)
                    byScid[channel.ShortChannelId] = channel.ChannelId;
                foreach (var alias in channel.LocalAliases ?? [])
                    byScid.TryAdd(alias, channel.ChannelId);
                if (channel.RemoteAlias is { } remoteAlias)
                    byScid.TryAdd(remoteAlias, channel.ChannelId);
            }

            var totals = new Dictionary<ChannelId, (ulong SentMsat, ulong ReceivedMsat)>();
            void Add(ChannelId? channelId, ulong sentMsat, ulong receivedMsat)
            {
                if (channelId is not { } id)
                    return;

                var (sent, received) = totals.GetValueOrDefault(id);
                totals[id] = (sent + sentMsat, received + receivedMsat);
            }

            await using var scope = CreateScope();
            var unitOfWork = UnitOfWork(scope);
            for (var skip = 0; ; skip += TrafficPageSize)
            {
                var payments = await unitOfWork.PaymentDbRepository.ListAsync(skip, TrafficPageSize);
                foreach (var payment in payments.Where(p => p.Status == PaymentStatus.Succeeded))
                    Add(payment.OutgoingChannelId, (payment.Amount + payment.Fee).MilliSatoshi, 0);
                if (payments.Count < TrafficPageSize)
                    break;
            }

            for (var skip = 0; ; skip += TrafficPageSize)
            {
                var forwards = await unitOfWork.ForwardCircuitDbRepository.ListAsync(
                                   new ForwardCircuitListQuery(skip, TrafficPageSize,
                                                               Status: ForwardCircuitStatus.Fulfilled),
                                   cancellationToken);
                foreach (var forward in forwards)
                {
                    Add(forward.IncomingChannelId, 0, forward.IncomingAmount.MilliSatoshi);
                    var outgoing = forward.OutgoingChannelId
                                ?? (byScid.TryGetValue(forward.OutgoingShortChannelId, out var id) ? id : (ChannelId?)null);
                    Add(outgoing, forward.OutgoingAmount.MilliSatoshi, 0);
                }

                if (forwards.Count < TrafficPageSize)
                    break;
            }

            for (var skip = 0; ; skip += TrafficPageSize)
            {
                var invoices = await unitOfWork.InvoiceDbRepository.ListAsync(skip, TrafficPageSize);
                foreach (var htlc in invoices.SelectMany(i => i.Htlcs).Where(h => h.State == InvoiceHtlcState.Settled))
                    Add(byScid.TryGetValue(htlc.ShortChannelId, out var id) ? id : (ChannelId?)null, 0, htlc.AmountMsat);
                if (invoices.Count < TrafficPageSize)
                    break;
            }

            _trafficCache = (now, totals);
            return totals;
        }
        finally
        {
            _trafficGate.Release();
        }
    }
}