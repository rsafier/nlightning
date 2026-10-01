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

        WriteSummary(item.Summary);
    }

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
        _output.WriteLine("  Fee:         {0} msat   Hash: {1}", forward.FeeMsat, ShortHash(forward.PaymentHash));
        if (forward.FailureSource is { } source)
            _output.WriteLine("  Failed at:   {0}", ChannelName(forward.FailureSourceScid, source));
    }

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