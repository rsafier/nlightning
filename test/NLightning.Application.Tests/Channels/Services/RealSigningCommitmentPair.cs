using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Services;

using Application.Channels.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Models;
using Infrastructure.Bitcoin;
using Infrastructure.Crypto.Hashes;

/// <summary>
/// One side of <see cref="RealSigningCommitmentPair"/>: a commitment engine whose ports are the production ones
/// (<see cref="EngineCommitmentSignerPort"/>, <see cref="EngineCommitmentVerifierPort"/>,
/// <see cref="EngineRevocationVerifierPort"/>) resolved from <see cref="CommitmentEngineServiceCollectionExtensions"/>
/// over a real <c>LocalLightningSigner</c>, key derivation, commitment factory and builders.
/// </summary>
internal sealed class RealSigningNode : IDisposable
{
    private delegate bool TryGetChannelCallback(ChannelId channelId, out ChannelModel? channel);

    private readonly ServiceProvider _provider;

    public string Name { get; }
    public ILightningSigner Signer { get; }
    public CommitmentSigningService SigningService { get; }
    public ICommitmentSigner CommitmentSigner { get; }
    public ICommitmentVerifier CommitmentVerifier { get; }
    public IRevocationVerifier RevocationVerifier { get; }
    public ChannelBasepoints Basepoints { get; }
    public uint KeyIndex { get; }
    public ChannelModel Channel { get; set; } = null!;
    public ChannelCommitments State { get; set; } = null!;

    /// <summary>Every domain event this node's engine raised, with the step that raised it (N4-T4).</summary>
    public List<(string Step, IChannelDomainEvent Event)> Events { get; } = [];

    /// <summary>Records the events of <paramref name="result"/> and swaps in its snapshot.</summary>
    public void Apply(string step, CommitmentsResult result)
    {
        Events.AddRange(result.Events.Select(e => (step, e)));
        State = result.Next;
    }

    public RealSigningNode(string name, byte seedTag)
    {
        Name = name;
        KeyIndex = seedTag;

        var rootKey = new ExtKey(new Key(Enumerable.Repeat(seedTag, 32).ToArray()), new byte[32]);
        var secureKeyManager = new Mock<ISecureKeyManager>();
        secureKeyManager.Setup(x => x.GetChannelKeyAtIndex(It.IsAny<uint>()))
                        .Returns((uint index) => (ExtPrivKey)rootKey.Derive((int)index, true).ToBytes());

        var channelMemoryRepository = new Mock<IChannelMemoryRepository>();
        channelMemoryRepository
           .Setup(x => x.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
           .Returns(new TryGetChannelCallback((ChannelId channelId, out ChannelModel? channel) =>
            {
                channel = Channel is not null && Channel.ChannelId == channelId ? Channel : null;
                return channel is not null;
            }));

        var nodeOptions = new NodeOptions();
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(nodeOptions));
        services.AddSingleton(secureKeyManager.Object);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddSingleton(channelMemoryRepository.Object);
        services.AddBitcoinInfrastructure();
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddCommitmentEngineServices();
        _provider = services.BuildServiceProvider();

        Signer = _provider.GetRequiredService<ILightningSigner>();
        SigningService = _provider.GetRequiredService<CommitmentSigningService>();
        CommitmentSigner = _provider.GetRequiredService<ICommitmentSigner>();
        CommitmentVerifier = _provider.GetRequiredService<ICommitmentVerifier>();
        RevocationVerifier = _provider.GetRequiredService<IRevocationVerifier>();
        Basepoints = Signer.GetChannelBasepoints(KeyIndex);
    }

    public CompactPubKey Point(ulong commitmentNumber) => Signer.GetPerCommitmentPoint(KeyIndex, commitmentNumber);

    public void Dispose() => _provider.Dispose();
}

/// <summary>
/// Two commitment engines (Alice = funder, Bob) that sign and verify <b>real</b> BOLT 3 commitments and HTLC
/// transactions through the engine ports (NL-230). Every <c>commitment_signed</c> is delivered to the peer's engine,
/// which verifies the secp256k1 signatures against the commitment it builds itself; every <c>revoke_and_ack</c>
/// carries the real per-commitment secret released by the signer's revocation guard. The txid the sender signed and the
/// txid the receiver verified are recorded for every commitment (invariant I7).
/// </summary>
internal sealed class RealSigningCommitmentPair : IDisposable
{
    public const ulong FundingSatoshis = 1_000_000;
    public const ulong AlicePushedSatoshis = 200_000;
    public const uint InitialFeeratePerKw = 2_500;

    private static readonly byte[] s_onion = new byte[1366];

    public static readonly ChannelId ChannelId = new(Enumerable.Repeat((byte)0x5A, 32).ToArray());

    public RealSigningNode Alice { get; }
    public RealSigningNode Bob { get; }

