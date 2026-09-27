using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Application.Tests.InteractiveTx.TestDoubles;

using Application.InteractiveTx;
using Application.InteractiveTx.Interfaces;
using Application.InteractiveTx.Models;
using Domain.Channels.Quiescence;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Models;
using Domain.Protocol.Interfaces;

/// <summary>
/// The engines the harness runs on: the reference test engine always, and lane IT-A's
/// <see cref="DomainInteractiveTxEngine"/> as soon as <see cref="InteractiveTxSession"/> is implemented (so the same
/// scenarios re-run on the real engine after the wave's merge without a test change).
/// </summary>
public static class InteractiveTxEngines
{
    public const string Reference = "reference";
    public const string Domain = "domain";

    public static TheoryData<string> All
    {
        get
        {
            var data = new TheoryData<string> { Reference };
            if (IsDomainEngineImplemented())
                data.Add(Domain);
            return data;
        }
    }

    internal static IInteractiveTxEngine Get(string name) =>
        name == Domain ? new DomainInteractiveTxEngine() : new ReferenceInteractiveTxEngine();

    /// <summary>The message of the wave contract's <see cref="InteractiveTxSession"/> stub (lane IT-A replaces it).</summary>
    internal const string ContractStubMessage = "Interactive-tx engine: lane IT-A (IT1-T1..T4)";

    /// <summary>
    /// False only while <see cref="InteractiveTxSession"/> is still the wave contract's stub (its exact
    /// <see cref="NotImplementedException"/>). Anything else, including a <see cref="NotImplementedException"/> with
    /// another message (a partly implemented engine), adds the domain variant, so its failures show instead of the
    /// variant silently disappearing.
    /// </summary>
    internal static bool IsDomainEngineImplemented()
    {
        var key = new Key().PubKey.ToBytes();
        var parameters = new InteractiveTxSessionParameters(new ChannelId(new byte[32]), true, 253, 0,
                                                            InteractiveTxContribution.Empty, null, false, false, key,
                                                            key, []);
        try
        {
            InteractiveTxSession.Create(parameters).Start();
            return true;
        }
        catch (NotImplementedException e) when (e.Message == ContractStubMessage)
        {
            return false;
        }
        catch (Exception)
        {
            // Implemented (it may refuse this dummy negotiation), or partly: either way the scenarios must run on it
            return true;
        }
    }
}

/// <summary>One node of the harness: a production <see cref="InteractiveTxDriver"/> over fakes.</summary>
internal sealed class InteractiveTxTestNode
{
    private readonly IInteractiveTxEngine _engine;

    public required string Name { get; init; }
    public required CompactPubKey NodeId { get; init; }
    public required TestSharedFundingHost Host { get; set; }
    public FakeInteractiveTxContributor Contributor { get; } = new();
    public FakePrevTxInspector Inspector { get; } = new();
    public FakeInteractiveTxBuilder Builder { get; } = new();
    public InMemoryInteractiveTxSessionRepository Repository { get; } = new();
    public IUnitOfWork UnitOfWork { get; }
    public int Saves { get; private set; }
    public InteractiveTxDriver Driver { get; private set; }

    /// <summary>The next save throws (its staged writes are discarded), as a database failure would.</summary>
    public bool FailNextSave { get; set; }

    public IQuiescenceService? Quiescence { get; private set; }
    public TimeProvider? Clock { get; private set; }

    public InteractiveTxTestNode(IInteractiveTxEngine engine)
    {
        _engine = engine;
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.InteractiveTxSessionDbRepository).Returns(Repository);
        unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(() =>
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                Repository.DiscardStaged();
                throw new InvalidOperationException("Simulated save failure");
            }

            Repository.Commit();
            Saves++;
            return Task.CompletedTask;
        });
        UnitOfWork = unitOfWork.Object;
        Driver = CreateDriver();
    }

    public static InteractiveTxTestNode Create(string name, byte keyByte, IInteractiveTxEngine engine,
                                               long localOutputShareSat, long remoteOutputShareSat)
    {
        return new InteractiveTxTestNode(engine)
        {
            Name = name,
            NodeId = new Key(Enumerable.Repeat(keyByte, 32).ToArray()).PubKey.ToBytes(),
            Host = new TestSharedFundingHost
            {
                LocalOutputShare = LightningMoney.Satoshis(localOutputShareSat),
                RemoteOutputShare = LightningMoney.Satoshis(remoteOutputShareSat)
            }
        };
    }

    /// <summary>Replaces the driver (for example one built with other optional services).</summary>
    public void ReplaceDriver(InteractiveTxDriver driver) => Driver = driver;

    /// <summary>Rebuilds the driver with a quiescence service and/or a clock (the memory is lost).</summary>
    public void Configure(IQuiescenceService? quiescence = null, TimeProvider? clock = null)
    {
        Quiescence = quiescence;
        Clock = clock;
        Driver = CreateDriver();
    }

    /// <summary>A restart: a new driver (empty memory) over the same database and wallet.</summary>
    public void Restart() => Driver = CreateDriver();

    public WalletUtxo Fund(long satoshis)
    {
        var utxo = WalletUtxo.Create(satoshis);
        Contributor.Utxos.Add(utxo);
        return utxo;
    }

    public InteractiveTxTerms Terms(ChannelId channelId, InteractiveTxTestNode peer, bool isInitiator,
                                    uint feeratePerKw, bool contribute = true, long? walletAmountSat = null)
    {
        var walletAmount = walletAmountSat is { } sat ? LightningMoney.Satoshis(sat) : Host.LocalOutputShare;
        var request = contribute
                          ? new InteractiveTxContributionRequest(channelId, InteractiveTxPurpose.DualFund,
                                                                 walletAmount, [], feeratePerKw,
                                                                 isInitiator ? 600 : 0, false)
                          : null;
        return new InteractiveTxTerms(channelId, NodeId, peer.NodeId, isInitiator, feeratePerKw, 0,
                                      ContributionRequest: request);
    }

    public InteractiveTxSessionModel? StoredSession(ChannelId channelId) =>
        Repository.Committed.Values.Where(s => s.ChannelId == channelId).MaxBy(s => s.CreatedAt);

    private InteractiveTxDriver CreateDriver() =>
        new(_engine, Builder, Contributor, Inspector, NullLogger<InteractiveTxDriver>.Instance, Quiescence, Clock);
}

