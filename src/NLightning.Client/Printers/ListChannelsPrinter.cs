using System.Globalization;

namespace NLightning.Client.Printers;

using Domain.Channels.Splicing.Enums;
using Transport.Ipc.Responses;

public sealed class ListChannelsPrinter : IPrinter<ListChannelsIpcResponse>
{
    private readonly TextWriter _output;

    public ListChannelsPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(ListChannelsIpcResponse item)
    {
        _output.WriteLine("Channels:");
        if (item.Channels.Count == 0)
        {
            _output.WriteLine("  None");
            return;
        }

        _output.WriteLine(PaymentsPrintFormat.Separator);
        foreach (var channel in item.Channels)
        {
            _output.WriteLine("  Id:                 {0}", channel.ChannelId);
            _output.WriteLine("  Peer:               {0} ({1})", channel.PeerId,
                              channel.IsPeerConnected ? "connected" : "disconnected");
            _output.WriteLine("  State:              {0}", channel.State);
            _output.WriteLine("  Reestablished:      {0}", channel.IsReestablished ? "Yes" : "No");
            _output.WriteLine("  Initiator:          {0}", channel.IsInitiator ? "Yes" : "No");
            _output.WriteLine("  Short Channel Id:   {0}", FormatShortChannelId(channel.ShortChannelId));
            // The funding txid in the display (bitcoind, block explorer) byte order, as TxId.ToString() prints it since NL-519.
            _output.WriteLine("  Funding Output:     {0}",
                              channel.FundingTxId is { } fundingTxId
                                  ? $"{DisplayOrder.ToHex(fundingTxId)}:{channel.FundingOutputIndex}"
                                  : "-");
            _output.WriteLine("  Capacity (sat):     {0}", Invariant(channel.Capacity.Satoshi));
            _output.WriteLine("  Local (msat):       {0}", Invariant(channel.LocalBalance.MilliSatoshi));
            _output.WriteLine("  Remote (msat):      {0}", Invariant(channel.RemoteBalance.MilliSatoshi));
            _output.WriteLine("  Commitment (l/r):   {0}/{1}", Invariant(channel.LocalCommitmentNumber),
                              Invariant(channel.RemoteCommitmentNumber));
            _output.WriteLine("  HTLCs (out/in):     {0}/{1}", Invariant(channel.OfferedHtlcCount),
                              Invariant(channel.ReceivedHtlcCount));
            _output.WriteLine("  Fee (base/ppm):     {0} msat/{1}{2}", Invariant(channel.FeeBaseMsat),
                              Invariant(channel.FeePpm), channel.HasPolicyOverride ? " (channel policy)" : "");
            _output.WriteLine("  CLTV Delta:         {0}", Invariant(channel.CltvExpiryDelta));
            _output.WriteLine("  HTLC (min/max):     {0}/{1} msat", Invariant(channel.HtlcMinimumMsat),
                              Invariant(channel.HtlcMaximumMsat));
            PaymentsPrintFormat.WriteLabels(_output, channel.Label, channel.Tags);
            PrintFundings(channel);
            if (channel.DataLossDetected)
                _output.WriteLine("  DATA LOSS DETECTED: do not force-close this channel");
            _output.WriteLine(PaymentsPrintFormat.Separator);
        }
    }

    /// <summary>
    /// The fundings (current, pending splices, replaced ones still resolvable) and the retired short channel ids
    /// (splicing plan §3.10, D12); nothing from a daemon that predates them (null lists).
    /// </summary>
    private void PrintFundings(ChannelInfoIpcResponse channel)
    {
        if (channel.Fundings is { Count: > 0 } fundings)
        {
            _output.WriteLine("  Fundings:");
            foreach (var funding in fundings)
            {
                // A pending initial funding is another signed attempt of a dual-funded open (RBF, NL-535)
                if (funding is { Status: ChannelFundingStatus.Pending, Kind: ChannelFundingKind.Initial })
                    _output.WriteLine("    - Pending (another attempt of the dual-funded open)");
                else
                    _output.WriteLine("    - {0} ({1})", funding.Status, funding.Kind);
                _output.WriteLine("      Outpoint:         {0}:{1}", DisplayOrder.ToHex(funding.FundingTxId),
                                  Invariant(funding.OutputIndex));
                _output.WriteLine("      Capacity (sat):   {0}", Invariant(funding.Capacity.Satoshi));
                _output.WriteLine("      Depth:            {0}",
                                  funding.Depth is { } depth ? Invariant(depth) : "unconfirmed");
                _output.WriteLine("      Short Channel Id: {0}", FormatShortChannelId(funding.ShortChannelId));
                if (funding.Kind != ChannelFundingKind.Initial)
                    _output.WriteLine("      splice_locked:    sent {0}, received {1}",
                                      funding.SpliceLockedSent ? "Yes" : "No",
                                      funding.SpliceLockedReceived ? "Yes" : "No");
            }
        }

        if (channel.RetiredShortChannelIds is { Count: > 0 } retired)
        {
            _output.WriteLine("  Retired SCIDs:");
            foreach (var scid in retired)
                _output.WriteLine("    - {0} (retired at {1}, expires at {2})",
                                  FormatShortChannelId(scid.ShortChannelId), Invariant(scid.RetiredAtHeight),
                                  Invariant(scid.ExpiresAtHeight));
        }
    }

    private static string FormatShortChannelId(ulong? shortChannelId) =>
        shortChannelId is { } scid ? $"{scid >> 40}x{(scid >> 16) & 0xFFFFFF}x{scid & 0xFFFF}" : "-";

    private static string Invariant(IFormattable value) => value.ToString(null, CultureInfo.InvariantCulture);
}