    /// <summary>Every signed commitment: (signer, holder commitment number, txid signed, txid verified).</summary>
    public List<(string Signer, ulong Number, TxId Signed, TxId Verified)> Commitments { get; } = [];

    public RealSigningCommitmentPair(bool hasAnchors)
    {
        Alice = new RealSigningNode("Alice", 0xA1);
        Bob = new RealSigningNode("Bob", 0xB0);

        // Per-side values differ on purpose (dust limit, to_self_delay) so a direction mix-up changes the txid
        var aliceParty = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(10_000),
                                          LightningMoney.MilliSatoshis(1_000), 30,
                                          LightningMoney.Satoshis(FundingSatoshis), 144);
        var bobParty = new ChannelParty(LightningMoney.Satoshis(600), LightningMoney.Satoshis(10_000),
                                        LightningMoney.MilliSatoshis(1_000), 30,
                                        LightningMoney.Satoshis(FundingSatoshis), 100);
        var fundingTxId = new TxId(Enumerable.Repeat((byte)0x77, 32).ToArray());
        var obscuring = new CommitmentNumber(Alice.Basepoints.PaymentBasepoint, Bob.Basepoints.PaymentBasepoint,
                                             new Sha256());

        Alice.Channel = CreateChannel(Alice, Bob, aliceParty, bobParty, true, fundingTxId, obscuring, hasAnchors);
        Bob.Channel = CreateChannel(Bob, Alice, bobParty, aliceParty, false, fundingTxId, obscuring, hasAnchors);
        Alice.Signer.RegisterChannel(ChannelId, Alice.Channel.GetSigningInfo());
        Bob.Signer.RegisterChannel(ChannelId, Bob.Channel.GetSigningInfo());

