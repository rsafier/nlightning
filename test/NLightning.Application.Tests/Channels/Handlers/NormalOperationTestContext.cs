using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Channels.Handlers;

using Application.Channels.Services;
using Application.Protocol.Factories;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Models;
using Domain.Serialization.Interfaces;
using Infrastructure.Crypto.Hashes;

/// <summary>
/// The collaborators of the BOLT 2 normal-operation handlers (N6-T1) with the pure commitment engine and fake crypto
/// ports: an Open channel with a snapshot, mocked persistence that records the order of every call, a mocked signer and
/// shachain, and a real <see cref="MessageFactory"/>.
/// </summary>
internal sealed class NormalOperationTestContext
{
    public const ulong FundingSatoshis = 1_000_000;
    public const ulong PushSatoshis = 200_000;
    public const uint Feerate = 2_500;

    public static readonly ChannelId TestChannelId = new(Enumerable.Repeat((byte)0x3C, 32).ToArray());
    public static readonly CompactPubKey PeerNodeId = Point(0x0A);
    public static readonly byte[] Onion = new byte[1366];

    public ChannelModel Channel { get; }
    public NodeOptions NodeOptions { get; } = new() { EnableHtlcs = true };
    public Mock<IChannelMemoryRepository> ChannelMemoryRepository { get; } = new();
    public Mock<IUnitOfWork> UnitOfWork { get; } = new();
    public Mock<IChannelStateDbRepository> ChannelStateDbRepository { get; } = new();
    public Mock<IChannelDbRepository> ChannelDbRepository { get; } = new();
    public Mock<IRemoteShachainDbRepository> RemoteShachainDbRepository { get; } = new();
    public Mock<ILightningSigner> LightningSigner { get; } = new();
    public Mock<ISecretStorageService> Shachain { get; } = new();
    public Mock<IMessageSerializer> MessageSerializer { get; } = new();
    public FakeCommitmentPorts Ports { get; } = new();
    public ChannelDomainEventQueue Events { get; } = new();
    public MessageFactory MessageFactory { get; }

    /// <summary>Every persistence and signer call, in order ("apply", "save", "advance", "reveal", ...).</summary>
    public List<string> Calls { get; } = [];

    /// <summary>What each <c>ApplyAsync</c> was given.</summary>
    public List<(ChannelCommitments Next, ChannelTransition Transition, ChannelStateExtras? Extras)> Applied { get; } =
        [];

    public NormalOperationTestContext(bool localIsFunder = true, ChannelState state = ChannelState.Open)
    {
        Channel = CreateChannel(localIsFunder, state);
        var localMsat = (localIsFunder ? FundingSatoshis - PushSatoshis : PushSatoshis) * 1_000;
        var remoteMsat = FundingSatoshis * 1_000 - localMsat;
        Channel.UpdateCommitments(ChannelCommitments.Create(TestChannelId, CommitmentParams.FromChannel(Channel),
                                                            localMsat, remoteMsat, Feerate, Point(0x20),
                                                            Point(0x21), new CommitmentSignatures(Signature(1), [])));

        MessageFactory = new MessageFactory(Options.Create(NodeOptions));

        ChannelMemoryRepository
           .Setup(r => r.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel>.IsAny!))
           .Returns(new TryGetChannelDelegate((ChannelId id, out ChannelModel channel) =>
            {
                channel = id == Channel.ChannelId ? Channel : null!;
                return channel is not null;
            }));

        UnitOfWork.SetupGet(u => u.ChannelStateDbRepository).Returns(ChannelStateDbRepository.Object);
        UnitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(ChannelDbRepository.Object);
        UnitOfWork.SetupGet(u => u.RemoteShachainDbRepository).Returns(RemoteShachainDbRepository.Object);
        UnitOfWork.Setup(u => u.SaveChangesAsync()).Callback(() => Calls.Add("save")).Returns(Task.CompletedTask);
        ChannelStateDbRepository
           .Setup(r => r.ApplyAsync(It.IsAny<ChannelCommitments>(), It.IsAny<ChannelTransition>(),
                                    It.IsAny<ChannelStateExtras?>()))
           .Callback((ChannelCommitments next, ChannelTransition transition, ChannelStateExtras? extras) =>
            {
                Calls.Add("apply");
                Applied.Add((next, transition, extras));
            })
           .Returns(Task.CompletedTask);
        RemoteShachainDbRepository.Setup(r => r.GetByChannelIdAsync(It.IsAny<ChannelId>()))
                                  .ReturnsAsync(Array.Empty<ShachainEntry>());

