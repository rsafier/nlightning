using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Integration.Tests.Scale;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Models;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Models;
using Infrastructure.Crypto.Hashes;

/// <summary>
/// The keys and per-commitment points of one side of a seeded channel: the hub's signer, a live peer node's signer, or
/// random valid keys for a peer that never comes online.
/// </summary>
internal interface IScaleKeySource
{
    /// <summary>The key index the next channel uses and its basepoints.</summary>
    (uint KeyIndex, ChannelBasepoints Basepoints) NextChannel();

    CompactPubKey Point(uint keyIndex, ulong commitmentNumber);
}

/// <summary>A node's own signer: the keys a real open would derive (key index 1, 2, ...).</summary>
internal sealed class SignerKeySource(ILightningSigner signer, uint firstIndex = 1) : IScaleKeySource
{
    private uint _next = firstIndex;

    public (uint KeyIndex, ChannelBasepoints Basepoints) NextChannel()
    {
        var index = _next++;
        return (index, signer.GetChannelBasepoints(index));
    }

    public CompactPubKey Point(uint keyIndex, ulong commitmentNumber) =>
        signer.GetPerCommitmentPoint(keyIndex, commitmentNumber);
}

/// <summary>Random valid secp256k1 keys for peers that never come online (nothing is ever signed with them).</summary>
internal sealed class RandomKeySource : IScaleKeySource
{
    private readonly Dictionary<(uint, ulong), CompactPubKey> _points = [];
    private uint _next;

    public (uint KeyIndex, ChannelBasepoints Basepoints) NextChannel() =>
        (_next++, new ChannelBasepoints(NewKey(), NewKey(), NewKey(), NewKey(), NewKey()));

    public CompactPubKey Point(uint keyIndex, ulong commitmentNumber)
    {
        if (!_points.TryGetValue((keyIndex, commitmentNumber), out var point))
            _points[(keyIndex, commitmentNumber)] = point = NewKey();
        return point;
    }

    public static CompactPubKey NewKey() => new Key().PubKey.ToBytes();
}

/// <summary>One peer of the hub.</summary>
/// <param name="Live">A peer node the benchmark runs: its channels carry no seeded HTLCs (its own database must match).</param>
internal sealed record ScalePeer(CompactPubKey NodeId, string Host, uint Port, IScaleKeySource Keys, bool Live = false);

/// <summary>What a seeded channel looks like from both ends (the peer's view feeds a live peer's own database).</summary>
internal sealed record SeededChannel(ChannelModel Hub, ChannelModel PeerSide, ScalePeer Peer, IScaleKeySource HubKeys,
                                     Transaction Funding);

/// <summary>
/// Writes a realistic node database for the channel-scale benchmark (NL-1357): Open anchors channels as a v1 open and
/// <c>channel_ready</c> leave them (key sets, funding output and watch, real short channel id, the first commitment
/// snapshot, the peer row), funding transactions mined on the <see cref="ScaleChain"/>, and on a share of the channels
/// HTLCs we offered locked into both commitments (with their <see cref="HtlcOrigin"/>), so the startup replay, the HTLC
/// deadline monitor and the reload carry real HTLC rows.
/// </summary>
/// <remarks>
/// Every key and point is a real one (the hub's own signer, a live peer's own signer, or random valid keys for offline
/// peers). The engine's commitment signatures are stand-ins of the right shape (nothing verifies them unless a channel is
/// force-closed, which the benchmark never does); HTLCs are added through the real commitment engine.
/// </remarks>
internal sealed class ChannelScaleSeeder
{
    public const uint FirstFundingHeight = 110;
    private const int FundingsPerBlock = 200;
    private const int ChannelsPerSave = 250;

    private static readonly CompactSignature s_signature = CreateSignature();
    private readonly Random _rng;

    public ChannelScaleSeeder(int seed = 7) => _rng = new Random(seed);

    /// <summary>Share of channels that carry HTLCs we offered (1 to 3 each), 0 to 1.</summary>
    public double HtlcChannelShare { get; init; } = 0.1;

    /// <summary>The CLTV expiry of seeded HTLCs: far enough that no deadline is near during the benchmark.</summary>
    public uint HtlcCltvExpiry { get; init; } = 5_000;

