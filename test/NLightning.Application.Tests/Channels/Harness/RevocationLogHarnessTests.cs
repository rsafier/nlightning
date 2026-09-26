using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Channels.Harness;

using Application.Channels.Services;
using Domain.Channels.Commitments;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Onion.ValueObjects;

/// <summary>
/// Proof O1 (BOLT 5 plan O1-T1): two in-process nodes run 30 HTLC round trips through the production handlers and
/// channel operations. After every exchange each node's saved revocation log holds exactly the revoked peer
/// commitments that had HTLCs; every revoke_and_ack reported the commitment it revoked, in order, in its own save; and
/// every logged spec rebuilds the very transaction that was signed for that number, so a breach of it can be matched
/// output by output. A crash mid-run loses nothing: the restarted node's log is still complete.
/// </summary>
public class RevocationLogHarnessTests
{
    private const int RoundTrips = 30;
    private const uint CltvExpiry = 700;

    private static readonly OnionPacket s_onion = new(TwoNodeHarness.Onion);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_ThirtyHtlcRoundTrips_When_EveryRevokeAndAckIsSaved_Then_TheLogMatchesTheRevokedCommitmentsWithHtlcs(
        bool hasAnchors)
    {
        // Arrange
        using var harness = new TwoNodeHarness(hasAnchors);
        var ct = TestContext.Current.CancellationToken;

        // Act & Assert: each round trip adds an HTLC each way, then settles both; a crash hits Bob halfway
        for (var i = 0; i < RoundTrips; i++)
        {
            if (i == RoundTrips / 2)
                harness.Bob.Store.CrashAtSave = harness.Bob.Store.Saves + 2;

            var aliceId = await OfferAsync(harness.Alice, AmountMsat(i), TwoNodeHarness.Preimage(i));
            var bobId = await OfferAsync(harness.Bob, AmountMsat(i + 3), TwoNodeHarness.Preimage(1_000 + i));
            await harness.PumpAsync();
            AssertLog(harness.Alice);
            AssertLog(harness.Bob);

            if (i % 3 == 2)
            {
                await harness.Bob.Operations.FailHtlcAsync(TwoNodeHarness.ChannelId, aliceId, new byte[292], ct);
                await harness.Alice.Operations.FailHtlcAsync(TwoNodeHarness.ChannelId, bobId, new byte[292], ct);
            }
            else
            {
                await harness.Bob.Operations.FulfillHtlcAsync(TwoNodeHarness.ChannelId, aliceId,
                                                              TwoNodeHarness.Preimage(i), ct);
                await harness.Alice.Operations.FulfillHtlcAsync(TwoNodeHarness.ChannelId, bobId,
                                                                TwoNodeHarness.Preimage(1_000 + i), ct);
            }

            await harness.PumpAsync();
            AssertLog(harness.Alice);
            AssertLog(harness.Bob);
        }

        // Assert: every logged spec rebuilds the transaction signed at the time
        AssertRebuiltTxIds(harness.Alice);
        AssertRebuiltTxIds(harness.Bob);

        // Assert: the run revoked many commitments with HTLCs on both sides, and Bob went through a restart
        Assert.True(harness.Restarts >= 1, "Bob never crashed");
        Assert.Empty(harness.Alice.State.Htlcs);
        Assert.Empty(harness.Bob.State.Htlcs);
        Assert.True(harness.Alice.Store.CommittedRevocationLog.Count >= RoundTrips,
                    $"Alice logged only {harness.Alice.Store.CommittedRevocationLog.Count} revoked commitments");
        Assert.True(harness.Bob.Store.CommittedRevocationLog.Count >= RoundTrips,
                    $"Bob logged only {harness.Bob.Store.CommittedRevocationLog.Count} revoked commitments");
        Assert.Contains(harness.Alice.Store.CommittedRevocations, c => c.Spec.Htlcs.Count == 0);
    }

    /// <summary>
    /// The saved log of <paramref name="node"/>: every peer commitment below the current one was reported revoked once,
    /// in order, and the log is exactly those with HTLCs.
    /// </summary>
    private static void AssertLog(HarnessNode node)
    {
        var store = node.Store;
        var current = node.State.RemoteCommit.Number;

        // Every revoke_and_ack reported its commitment, in order (a crashed save reported nothing)
        Assert.Equal(Enumerable.Range(0, (int)current).Select(n => (ulong)n),
                     store.CommittedRevocations.Select(c => c.Number));

        // The log is the revoked commitments with HTLCs, nothing else
        Assert.Equal(store.CommittedRevocations.Where(c => c.Spec.Htlcs.Count > 0).Select(c => c.Number),
                     store.CommittedRevocationLog.Keys);
    }

    /// <summary>Every revoked commitment of <paramref name="node"/>, rebuilt from its saved spec, has the txid the node
    /// signed for that number (and was signed with one txid only).</summary>
    private static void AssertRebuiltTxIds(HarnessNode node)
    {
        var store = node.Store;

        // Rebuilding a revoked commitment from its logged spec gives the txid signed for that number at the time
        var signing = node.Services.GetRequiredService<CommitmentSigningService>();
        var signed = node.Signed.GroupBy(s => s.Number).ToDictionary(g => g.Key, g => g.Select(s => s.TxId).ToList());
        foreach (var revoked in store.CommittedRevocations.Where(c => c.Number > 0))
        {
            Assert.True(signed.TryGetValue(revoked.Number, out var txIds), $"{node.Name} never signed #{revoked.Number}");
            Assert.Single(txIds.Distinct());
            var rebuilt = signing.SignRemoteCommitment(node.Channel, CommitmentTxSpec.FromCommitmentSpec(revoked.Spec),
                                                       revoked.Number, node.Peer.Point(revoked.Number));
            Assert.Equal(txIds[0], rebuilt.CommitmentTxId);
            Assert.Equal(revoked.PerCommitmentPoint, node.Peer.Point(revoked.Number));
        }
    }

    private static Task<ulong> OfferAsync(HarnessNode node, ulong amountMsat, Secret preimage)
    {
        var hash = TwoNodeHarness.Hash(preimage);
        return node.Operations.OfferHtlcAsync(TwoNodeHarness.ChannelId, LightningMoney.MilliSatoshis(amountMsat), hash,
                                              CltvExpiry, s_onion, null, HtlcOrigin.Local(hash),
                                              TestContext.Current.CancellationToken);
    }

    /// <summary>Amounts from dust (trimmed on one or both commitments) to about 20,000 sat.</summary>
    private static ulong AmountMsat(int i) => (i % 4) switch
    {
        0 => 700_000UL + (ulong)i * 1_000,
        _ => (5_000UL + (ulong)i * 500) * 1_000
    };
}