        LightningSigner.Setup(s => s.AdvanceLocalCommitment(It.IsAny<ChannelId>(), It.IsAny<ulong>()))
                       .Callback((ChannelId _, ulong n) => Calls.Add($"advance {n}"));
        LightningSigner.Setup(s => s.RevealPerCommitmentSecret(It.IsAny<ChannelId>(), It.IsAny<ulong>()))
                       .Callback((ChannelId _, ulong n) => Calls.Add($"reveal {n}"))
                       .Returns((ChannelId _, ulong n) => SecretOf((byte)n));
        LightningSigner.Setup(s => s.GetPerCommitmentPoint(It.IsAny<ChannelId>(), It.IsAny<ulong>()))
                       .Returns((ChannelId _, ulong n) => Point((byte)(0x40 + n)));

        Shachain.Setup(s => s.InsertSecret(It.IsAny<Secret>(), It.IsAny<ulong>())).Returns(true);
        Shachain.Setup(s => s.Export()).Returns([new ShachainEntry(0, ulong.MaxValue >> 16, SecretOf(0x77))]);

        MessageSerializer.Setup(s => s.SerializeAsync(It.IsAny<IMessage>(), It.IsAny<Stream>()))
                         .Callback((IMessage message, Stream stream) =>
                          {
                              stream.WriteByte((byte)((ushort)message.Type >> 8));
                              stream.WriteByte((byte)message.Type);
                          })
                         .Returns(Task.CompletedTask);
    }

    private delegate bool TryGetChannelDelegate(ChannelId channelId, out ChannelModel channel);

    public ChannelStateTransitionService CreateTransitions()
    {
        var secretStorageFactory = new Mock<ISecretStorageServiceFactory>();
        secretStorageFactory.Setup(f => f.CreatePerCommitmentStorage()).Returns(Shachain.Object);

        return new ChannelStateTransitionService(ChannelMemoryRepository.Object, Events, Ports, LightningSigner.Object,
                                                 NullLogger<ChannelStateTransitionService>.Instance, MessageFactory,
                                                 MessageSerializer.Object, Options.Create(NodeOptions),
                                                 secretStorageFactory.Object, UnitOfWork.Object);
    }

    /// <summary>Replaces the channel's snapshot, as a persisted transition would.</summary>
    public void SetState(ChannelCommitments commitments) => Channel.UpdateCommitments(commitments);

    public ChannelCommitments State => Channel.Commitments!;

    /// <summary>Makes every save fail from now on.</summary>
    public void FailSaves() =>
        UnitOfWork.Setup(u => u.SaveChangesAsync()).Callback(() => Calls.Add("save failed"))
                  .ThrowsAsync(new InvalidOperationException("database is down"));

    public static Secret SecretOf(byte tag) => new(Enumerable.Repeat(tag, 32).ToArray());

    public static Hash HashOf(Secret preimage)
    {
        using var sha256 = new Sha256();
        var hash = new byte[32];
        sha256.AppendData(preimage);
        sha256.GetHashAndReset(hash);
        return new Hash(hash);
    }

    public static CompactPubKey Point(byte tag)
    {
        var bytes = new byte[33];
        bytes[0] = 0x02;
        bytes[32] = tag;
        return bytes;
    }

    public static CompactSignature Signature(byte tag) => new(Enumerable.Repeat(tag, 64).ToArray());

    /// <summary>
    /// Drives the local engine through an offered or received HTLC until it is locked in on both commitments, with
    /// the fake ports (no persistence, no messages).
    /// </summary>
    public HtlcRecord LockIn(HtlcDirection direction, ulong amountMsat, Secret preimage)
    {
        var state = State;
        if (direction == HtlcDirection.Outgoing)
        {
            state = state.SendAdd(amountMsat, HashOf(preimage), 600, Onion).Next;
            state = state.SendCommit(Ports).Next;
            state = state.ReceiveRevoke(SecretOf(0x90), Point(0x22), Ports).Next;
            state = state.ReceiveCommit(Ports.SignaturesFor(state), Ports).Next;
        }
        else
        {
            state = state.ReceiveAdd(state.RemoteNextHtlcId, amountMsat, HashOf(preimage), 600, Onion).Next;
            state = state.ReceiveCommit(Ports.SignaturesFor(state), Ports).Next;
            state = state.SendCommit(Ports).Next;
            state = state.ReceiveRevoke(SecretOf(0x90), Point(0x22), Ports).Next;
        }

        SetState(state);
        var id = direction == HtlcDirection.Outgoing ? state.LocalNextHtlcId - 1 : state.RemoteNextHtlcId - 1;
        return state.GetHtlc(direction, id)!;
    }

    private static ChannelModel CreateChannel(bool localIsFunder, ChannelState state)
    {
        var party = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(10_000),
                                     LightningMoney.MilliSatoshis(1_000), 30, LightningMoney.Satoshis(FundingSatoshis),
                                     144);
        var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(Feerate), 3, false,
                                              FeatureSupport.No);
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(FundingSatoshis), Point(0x01), Point(0x02))
        {
            TransactionId = new TxId(Enumerable.Repeat((byte)0x77, 32).ToArray()),
            Index = 0
        };
        var keySet = new ChannelKeySetModel(0, Point(0x01), Point(0x03), Point(0x04), Point(0x05), Point(0x06),
                                            Point(0x07));
        var remoteKeySet = ChannelKeySetModel.CreateForRemote(Point(0x02), Point(0x13), Point(0x14), Point(0x15),
                                                              Point(0x16), Point(0x20));
        var localSat = localIsFunder ? FundingSatoshis - PushSatoshis : PushSatoshis;
        return new ChannelModel(channelParams, TestChannelId, new CommitmentNumber(Point(0x04), Point(0x14),
                                                                               new FakeSha256()),
                                fundingOutput, localIsFunder, null, null, LightningMoney.Satoshis(localSat), keySet, 0,
                                0, LightningMoney.Satoshis(FundingSatoshis - localSat), remoteKeySet, 0, PeerNodeId, 0,
                                state, ChannelVersion.V1);
    }
}