    /// <summary>
    /// Builds <paramref name="channelCount"/> channels between the hub (keys <paramref name="hubKeys"/>) and
    /// <paramref name="peers"/> (round robin), mines their fundings on <paramref name="chain"/> and returns them.
    /// </summary>
    public List<SeededChannel> Build(CompactPubKey hubNodeId, IScaleKeySource hubKeys, IReadOnlyList<ScalePeer> peers,
                                     int channelCount,
                                     ScaleChain chain, IChannelIdFactory channelIdFactory)
    {
        var channels = new List<SeededChannel>(channelCount);
        var pendingFundings = new List<(int Index, Transaction Tx)>();
        var drafts = new List<(ScalePeer Peer, uint HubIndex, ChannelBasepoints HubBp, uint PeerIndex,
            ChannelBasepoints PeerBp, Transaction Funding, ulong Capacity, bool HubFunds)>(channelCount);
        for (var i = 0; i < channelCount; i++)
        {
            var peer = peers[i % peers.Count];
            var (hubIndex, hubBp) = hubKeys.NextChannel();
            var (peerIndex, peerBp) = peer.Keys.NextChannel();
            var capacity = (ulong)_rng.Next(1_000, 5_000) * 1_000;
            var funding = CreateFunding(hubBp.FundingPubKey, peerBp.FundingPubKey, capacity);
            drafts.Add((peer, hubIndex, hubBp, peerIndex, peerBp, funding, capacity, _rng.Next(4) != 0));
            pendingFundings.Add((i, funding));
        }

        // Mine the fundings, 200 per block, so every channel has a real short channel id deep in the chain
        var positions = new (uint Height, int TxIndex)[channelCount];
        foreach (var batch in pendingFundings.Chunk(FundingsPerBlock))
        {
            var (_, height) = chain.Mine(batch.Select(b => b.Tx));
            for (var t = 0; t < batch.Length; t++)
                positions[batch[t].Index] = (height, t + 1); // after the coinbase
        }

        for (var i = 0; i < channelCount; i++)
        {
            var (peer, hubIndex, hubBp, peerIndex, peerBp, funding, capacity, hubFunds) = drafts[i];
            var fundingTxId = new TxId(funding.GetHash().ToBytes());
            var channelId = channelIdFactory.CreateV1(fundingTxId, 0);
            var scid = new ShortChannelId(positions[i].Height, (uint)positions[i].TxIndex, 0);
            var hubBalanceSat = hubFunds ? capacity * (uint)_rng.Next(50, 95) / 100
                                           : capacity * (uint)_rng.Next(5, 50) / 100;
            var hub = CreateModel(channelId, fundingTxId, capacity, scid, hubFunds, hubBalanceSat,
                                  (hubIndex, hubBp), (peerIndex, peerBp), hubKeys, peer.Keys,
                                  peer.NodeId);
            var peerSide = CreateModel(channelId, fundingTxId, capacity, scid, !hubFunds,
                                       capacity - hubBalanceSat, (peerIndex, peerBp), (hubIndex, hubBp),
                                       peer.Keys, hubKeys, hubNodeId);
            channels.Add(new SeededChannel(hub, peerSide, peer, hubKeys, funding));
        }

        return channels;
    }

    /// <summary>
    /// Writes the peers and channels of <paramref name="channels"/> into <paramref name="services"/>' database, the hub's
    /// view (<paramref name="hubSide"/>) or the peer's, with the commitment snapshots (and the seeded HTLCs) and the
    /// funding watches; <paramref name="peerFor"/> gives the peer row of each channel's other end.
    /// </summary>
    /// <returns>The number of HTLCs written.</returns>
    public async Task<int> WriteAsync(IServiceProvider services, IReadOnlyList<SeededChannel> channels, bool hubSide,
                                      Func<SeededChannel, PeerModel> peerFor, CancellationToken ct)
    {
        var htlcs = 0;
        var written = new HashSet<CompactPubKey>();
        var watch = Stopwatch.StartNew();
        foreach (var batch in channels.Chunk(ChannelsPerSave))
        {
            ct.ThrowIfCancellationRequested();
            using var scope = services.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            foreach (var seeded in batch)
            {
                var peer = peerFor(seeded);
                if (written.Add(peer.NodeId))
                    await uow.PeerDbRepository.AddOrUpdateAsync(peer);

                var channel = hubSide ? seeded.Hub : seeded.PeerSide;
                var mirror = hubSide ? seeded.PeerSide : seeded.Hub;
                await uow.ChannelDbRepository.AddAsync(channel);
                var (snapshot, offered) = BuildSnapshot(channel, mirror,
                                                        hubSide ? seeded.Peer.Keys : seeded.HubKeys,
                                                        hubSide ? seeded.HubKeys : seeded.Peer.Keys,
                                                        hubSide && !seeded.Peer.Live);
                await uow.ChannelStateDbRepository.InitializeAsync(snapshot);
                foreach (var hash in offered)
                    await uow.ChannelStateDbRepository.SetHtlcOriginAsync(channel.ChannelId, hash.Key,
                                                                          HtlcOrigin.Local(hash.PaymentHash));
                htlcs += offered.Count;
                // The funding watch a confirmed open leaves: seen at its block, completed at the depth
                var fundingWatch = new WatchedTransactionModel(channel.ChannelId,
                                                               channel.FundingOutput!.TransactionId!.Value, 3);
                fundingWatch.SetHeightAndIndex(channel.ShortChannelId.BlockHeight,
                                               channel.ShortChannelId.TransactionIndex);
                fundingWatch.MarkAsCompleted();
                uow.WatchedTransactionDbRepository.Add(fundingWatch);
                uow.WatchedOutpointDbRepository.Add(new WatchedOutpointModel(channel.FundingOutput!.TransactionId!.Value,
                                                                             0, channel.ChannelId,
                                                                             WatchedOutpointPurpose.FundingOutput));
            }

            await uow.SaveChangesAsync();
        }

        Console.WriteLine($"seeded {channels.Count} channels ({htlcs} HTLCs) in {watch.Elapsed.TotalSeconds:F1} s");
        return htlcs;
    }

