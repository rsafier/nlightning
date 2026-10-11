namespace NLightning.Domain.Accounting.Financial.Reports;

using Models;

/// <summary>
/// Builds the risk-weighted capital view from a live snapshot (NL-602 A3-T6, plan §6.2 "Audit"). Pure.
/// </summary>
public static class AccountingRiskCapital
{
    /// <summary>The settlement states, in the report's order.</summary>
    public static class States
    {
        public const string WalletConfirmed = "wallet-confirmed";
        public const string WalletUnconfirmed = "wallet-unconfirmed";
        public const string ChannelSettled = "channel-settled";
        public const string OutgoingInFlight = "outgoing-in-flight";
        public const string OutgoingFulfilled = "outgoing-fulfilled";
        public const string IncomingWithPreimage = "incoming-with-preimage";
        public const string IncomingWithoutPreimage = "incoming-without-preimage";
        public const string PendingOnchain = "onchain-pending";
        public const string PendingHtlcOnchain = "onchain-pending-htlc";
        public const string PendingUncounted = "onchain-uncounted";
    }

    /// <summary>
    /// The view of <paramref name="snapshot"/> under <paramref name="weights"/>. A channel's settled balance is its gross
    /// local balance less its offered HTLCs; an offered HTLC the peer fulfilled weighs 0 (paid); an incoming HTLC counts
    /// only with its preimage; the peers' balances (their gross balance less the HTLCs they offered) are left out and
    /// shown as <see cref="AccountingRiskCapitalReport.ExcludedRemoteMsat"/>.
    /// </summary>
    public static AccountingRiskCapitalReport Build(AccountingSnapshot snapshot, AccountingRiskWeights weights)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(weights);

        long settled = 0, outgoing = 0, fulfilled = 0, incomingKnown = 0, incomingUnknown = 0, remote = 0;
        long pending = 0, pendingHtlc = 0, uncounted = 0;
        var channels = new List<AccountingRiskCapitalChannel>(snapshot.Channels.Count);
        foreach (var bucket in snapshot.Channels)
        {
            var channelFulfilled = Math.Clamp(bucket.LocalInFlightFulfilledMsat, 0, bucket.LocalInFlightMsat);
            var channelOutgoing = bucket.LocalInFlightMsat - channelFulfilled;
            var channelSettled = bucket.LocalBalanceMsat - bucket.LocalInFlightMsat;
            var channelKnown = Math.Clamp(bucket.RemoteInFlightPreimageMsat, 0, bucket.RemoteInFlightMsat);
            var channelUnknown = bucket.RemoteInFlightMsat - channelKnown;
            var channelRemote = bucket.RemoteBalanceMsat - bucket.RemoteInFlightMsat;

            settled += channelSettled;
            outgoing += channelOutgoing;
            fulfilled += channelFulfilled;
            incomingKnown += channelKnown;
            incomingUnknown += channelUnknown;
            remote += channelRemote;
            pending += bucket.PendingOnchainMsat;
            pendingHtlc += bucket.PendingHtlcOnchainMsat;
            uncounted += bucket.PendingUncountedMsat;

            var weighted = new AccountingRiskCapitalLine(States.ChannelSettled, channelSettled, weights.ChannelSettled)
                              .WeightedMsat
                         + new AccountingRiskCapitalLine(States.OutgoingInFlight, channelOutgoing,
                                                         weights.OutgoingInFlight).WeightedMsat
                         + new AccountingRiskCapitalLine(States.IncomingWithPreimage, channelKnown,
                                                         weights.IncomingWithPreimage).WeightedMsat
                         + new AccountingRiskCapitalLine(States.PendingOnchain, bucket.PendingOnchainMsat,
                                                         weights.PendingOnchain).WeightedMsat
                         + new AccountingRiskCapitalLine(States.PendingHtlcOnchain, bucket.PendingHtlcOnchainMsat,
                                                         weights.PendingHtlcOnchain).WeightedMsat
                         + new AccountingRiskCapitalLine(States.PendingUncounted, bucket.PendingUncountedMsat,
                                                         weights.PendingUncounted).WeightedMsat;
            channels.Add(new AccountingRiskCapitalChannel(bucket.ChannelId, bucket.ShortChannelId, bucket.State,
                                                          bucket.Counterparty, channelSettled, channelOutgoing,
                                                          channelFulfilled, channelKnown, channelUnknown,
                                                          channelRemote, bucket.PendingOnchainMsat,
                                                          bucket.PendingHtlcOnchainMsat, bucket.PendingUncountedMsat,
                                                          weighted));
        }

        List<AccountingRiskCapitalLine> lines =
        [
            new(States.WalletConfirmed, snapshot.Wallet.ConfirmedMsat, weights.WalletConfirmed),
            new(States.WalletUnconfirmed, snapshot.Wallet.UnconfirmedMsat, weights.WalletUnconfirmed),
            new(States.ChannelSettled, settled, weights.ChannelSettled),
            new(States.OutgoingInFlight, outgoing, weights.OutgoingInFlight),
            new(States.OutgoingFulfilled, fulfilled, 0m),
            new(States.IncomingWithPreimage, incomingKnown, weights.IncomingWithPreimage),
            new(States.IncomingWithoutPreimage, incomingUnknown, 0m),
            new(States.PendingOnchain, pending, weights.PendingOnchain),
            new(States.PendingHtlcOnchain, pendingHtlc, weights.PendingHtlcOnchain),
            new(States.PendingUncounted, uncounted, weights.PendingUncounted)
        ];

        return new AccountingRiskCapitalReport(snapshot.TakenAt, snapshot.BlockHeight, weights,
                                               lines.Where(l => l.AmountMsat != 0).ToList(), channels)
        {
            ExcludedRemoteMsat = remote
        };
    }
}