/// <summary>
/// Engine crypto ports that accept everything unless told otherwise, and produce one fake HTLC signature per untrimmed
/// HTLC.
/// </summary>
internal sealed class FakeCommitmentPorts : ICommitmentSigner, ICommitmentVerifier, IRevocationVerifier
{
    public bool CommitmentSignaturesValid { get; set; } = true;
    public bool SecretsValid { get; set; } = true;

    public CommitmentSignatures SignRemoteCommitment(ChannelId channelId, ulong number, CommitmentSpec spec,
                                                     CompactPubKey remotePerCommitmentPoint) =>
        new(NormalOperationTestContext.Signature(0x51),
            Enumerable.Repeat(NormalOperationTestContext.Signature(0x52),
                              CommitmentFeeCalculator.UntrimmedHtlcCount(spec, 546, false)).ToList());

    public bool VerifyLocalCommitment(ChannelId channelId, ulong number, CommitmentSpec spec,
                                      CommitmentSignatures signatures) => CommitmentSignaturesValid;

    public bool IsValidSecret(Secret perCommitmentSecret, CompactPubKey expectedPerCommitmentPoint) => SecretsValid;

    /// <summary>
    /// A <c>commitment_signed</c> the peer could send for our next commitment after <paramref name="state"/>'s pending
    /// changes: one fake HTLC signature per untrimmed HTLC (found by trying each count).
    /// </summary>
    public CommitmentSignatures SignaturesFor(ChannelCommitments state)
    {
        for (var count = 0; count <= state.Htlcs.Count; count++)
        {
            var signatures = new CommitmentSignatures(NormalOperationTestContext.Signature(0x61),
                                                      Enumerable.Repeat(NormalOperationTestContext.Signature(0x62),
                                                                        count).ToList());
            try
            {
                state.ReceiveCommit(signatures, new AcceptingVerifier());
                return signatures;
            }
            catch (CommitmentViolationException e) when (e.RequirementId == "B2-CS-R02")
            {
                // Try the next count
            }
        }

        throw new InvalidOperationException("No HTLC signature count is accepted");
    }

    private sealed class AcceptingVerifier : ICommitmentVerifier
    {
        public bool VerifyLocalCommitment(ChannelId channelId, ulong number, CommitmentSpec spec,
                                          CommitmentSignatures signatures) => true;
    }
}