    /// <summary>
    /// The channel's first commitment state; on <see cref="HtlcChannelShare"/> of the hub's channels with offline peers
    /// (<paramref name="withHtlcs"/>) it is advanced through the engine with 1 to 3 HTLCs we offered, locked
    /// into both commitments (commitment numbers 1 and 1).
    /// </summary>
    private (ChannelCommitments Snapshot, List<(HtlcKey Key, Hash PaymentHash)> Offered) BuildSnapshot(
        ChannelModel channel, ChannelModel mirror, IScaleKeySource remoteKeys, IScaleKeySource localKeys,
        bool withHtlcs)
    {
        var us = ChannelCommitments.Create(channel.ChannelId, CommitmentParams.FromChannel(channel),
                                           channel.LocalBalance.MilliSatoshi, channel.RemoteBalance.MilliSatoshi,
                                           2_500, channel.RemoteKeySet!.CurrentPerCommitmentCompactPoint,
                                           NextRemotePoint(channel), new CommitmentSignatures(s_signature, []));
        var offered = new List<(HtlcKey, Hash)>();
        if (!withHtlcs || _rng.NextDouble() >= HtlcChannelShare)
            return (us, offered);

        var peer = ChannelCommitments.Create(mirror.ChannelId, CommitmentParams.FromChannel(mirror),
                                             mirror.LocalBalance.MilliSatoshi, mirror.RemoteBalance.MilliSatoshi,
                                             2_500, mirror.RemoteKeySet!.CurrentPerCommitmentCompactPoint,
                                             NextRemotePoint(mirror), new CommitmentSignatures(s_signature, []));
        var count = _rng.Next(1, 4);
        for (var h = 0; h < count; h++)
        {
            var preimage = new byte[32];
            _rng.NextBytes(preimage);
            var hash = new Hash(System.Security.Cryptography.SHA256.HashData(preimage));
            var amount = (ulong)_rng.Next(10_000, 200_000) * 1_000;
            CommitmentsResult added;
            try
            {
                added = us.SendAdd(amount, hash, HtlcCltvExpiry + (uint)_rng.Next(0, 500), new byte[1366]);
            }
            catch (CommitmentRefusedException)
            {
                break;
            }

            us = added.Next;
            var htlc = added.Outbound.OfType<OutboundAddHtlc>().Single().Htlc;
            peer = peer.ReceiveAdd(htlc.Id, htlc.AmountMsat, htlc.PaymentHash, htlc.CltvExpiry,
                                   htlc.OnionRoutingPacket).Next;
            offered.Add((htlc.Key, hash));
        }

        if (offered.Count == 0)
            return (us, offered);

        // commitment_signed -> revoke_and_ack, then the peer's commitment_signed -> our revoke_and_ack
        var signed = us.SendCommit(new StandInSigner(us.Params));
        us = signed.Next;
        var received = peer.ReceiveCommit(signed.Outbound.OfType<OutboundCommitmentSigned>().First().Signatures,
                                          AcceptAll.Instance);
        peer = received.Next;
        us = us.ReceiveRevoke(RandomSecret(), remoteKeys.Point(channel.RemoteKeySet.KeyIndex, 2), AcceptAll.Instance)
               .Next;
        var peerSigned = peer.SendCommit(new StandInSigner(peer.Params));
        var ours = us.ReceiveCommit(peerSigned.Outbound.OfType<OutboundCommitmentSigned>().Single().Signatures,
                                    AcceptAll.Instance);
        us = ours.Next;
        return (us, offered);

        CompactPubKey NextRemotePoint(ChannelModel model) =>
            (model == channel ? remoteKeys : localKeys).Point(model.RemoteKeySet!.KeyIndex, 1);
    }

