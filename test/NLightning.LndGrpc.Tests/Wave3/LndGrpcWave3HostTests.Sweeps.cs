namespace NLightning.LndGrpc.Tests.Wave3;

using Domain.Accounting.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using LndGrpc.Macaroons;
using Testing.Lnd.Walletrpc;

/// <summary>walletrpc <c>ListSweeps</c> and <c>PendingSweeps</c> from the BOLT 5 resolution rows (NL-1245).</summary>
public sealed partial class LndGrpcWave3HostTests
{
    private static readonly ChannelId s_closedChannel = new(Enumerable.Repeat((byte)0x5C, 32).ToArray());

    [Fact]
    public async Task Given_ASweepAPenaltyAndAReplacedAttempt_When_ListSweeps_Then_TheLiveSweepsAreListed()
    {
        // Arrange: a confirmed to_local sweep paying our wallet at 130, an unconfirmed penalty, a replaced attempt,
        // and the channel's own commitment (not a sweep)
        var sweep = AddBroadcast(BroadcastPurpose.Sweep, 1, BroadcastState.Confirmed, 130);
        var penalty = AddBroadcast(BroadcastPurpose.Penalty, 2, BroadcastState.Pending, null);
        AddBroadcast(BroadcastPurpose.Sweep, 3, BroadcastState.Replaced, null);
        AddBroadcast(BroadcastPurpose.LocalCommitment, 4, BroadcastState.Confirmed, 120);
        _closes.Add(new ChannelCloseModel(s_closedChannel, ChannelCloseKind.LocalCommitment,
                                          new TxId(Enumerable.Repeat((byte)0xC0, 32).ToArray()), 3, 120,
                                          new Hash(new byte[32]), DateTimeOffset.UtcNow));
        AddEvent(AccountingEventKind.WalletReceived, sweep, 0, 130, 90_000_000);
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var ids = await connection.WalletKitClient.ListSweepsAsync(new ListSweepsRequest(), cancellationToken: Ct);
        var unconfirmed = await connection.WalletKitClient.ListSweepsAsync(new ListSweepsRequest { StartHeight = -1 },
                                                                           cancellationToken: Ct);
        var verbose = await connection.WalletKitClient.ListSweepsAsync(new ListSweepsRequest { Verbose = true },
                                                                       cancellationToken: Ct);

        // Assert: txids in display order; verbose is the wallet history of those that moved a wallet output
        Assert.Equal([sweep.ToString(), penalty.ToString()], ids.TransactionIds.TransactionIds);
        Assert.Equal([penalty.ToString()], unconfirmed.TransactionIds.TransactionIds);
        var listed = Assert.Single(verbose.TransactionDetails.Transactions);
        Assert.Equal(sweep.ToString(), listed.TxHash);
        Assert.Equal(90_000, listed.Amount);
        Assert.Equal(130, listed.BlockHeight);
    }

    [Fact]
    public async Task Given_OutputsBeingResolved_When_PendingSweeps_Then_OursAreListedWithLndsWitnessTypes()
    {
        // Arrange: our to_local waiting for its CSV, an HTLC claim broadcast at 2,500 sat/kw, a resolved output and the
        // peer's output
        var commitment = new TxId(Enumerable.Repeat((byte)0xC0, 32).ToArray());
        _closes.Add(new ChannelCloseModel(s_closedChannel, ChannelCloseKind.RemoteCommitment, commitment, 3, 120,
                                          new Hash(new byte[32]), DateTimeOffset.UtcNow));
        var claim = AddBroadcast(BroadcastPurpose.HtlcClaim, 5, BroadcastState.Pending, null, 2_500);
        AddOutput(commitment, 0, OutputDescriptorKind.DelayedToLocal, OutputResolutionState.Waiting,
                  waitUntil: 264, deadline: null);
        AddOutput(commitment, 1, OutputDescriptorKind.RemoteOfferedHtlc, OutputResolutionState.Broadcast,
                  waitUntil: null, deadline: 200, resolving: claim);
        AddOutput(commitment, 2, OutputDescriptorKind.PaymentToRemote, OutputResolutionState.Resolved,
                  waitUntil: null, deadline: null);
        AddOutput(commitment, 3, OutputDescriptorKind.PeerOutput, OutputResolutionState.Ignored,
                  waitUntil: null, deadline: null);
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var response = await connection.WalletKitClient.PendingSweepsAsync(new PendingSweepsRequest(),
                                                                           cancellationToken: Ct);

        // Assert
        Assert.Equal(2, response.PendingSweeps.Count);
        var toLocal = response.PendingSweeps[0];
        Assert.Equal((WitnessType.CommitmentTimeLock, 0u, 264u, 0u, commitment.ToString()),
                     (toLocal.WitnessType, toLocal.Outpoint.OutputIndex, toLocal.MaturityHeight,
                      toLocal.BroadcastAttempts, toLocal.Outpoint.TxidStr));
        Assert.Equal(1_000u, toLocal.AmountSat);
        var htlc = response.PendingSweeps[1];
        Assert.Equal((WitnessType.HtlcAcceptedRemoteSuccess, 1u, 200u, 1u, 10ul),
                     (htlc.WitnessType, htlc.Outpoint.OutputIndex, htlc.DeadlineHeight, htlc.BroadcastAttempts,
                      htlc.SatPerVbyte));
    }

    private TxId AddBroadcast(BroadcastPurpose purpose, byte tag, BroadcastState state, uint? height,
                              uint feeratePerKw = 0)
    {
        var tx = NBitcoin.Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new NBitcoin.TxIn(new NBitcoin.OutPoint(new NBitcoin.uint256(tag), 0)));
        tx.Outputs.Add(NBitcoin.Money.Satoshis(90_000), new NBitcoin.Key().PubKey.WitHash.ScriptPubKey);
        var txId = new TxId(tx.GetHash().ToBytes());
        _broadcastRows.Add(BroadcastTransactionModel.Restore(txId, NBitcoin.BitcoinSerializableExtensions.ToBytes(tx), purpose, s_closedChannel,
                                                             feeratePerKw, null, 120, state, height,
                                                             height is null ? (Hash?)null : new Hash(new byte[32]),
                                                             DateTimeOffset.UnixEpoch.AddSeconds(tag)));
        return txId;
    }

    private void AddOutput(TxId commitment, uint index, OutputDescriptorKind descriptor, OutputResolutionState state,
                           uint? waitUntil, uint? deadline, TxId? resolving = null)
    {
        var data = new OutputDescriptorData(1_000, new byte[22], null, 0, false, null, null);
        _outputs.Add(new OutputResolutionModel
        {
            TransactionId = commitment,
            OutputIndex = index,
            ChannelId = s_closedChannel,
            Descriptor = descriptor,
            DescriptorData = data.Encode(),
            State = state,
            WaitUntilHeight = waitUntil,
            DeadlineHeight = deadline,
            ResolvingTransactionId = resolving,
            HtlcDirection = descriptor == OutputDescriptorKind.RemoteOfferedHtlc ? HtlcDirection.Incoming : null
        });
    }
}