/// <summary>
/// Two <see cref="InteractiveTxTestNode"/>s joined by one FIFO: every message a driver returns is delivered to the
/// other driver in order (the host's commitment_signed through <c>OnCommitmentSignedReceivedAsync</c>).
/// </summary>
internal sealed class InteractiveTxHarness
{
    public static readonly ChannelId ChannelId = new(Enumerable.Repeat((byte)0x77, 32).ToArray());

    public InteractiveTxTestNode Alice { get; }
    public InteractiveTxTestNode Bob { get; }
    public List<(string From, IChannelMessage Message)> Transcript { get; } = [];

    public InteractiveTxHarness(string engine, long aliceShareSat, long bobShareSat)
        : this(InteractiveTxEngines.Get(engine), aliceShareSat, bobShareSat)
    {
    }

    public InteractiveTxHarness(IInteractiveTxEngine implementation, long aliceShareSat, long bobShareSat)
    {
        Alice = InteractiveTxTestNode.Create("alice", 0x11, implementation, aliceShareSat, bobShareSat);
        Bob = InteractiveTxTestNode.Create("bob", 0x22, implementation, bobShareSat, aliceShareSat);
    }

    public InteractiveTxTestNode Other(InteractiveTxTestNode node) => ReferenceEquals(node, Alice) ? Bob : Alice;

    /// <summary>Alice (initiator) and Bob start a negotiation at <paramref name="feeratePerKw"/>; nothing is pumped.</summary>
    public async Task<IReadOnlyList<IChannelMessage>> StartAsync(uint feeratePerKw, CancellationToken cancellationToken,
                                                                 bool aliceContributes = true,
                                                                 bool bobContributes = true)
    {
        await Bob.Driver.StartAsync(Bob.Terms(ChannelId, Alice, false, feeratePerKw, bobContributes), Bob.Host,
                                    cancellationToken);
        return await Alice.Driver.StartAsync(Alice.Terms(ChannelId, Bob, true, feeratePerKw, aliceContributes),
                                             Alice.Host, cancellationToken);
    }

    /// <summary>
    /// Delivers <paramref name="messages"/> from <paramref name="from"/> and every reply, until nothing is left or
    /// <paramref name="stopBefore"/> holds for the next message; returns what was not delivered.
    /// </summary>
    public Task<List<(InteractiveTxTestNode From, IChannelMessage Message)>> PumpAsync(
        InteractiveTxTestNode from, IReadOnlyList<IChannelMessage> messages, CancellationToken cancellationToken,
        Func<InteractiveTxTestNode, IChannelMessage, bool>? stopBefore = null, int maxMessages = 1_000) =>
        PumpAsync(messages.Select(m => (from, m)), cancellationToken, stopBefore, maxMessages);

    /// <summary>
    /// Delivers <paramref name="items"/> in order (one FIFO for both directions) and every reply, until nothing is
    /// left or <paramref name="stopBefore"/> holds for the next message; returns what was not delivered. A
    /// commitment_signed for a node that no longer negotiates is dropped (its host would ignore it). More than
    /// <paramref name="maxMessages"/> deliveries fail the test (two drivers bouncing messages forever).
    /// </summary>
    public async Task<List<(InteractiveTxTestNode From, IChannelMessage Message)>> PumpAsync(
        IEnumerable<(InteractiveTxTestNode From, IChannelMessage Message)> items, CancellationToken cancellationToken,
        Func<InteractiveTxTestNode, IChannelMessage, bool>? stopBefore = null, int maxMessages = 1_000)
    {
        var queue = new Queue<(InteractiveTxTestNode From, IChannelMessage Message)>(items);
        var delivered = 0;
        while (queue.TryPeek(out var next))
        {
            if (stopBefore?.Invoke(next.From, next.Message) == true)
                return [.. queue];
            if (++delivered > maxMessages)
                throw new InvalidOperationException(
                    $"More than {maxMessages} messages pumped; the last ones: "
                  + string.Join(", ", Transcript.TakeLast(6).Select(t => $"{t.From} {t.Message.Type}")));

            queue.Dequeue();
            Transcript.Add((next.From.Name, next.Message));
            var to = Other(next.From);
            IReadOnlyList<IChannelMessage> replies;
            if (next.Message is TestCommitmentSignedMessage)
                replies = to.Driver.IsNegotiating(ChannelId)
                              ? await to.Driver.OnCommitmentSignedReceivedAsync(ChannelId, to.UnitOfWork,
                                                                                cancellationToken)
                              : [];
            else
                replies = await to.Driver.ReceiveAsync(next.Message, next.From.NodeId, to.UnitOfWork,
                                                       cancellationToken);

            foreach (var reply in replies)
                queue.Enqueue((to, reply));
        }

        return [];
    }
}