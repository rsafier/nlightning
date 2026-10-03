namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

/// <summary>
/// Prints a <c>listforwards</c> page (NL-597): one line per forward, then the totals over the whole filtered set and
/// the HTLCs refused before forwarding since start (NL-598).
/// </summary>
public sealed class ListForwardsPrinter : IPrinter<ListForwardsIpcResponse>
{
    private readonly TextWriter _output;

    public ListForwardsPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(ListForwardsIpcResponse item)
    {
        _output.WriteLine("Forwards:");
        if (item.Forwards.Count == 0)
            _output.WriteLine("  None");

        foreach (var forward in item.Forwards)
            WriteForward(forward);

        if (item.TrampolineRelays is { Count: > 0 } relays)
        {
            _output.WriteLine(new string('-', 96));
            _output.WriteLine("Trampoline relays:");
            foreach (var relay in relays)
                WriteTrampolineRelay(relay);
        }

        WriteSummary(item.Summary);
    }

    // NL-875: kind trampoline, N incoming parts paid into one payment to the next trampoline node
    private void WriteTrampolineRelay(TrampolineRelayIpcResponse relay)
    {
        _output.WriteLine(new string('-', 96));
        _output.WriteLine("  Kind:        trampoline   Status: {0}{1}", TrampolineStatusName(relay.Status),
                          relay.FailureCodeName is null ? string.Empty : $" ({relay.FailureCodeName})");
        _output.WriteLine("  Created:     {0:yyyy-MM-dd HH:mm:ss} UTC{1}", UnixTime(relay.CreatedAtUnixSeconds),
                          relay.CompletedAtUnixSeconds is { } completed
                              ? $"   Completed: {UnixTime(completed):yyyy-MM-dd HH:mm:ss} UTC"
                              : string.Empty);
        var channels = relay.IncomingChannelIds.Select((id, i) => ChannelName(
                                                           i < relay.IncomingChannelScids.Count
                                                               ? relay.IncomingChannelScids[i]
                                                               : null, id));
        _output.WriteLine("  In:          {0} part(s), {1} of {2} msat  on {3}", relay.Parts,
                          relay.IncomingAmountMsat, relay.IncomingTotalMsat, string.Join(", ", channels));
        _output.WriteLine("  Out:         {0} msat to {1}", relay.AmountOutMsat,
                          relay.NextNodeId ?? "the recipient's blinded paths");
        _output.WriteLine("  Fee:         {0}   Hash: {1}",
                          relay.FeeEarnedMsat is { } fee ? $"{fee} msat"
                          : relay.Status == 3 ? "none (failed)" : "pending",
                          ShortHash(relay.PaymentHash));
    }

    private static string TrampolineStatusName(byte status) => status switch
    {
        0 => "collecting",
        1 => "sending",
        2 => "fulfilled",
        3 => "failed",
        _ => $"unknown({status})"
    };

    private void WriteForward(ForwardInfoIpcResponse forward)
    {
        var separator = new string('-', 96);
        _output.WriteLine(separator);
        _output.WriteLine("  Status:      {0}{1}", StatusName(forward.Status),
                          forward.FailureCodeName is null ? string.Empty : $" ({forward.FailureCodeName})");
        _output.WriteLine("  Created:     {0:yyyy-MM-dd HH:mm:ss} UTC{1}", UnixTime(forward.CreatedAtUnixSeconds),
                          forward.ResolvedAtUnixSeconds is { } resolved
                              ? $"   Resolved: {UnixTime(resolved):yyyy-MM-dd HH:mm:ss} UTC"
                              : string.Empty);
        _output.WriteLine("  In:          {0}  HTLC {1}  {2} msat  cltv {3}",
                          ChannelName(forward.IncomingChannelScid, forward.IncomingChannelId),
                          forward.IncomingHtlcId, forward.IncomingAmountMsat, forward.IncomingCltvExpiry);
        // Offered: the channel it went out on (its scid when known, else the channel id); not offered yet: the
        // requested short_channel_id
        var outgoingName = forward.OutgoingChannelId is { } outgoingChannel
            ? forward.OutgoingChannelScid ?? outgoingChannel
            : forward.OutgoingShortChannelId;
        _output.WriteLine("  Out:         {0}  {1} msat  cltv {2}",
                          outgoingName, forward.OutgoingAmountMsat, forward.OutgoingCltvExpiry);
        _output.WriteLine("  Fee:         {0}   Hash: {1}", FeeText(forward), ShortHash(forward.PaymentHash));
        if (forward.FailureSource is { } source)
            _output.WriteLine("  Failed:      {0} {1}",
                              // Offered: the failure came back from downstream (or timed out on chain); a forwarding
                              // node cannot read the failure onion's code. Not offered: our offer was refused here.
                              forward.OutgoingChannelId is null ? "not offered on" : "downstream of",
                              ChannelName(forward.FailureSourceScid, source));
    }

    // A failed forward earns nothing; pending and offered ones earn their fee only once fulfilled
    private static string FeeText(ForwardInfoIpcResponse forward) => forward.Status switch
    {
        2 => $"{forward.FeeMsat} msat",
        3 => "none (failed)",
        _ => $"{forward.FeeMsat} msat once fulfilled"
    };

    private void WriteSummary(ForwardSummaryIpcResponse summary)
    {
        var separator = new string('-', 96);
        _output.WriteLine(separator);
        _output.WriteLine("  Totals over the filtered set: {0} forward(s) - pending {1}, offered {2}, fulfilled {3}, "
                        + "failed {4}; fees earned {5} msat",
                          summary.Pending + summary.Offered + summary.Fulfilled + summary.Failed,
                          summary.Pending, summary.Offered, summary.Fulfilled, summary.Failed,
                          summary.FulfilledFeesMsat);
        _output.WriteLine("  Refused before forwarding since start: {0}{1}", summary.RefusedTotal,
                          summary.RefusedByReason.Count == 0
                              ? string.Empty
                              : " (" + string.Join(", ",
                                                   summary.RefusedByReason.Select(r => $"{r.Reason} {r.Count}"))
                                + ")");
    }

    private static string ChannelName(string? scid, string fallback) => scid ?? fallback;

    private static string StatusName(byte status) => status switch
    {
        0 => "pending",
        1 => "offered",
        2 => "fulfilled",
        3 => "failed",
        _ => $"unknown({status})"
    };

    private static DateTimeOffset UnixTime(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);

    private static string ShortHash(string hash) => hash.Length <= 16 ? hash : hash[..16] + "…";
}