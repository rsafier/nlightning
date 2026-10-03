using System.Globalization;
using Google.Protobuf;

namespace NLightning.Testing.Cluster.Nodes.Lnd;

using Testing.Lnd.Lnrpc;

/// <summary>
/// Conversions between LND's gRPC types and the facade's (<see cref="ILightningTestPeer"/>): transaction ids, short
/// channel ids, channel points and amounts.
/// </summary>
public static class LndMapping
{
    /// <summary>
    /// A transaction id in display order (lower-case hex) from LND's <c>funding_txid_bytes</c>, which are in internal
    /// (little-endian) order.
    /// </summary>
    public static string TxIdFromInternalBytes(ReadOnlySpan<byte> internalOrder)
    {
        if (internalOrder.Length != 32)
            throw new ArgumentException($"A txid has 32 bytes, not {internalOrder.Length}", nameof(internalOrder));

        Span<byte> display = stackalloc byte[32];
        internalOrder.CopyTo(display);
        display.Reverse();
        return Convert.ToHexStringLower(display);
    }

    /// <summary>The display-order txid of a <see cref="ChannelPoint"/>, whichever form it carries.</summary>
    public static string TxIdOf(ChannelPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        return point.FundingTxidCase switch
        {
            ChannelPoint.FundingTxidOneofCase.FundingTxidBytes => TxIdFromInternalBytes(point.FundingTxidBytes.Span),
            ChannelPoint.FundingTxidOneofCase.FundingTxidStr => point.FundingTxidStr.ToLowerInvariant(),
            _ => throw new ArgumentException("The channel point has no funding txid", nameof(point))
        };
    }

    /// <summary>A <see cref="ChannelPoint"/> for a display-order txid and output (for close and policy calls).</summary>
    public static ChannelPoint ToChannelPoint(string fundingTxId, uint outputIndex) =>
        new() { FundingTxidStr = fundingTxId, OutputIndex = outputIndex };

    /// <summary>
    /// Splits LND's <c>channel_point</c> string (<c>txid:index</c>, txid in display order).
    /// </summary>
    public static (string TxId, int OutputIndex) ParseChannelPoint(string channelPoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelPoint);

        var colon = channelPoint.LastIndexOf(':');
        if (colon != 64 || !int.TryParse(channelPoint.AsSpan(colon + 1), NumberStyles.None,
                                         CultureInfo.InvariantCulture, out var index))
            throw new FormatException($"Not a channel point: {channelPoint}");

        return (channelPoint[..colon].ToLowerInvariant(), index);
    }

    /// <summary>
    /// LND's 64-bit <c>chan_id</c> as <c>block x tx x output</c>, or null for 0 (not confirmed yet).
    /// </summary>
    public static string? FormatShortChannelId(ulong chanId)
    {
        if (chanId == 0)
            return null;

        var block = chanId >> 40;
        var tx = (chanId >> 16) & 0xFFFFFF;
        var output = chanId & 0xFFFF;
        return string.Create(CultureInfo.InvariantCulture, $"{block}x{tx}x{output}");
    }

    /// <summary>
    /// A push amount in whole satoshis (LND's <c>push_sat</c>).
    /// </summary>
    /// <exception cref="ArgumentException">The amount is negative or not a whole number of satoshis.</exception>
    public static long ToWholeSatoshis(long msat, string what)
    {
        if (msat < 0 || msat % 1000 != 0)
            throw new ArgumentException($"{what} must be a non-negative whole number of satoshis, not {msat} msat",
                                        nameof(msat));

        return msat / 1000;
    }

    /// <summary>The facade's view of an LND channel.</summary>
    public static TestChannel ToTestChannel(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var (txId, index) = ParseChannelPoint(channel.ChannelPoint);
        return new TestChannel(channel.RemotePubkey, txId, index, FormatShortChannelId(channel.ChanId),
                               channel.Capacity, checked(channel.LocalBalance * 1000), channel.Active);
    }

    /// <summary>The payment hash of an invoice as lower-case hex.</summary>
    public static string ToHex(ByteString bytes) => Convert.ToHexStringLower(bytes.Span);
}