    private ChannelModel CreateModel(ChannelId channelId, TxId fundingTxId, ulong capacity, ShortChannelId scid,
                                     bool isInitiator, ulong localSat, (uint Index, ChannelBasepoints Bp) local,
                                     (uint Index, ChannelBasepoints Bp) remote, IScaleKeySource localKeys,
                                     IScaleKeySource remoteKeys, CompactPubKey remoteNodeId)
    {
        var localParty = new ChannelParty(LightningMoney.Satoshis(354), LightningMoney.Satoshis(capacity / 100),
                                          LightningMoney.MilliSatoshis(1), 483,
                                          LightningMoney.MilliSatoshis(capacity * 1_000), 144);
        var remoteParty = new ChannelParty(LightningMoney.Satoshis(354), LightningMoney.Satoshis(capacity / 100),
                                           LightningMoney.MilliSatoshis(1), 483,
                                           LightningMoney.MilliSatoshis(capacity * 1_000), 144);
        var channelParams = new ChannelParams(localParty, remoteParty, LightningMoney.Satoshis(2_500), 3, true,
                                              FeatureSupport.No);
        var funding = new FundingOutputInfo(LightningMoney.Satoshis(capacity), local.Bp.FundingPubKey,
                                            remote.Bp.FundingPubKey, fundingTxId, 0);
        var localKeySet = new ChannelKeySetModel(local.Index, local.Bp.FundingPubKey, local.Bp.RevocationBasepoint,
                                                 local.Bp.PaymentBasepoint, local.Bp.DelayedPaymentBasepoint,
                                                 local.Bp.HtlcBasepoint, localKeys.Point(local.Index, 0));
        var remoteKeySet = new ChannelKeySetModel(remote.Index, remote.Bp.FundingPubKey, remote.Bp.RevocationBasepoint,
                                                  remote.Bp.PaymentBasepoint, remote.Bp.DelayedPaymentBasepoint,
                                                  remote.Bp.HtlcBasepoint, remoteKeys.Point(remote.Index, 0));
        using var sha256 = new Sha256();
        var obscuring = isInitiator
                            ? new CommitmentNumber(local.Bp.PaymentBasepoint, remote.Bp.PaymentBasepoint, sha256)
                            : new CommitmentNumber(remote.Bp.PaymentBasepoint, local.Bp.PaymentBasepoint, sha256);
        return new ChannelModel(channelParams, channelId, obscuring, funding, isInitiator, s_signature, s_signature,
                                LightningMoney.Satoshis(localSat), localKeySet, 0, 0,
                                LightningMoney.Satoshis(capacity - localSat), remoteKeySet, 0, remoteNodeId, 0,
                                ChannelState.Open, ChannelVersion.V1)
        {
            ShortChannelId = scid
        };
    }

    private Transaction CreateFunding(CompactPubKey a, CompactPubKey b, ulong capacity)
    {
        var keys = new[] { new PubKey(a), new PubKey(b) }.OrderBy(k => k.ToHex(), StringComparer.Ordinal).ToArray();
        var script = PayToMultiSigTemplate.Instance.GenerateScriptPubKey(2, keys);
        var tx = Network.RegTest.CreateTransaction();
        var prevout = new byte[32];
        _rng.NextBytes(prevout);
        tx.Inputs.Add(new OutPoint(new uint256(prevout), 0));
        tx.Outputs.Add(Money.Satoshis((long)capacity), script.WitHash.ScriptPubKey);
        tx.Outputs.Add(Money.Satoshis(50_000), new Key().PubKey.WitHash.ScriptPubKey);
        return tx;
    }

    private Secret RandomSecret()
    {
        var bytes = new byte[32];
        _rng.NextBytes(bytes);
        return new Secret(bytes);
    }

    private static CompactSignature CreateSignature()
    {
        var key = new Key();
        return key.SignCompact(new uint256(Enumerable.Repeat((byte)7, 32).ToArray()), false).Signature;
    }

    /// <summary>Stand-in commitment signatures of the right shape (one per untrimmed HTLC).</summary>
    private sealed class StandInSigner(CommitmentParams @params) : ICommitmentSigner
    {
        public CommitmentSignatures SignRemoteCommitment(ChannelId channelId, ChannelFunding? funding, ulong number,
                                                         CommitmentSpec spec, CompactPubKey remotePerCommitmentPoint,
                                                         MusigPublicNonce? remoteVerificationNonce = null) =>
            new(s_signature, Enumerable.Repeat(s_signature,
                                               CommitmentFeeCalculator.UntrimmedHtlcCount(
                                                   spec, @params.Remote.DustLimitSatoshis, @params.Format))
                                       .ToList());
    }

    private sealed class AcceptAll : ICommitmentVerifier, IRevocationVerifier
    {
        public static readonly AcceptAll Instance = new();

        public bool VerifyLocalCommitment(ChannelId channelId, ChannelFunding? funding, ulong number,
                                          CommitmentSpec spec, CommitmentSignatures signatures) => true;

        public bool IsValidSecret(Secret perCommitmentSecret, CompactPubKey expectedPerCommitmentPoint) => true;
    }
}