        var aliceMsat = (FundingSatoshis - AlicePushedSatoshis) * 1_000;
        var bobMsat = AlicePushedSatoshis * 1_000;
        Alice.State = ChannelCommitments.Create(ChannelId, CommitmentParams.FromChannel(Alice.Channel), aliceMsat,
                                                bobMsat, InitialFeeratePerKw, Bob.Point(0), Bob.Point(1));
        Bob.State = ChannelCommitments.Create(ChannelId, CommitmentParams.FromChannel(Bob.Channel), bobMsat,
                                              aliceMsat, InitialFeeratePerKw, Alice.Point(0), Alice.Point(1));
    }

    public RealSigningNode PeerOf(RealSigningNode node) => ReferenceEquals(node, Alice) ? Bob : Alice;

    /// <summary><paramref name="from"/> offers an HTLC with payment hash SHA256(preimage) and the peer accepts it.</summary>
    public ulong Add(RealSigningNode from, ulong amountMsat, Secret preimage, uint cltvExpiry = 600)
    {
        var to = PeerOf(from);
        var result = from.State.SendAdd(amountMsat, Hash(preimage), cltvExpiry, s_onion);
        from.Apply("add", result);
        var add = Assert.IsType<OutboundAddHtlc>(Assert.Single(result.Outbound)).Htlc;
        to.Apply("receive add",
                 to.State.ReceiveAdd(add.Id, add.AmountMsat, add.PaymentHash, add.CltvExpiry, add.OnionRoutingPacket));
        return add.Id;
    }

    /// <summary><paramref name="from"/> fulfills the peer's HTLC <paramref name="id"/>.</summary>
    public void Fulfill(RealSigningNode from, ulong id, Secret preimage)
    {
        var to = PeerOf(from);
        from.Apply("fulfill", from.State.SendFulfill(id, preimage, new Sha256()));
        to.Apply("receive fulfill", to.State.ReceiveFulfill(id, preimage, new Sha256()));
    }

    /// <summary><paramref name="from"/> fails the peer's HTLC <paramref name="id"/>.</summary>
    public void Fail(RealSigningNode from, ulong id)
    {
        var to = PeerOf(from);
        var reason = new byte[292];
        from.Apply("fail", from.State.SendFail(id, reason));
        to.Apply("receive fail", to.State.ReceiveFail(id, reason));
    }

    /// <summary>The funder (Alice) changes the feerate.</summary>
    public void UpdateFee(uint feeratePerKw)
    {
        Alice.Apply("fee", Alice.State.SendFee(feeratePerKw));
        Bob.Apply("receive fee", Bob.State.ReceiveFee(feeratePerKw, 253, 100_000));
    }

    /// <summary>
    /// <paramref name="from"/> sends <c>commitment_signed</c>; the peer verifies it and answers <c>revoke_and_ack</c>
    /// with the real secret of its previous commitment.
    /// </summary>
    public void Commit(RealSigningNode from)
    {
        var to = PeerOf(from);

        // commitment_signed
        var sent = from.State.SendCommit(from.CommitmentSigner);
        from.Apply("commit", sent);
        var commitmentSigned = Assert.IsType<OutboundCommitmentSigned>(Assert.Single(sent.Outbound));
        var signedCommit = from.State.RemoteNextCommit!.Commit;
        var signedTxId = from.SigningService
                             .SignRemoteCommitment(from.Channel, CommitmentTxSpec.FromCommitmentSpec(signedCommit.Spec),
                                                   signedCommit.Number, signedCommit.PerCommitmentPoint)
                             .CommitmentTxId;

        var received = to.State.ReceiveCommit(commitmentSigned.Signatures, to.CommitmentVerifier);
        to.Apply("receive commit", received);
        var verifiedTxId = to.SigningService
                             .VerifyLocalCommitment(to.Channel, CommitmentTxSpec.FromCommitmentSpec(to.State.LocalCommit.Spec),
                                                    to.State.LocalCommit.Number, commitmentSigned.Signatures.Signature,
                                                    commitmentSigned.Signatures.HtlcSignatures)
                             .CommitmentTxId;
        Commitments.Add((from.Name, commitmentSigned.RemoteCommitmentNumber, signedTxId, verifiedTxId));

        // revoke_and_ack, after the new local commitment is "persisted" (the signer's guard moves only then)
        var revoke = Assert.IsType<OutboundRevokeAndAck>(Assert.Single(received.Outbound));
        to.Signer.AdvanceLocalCommitment(ChannelId, to.State.LocalCommit.Number);
        var secret = to.Signer.RevealPerCommitmentSecret(ChannelId, revoke.RevokedCommitmentNumber);
        var nextPoint = to.Signer.GetPerCommitmentPoint(ChannelId, revoke.NextCommitmentNumber);
        from.Apply("receive revoke", from.State.ReceiveRevoke(secret, nextPoint, from.RevocationVerifier));
    }

    /// <summary>
    /// Runs the commitment dance until neither side has anything left to sign, starting with <paramref name="first"/>.
    /// </summary>
    public void Settle(RealSigningNode first)
    {
        var next = first;
        for (var i = 0; i < 8 && (Alice.State.HasPendingChangesForRemote || Bob.State.HasPendingChangesForRemote); i++)
        {
            if (next.State.CanSendCommit)
                Commit(next);
            next = PeerOf(next);
        }

        Assert.False(Alice.State.HasPendingChangesForRemote || Bob.State.HasPendingChangesForRemote,
                     "The commitment dance did not converge");
    }

    public static Secret Preimage(byte tag) => new(Enumerable.Repeat(tag, 32).ToArray());

    public static Hash Hash(Secret preimage)
    {
        using var sha256 = new Sha256();
        var hash = new byte[32];
        sha256.AppendData(preimage);
        sha256.GetHashAndReset(hash);
        return new Hash(hash);
    }

    public void Dispose()
    {
        Alice.Dispose();
        Bob.Dispose();
    }

    private static ChannelModel CreateChannel(RealSigningNode self, RealSigningNode peer, ChannelParty local,
                                              ChannelParty remote, bool isInitiator, TxId fundingTxId,
                                              CommitmentNumber obscuring, bool hasAnchors)
    {
        var channelParams = new ChannelParams(local, remote, LightningMoney.Satoshis(InitialFeeratePerKw), 3,
                                              hasAnchors, FeatureSupport.No);
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(FundingSatoshis),
                                                  self.Basepoints.FundingPubKey, peer.Basepoints.FundingPubKey,
                                                  fundingTxId, 0);
        var localKeySet = new ChannelKeySetModel(self.KeyIndex, self.Basepoints.FundingPubKey,
                                                 self.Basepoints.RevocationBasepoint,
                                                 self.Basepoints.PaymentBasepoint,
                                                 self.Basepoints.DelayedPaymentBasepoint,
                                                 self.Basepoints.HtlcBasepoint, self.Point(0));
        var remoteKeySet = new ChannelKeySetModel(0, peer.Basepoints.FundingPubKey, peer.Basepoints.RevocationBasepoint,
                                                  peer.Basepoints.PaymentBasepoint,
                                                  peer.Basepoints.DelayedPaymentBasepoint,
                                                  peer.Basepoints.HtlcBasepoint, peer.Point(0));
        var localSat = isInitiator ? FundingSatoshis - AlicePushedSatoshis : AlicePushedSatoshis;
        return new ChannelModel(channelParams, ChannelId, obscuring, fundingOutput, isInitiator, null, null,
                                LightningMoney.Satoshis(localSat), localKeySet, 0, 0,
                                LightningMoney.Satoshis(FundingSatoshis - localSat), remoteKeySet, 0,
                                peer.Basepoints.FundingPubKey, 0, ChannelState.Open, ChannelVersion.V1);
    }
}