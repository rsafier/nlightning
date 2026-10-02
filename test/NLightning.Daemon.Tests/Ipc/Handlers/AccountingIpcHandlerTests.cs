using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Daemon.Extensions;
using Daemon.Ipc.Interfaces;
using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Persistence.Interfaces;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// <c>listaccountingevents</c> (ClientCommand 41) and <c>accountingsnapshot</c> (42) over IPC, NL-602: every field
/// crosses the envelope, filters reach the repository, bad kinds and channels are <c>invalid_operation</c>, an older
/// client's empty request keeps the defaults, and the registration is idempotent.
/// </summary>
public class AccountingIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 12, 30, 15, 123, TimeSpan.Zero);
    private static readonly ChannelId s_channel = new(Enumerable.Repeat((byte)0x33, 32).ToArray());
    private static readonly Hash s_hash = new(Enumerable.Repeat((byte)0x11, 32).ToArray());
    private static readonly TxId s_txId = new(Enumerable.Repeat((byte)0x22, 32).ToArray());

    private static readonly CompactPubKey s_peer =
        new(Convert.FromHexString("02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619"));

    private readonly Mock<IAccountingEventDbRepository> _repository = new();
    private readonly Mock<IAccountingEventSealer> _sealer = new();
    private readonly Mock<INodeSnapshotSource> _snapshot = new();
    private AccountingEventQuery? _query;

    public AccountingIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
        _repository.Setup(r => r.ListAsync(It.IsAny<AccountingEventQuery>(), It.IsAny<CancellationToken>()))
                   .Callback((AccountingEventQuery q, CancellationToken _) => _query = q)
                   .ReturnsAsync([CreateEvent()]);
        _repository.Setup(r => r.GetChainTipAsync(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new AccountingChainTip(42, new byte[32]));
        _sealer.Setup(s => s.SealNowAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(new AccountingSealRoundResult(0, 0, AccountingChainTip.Genesis));
    }

    [Fact]
    public async Task Given_AnEvent_When_Listed_Then_EveryFieldCrossesTheEnvelope()
    {
        // Arrange
        var handler = GetHandler(ClientCommand.ListAccountingEvents);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.ListAccountingEvents,
                                                                new ListAccountingEventsIpcRequest
                                                                {
                                                                    AfterLedgerSeq = 41,
                                                                    Limit = 1,
                                                                    Kinds = [(int)AccountingEventKind.InvoiceSettled],
                                                                    Channel = s_channel.ToString(),
                                                                    SinceUnixSeconds = 1_000,
                                                                    UntilUnixSeconds = 2_000
                                                                }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var payload = MessagePackSerializer.Deserialize<ListAccountingEventsIpcResponse>(
            response.Payload, s_options, TestContext.Current.CancellationToken);
        var accountingEvent = Assert.Single(payload.Events);
        Assert.Equal(42, accountingEvent.LedgerSeq);
        Assert.Equal($"inv:{s_hash}:settled", accountingEvent.EventKey);
        Assert.Equal((int)AccountingEventKind.InvoiceSettled, accountingEvent.Kind);
        Assert.Equal("InvoiceSettled", accountingEvent.KindName);
        Assert.Equal(s_at.ToUnixTimeMilliseconds(), accountingEvent.OccurredAtUnixMilliseconds);
        Assert.Equal(812_345u, accountingEvent.BlockHeight);
        Assert.Equal(-123_456, accountingEvent.AmountMsat);
        Assert.Equal(42, accountingEvent.FeeMsat);
        Assert.Equal(s_channel.ToString(), accountingEvent.ChannelId);
        Assert.Equal("812000x12x1", accountingEvent.ShortChannelId);
        Assert.Equal(s_hash.ToString(), accountingEvent.PaymentHash);
        Assert.Equal(s_txId.ToString(), accountingEvent.TxId);
        Assert.Equal(3u, accountingEvent.OutputIndex);
        Assert.Equal(s_peer.ToString(), accountingEvent.Counterparty);
        Assert.Equal((byte)AccountingFinality.Confirmed, accountingEvent.Finality);
        Assert.Equal("Confirmed", accountingEvent.FinalityName);
        Assert.Equal((int)AccountingEventFlags.Backfilled, accountingEvent.Flags);
        Assert.Equal("coffee", accountingEvent.Details["description"]);
        Assert.Equal(new string('a', 64), accountingEvent.Hash);
        Assert.Equal(42, payload.NextAfter);
        Assert.True(payload.HasMore);
        Assert.Equal(42, payload.ChainTipLedgerSeq);
        Assert.NotNull(_query);
        Assert.Equal(41, _query.AfterLedgerSeq);
        Assert.Equal(1, _query.Take);
        Assert.Equal([AccountingEventKind.InvoiceSettled], _query.Kinds!);
        Assert.Equal(s_channel, _query.ChannelId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_000), _query.Since);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2_000), _query.Until);
        _sealer.Verify(s => s.SealNowAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_AnUnknownKind_When_Listed_Then_InvalidOperation()
    {
        // Arrange
        var handler = GetHandler(ClientCommand.ListAccountingEvents);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.ListAccountingEvents,
                                                                new ListAccountingEventsIpcRequest { Kinds = [999] }),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, ReadError(response).Code);
        _repository.Verify(r => r.ListAsync(It.IsAny<AccountingEventQuery>(), It.IsAny<CancellationToken>()),
                           Times.Never);
    }

    [Fact]
    public async Task Given_AChannelThatIsNeitherForm_When_Listed_Then_InvalidOperation()
    {
        // Arrange
        var handler = GetHandler(ClientCommand.ListAccountingEvents);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.ListAccountingEvents,
                                                                new ListAccountingEventsIpcRequest
                                                                {
                                                                    Channel = "not-a-channel"
                                                                }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, ReadError(response).Code);
    }

    [Fact]
    public void Given_AnOlderClientsEmptyRequest_When_Read_Then_TheDefaultsApply()
    {
        // Arrange
        var bytes = MessagePackSerializer.Serialize(new AccountingSnapshotIpcRequest(), s_options,
                                                    TestContext.Current.CancellationToken);

        // Act
        var request = MessagePackSerializer.Deserialize<ListAccountingEventsIpcRequest>(
                          bytes, s_options, TestContext.Current.CancellationToken)
                     .ToClientRequest();

        // Assert
        Assert.Equal(0, request.AfterLedgerSeq);
        Assert.Equal(100, request.Take);
        Assert.Null(request.Kinds);
        Assert.Null(request.ChannelId);
        Assert.Null(request.Since);
        Assert.Null(request.Until);
    }

    [Fact]
    public void Given_AShortChannelIdText_When_Mapped_Then_ItIsTheScidFilter()
    {
        // Act
        var request = new ListAccountingEventsIpcRequest { Channel = "812000x12x1" }.ToClientRequest();

        // Assert
        Assert.Equal(new ShortChannelId(812_000, 12, 1), request.ChannelScid);
        Assert.Null(request.ChannelId);
    }

    [Fact]
    public async Task Given_ASnapshot_When_Asked_Then_EveryBucketCrossesTheEnvelope()
    {
        // Arrange
        _snapshot.Setup(s => s.TakeSnapshotAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AccountingSnapshot(
                                   s_at, 812_345,
                                   [
                                       new ChannelBalanceBucket(s_channel, new ShortChannelId(812_000, 12, 1),
                                                                ChannelState.Open, s_peer, 1_000_000_000, 600_000_000,
                                                                400_000_000, 10_000_000, 5_000_000, 0, 0, 0, true),
                                       new ChannelBalanceBucket(new ChannelId(new byte[32]), null,
                                                                ChannelState.OnchainResolving, null, 0, 0, 0, 0, 0,
                                                                20_000_000, 7_000_000, 2, false)
                                   ],
                                   new WalletBalanceBucket(2_000_000, 300_000, 100_000)));
        var handler = GetHandler(ClientCommand.AccountingSnapshot);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.AccountingSnapshot,
                                                                new AccountingSnapshotIpcRequest()),
                                                 TestContext.Current.CancellationToken);

        // Assert
        var payload = MessagePackSerializer.Deserialize<AccountingSnapshotIpcResponse>(
            response.Payload, s_options, TestContext.Current.CancellationToken);
        Assert.Equal(s_at.ToUnixTimeMilliseconds(), payload.TakenAtUnixMilliseconds);
        Assert.Equal(812_345u, payload.BlockHeight);
        Assert.Equal(2_000_000, payload.WalletConfirmedMsat);
        Assert.Equal(300_000, payload.WalletUnconfirmedMsat);
        Assert.Equal(100_000, payload.WalletLockedMsat);
        Assert.Equal(600_000_000, payload.ChannelLocalMsat);
        Assert.Equal(20_000_000, payload.PendingOnchainMsat);
        Assert.Equal(7_000_000, payload.PendingHtlcOnchainMsat);
        Assert.Equal(2, payload.PendingSweepCount);
        Assert.Equal(600_000_000 + 20_000_000 + 7_000_000 + 2_000_000 + 300_000, payload.TotalMsat);
        var open = payload.Channels[0];
        Assert.Equal(s_channel.ToString(), open.ChannelId);
        Assert.Equal("812000x12x1", open.ShortChannelId);
        Assert.Equal((byte)ChannelState.Open, open.State);
        Assert.Equal("Open", open.StateName);
        Assert.Equal(s_peer.ToString(), open.Counterparty);
        Assert.Equal(1_000_000_000, open.CapacityMsat);
        Assert.Equal(600_000_000, open.LocalBalanceMsat);
        Assert.Equal(400_000_000, open.RemoteBalanceMsat);
        Assert.Equal(10_000_000, open.LocalInFlightMsat);
        Assert.Equal(5_000_000, open.RemoteInFlightMsat);
        Assert.True(open.IsLoaded);
        var gone = payload.Channels[1];
        Assert.Null(gone.ShortChannelId);
        Assert.Null(gone.Counterparty);
        Assert.False(gone.IsLoaded);
        Assert.Equal(20_000_000, gone.PendingOnchainMsat);
        Assert.Equal(7_000_000, gone.PendingHtlcOnchainMsat);
        Assert.Equal(2, gone.PendingSweepCount);
    }

    [Fact]
    public void Given_TheRegistrationCalledTwice_When_Composed_Then_OneHandlerPerCommand()
    {
        // Arrange
        var services = BuildServices();
        services.AddAccountingIpcServices();

        // Act
        using var provider = services.BuildServiceProvider();
        var commands = provider.GetServices<IIpcCommandHandler>().Select(h => h.Command).ToList();

        // Assert
        Assert.Single(commands, c => c == ClientCommand.ListAccountingEvents);
        Assert.Single(commands, c => c == ClientCommand.AccountingSnapshot);
    }

    [Fact]
    public void Given_TheCommands_When_Read_Then_TheWireValuesAre41And42()
    {
        // Assert (append-only, never renumber)
        Assert.Equal(41, (int)ClientCommand.ListAccountingEvents);
        Assert.Equal(42, (int)ClientCommand.AccountingSnapshot);
    }

    private IIpcCommandHandler GetHandler(ClientCommand command) =>
        BuildServices().BuildServiceProvider().GetServices<IIpcCommandHandler>().Single(h => h.Command == command);

    private ServiceCollection BuildServices()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.AccountingEventDbRepository).Returns(_repository.Object);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => unitOfWork.Object);
        services.AddSingleton(_sealer.Object);
        services.AddSingleton(_snapshot.Object);
        services.AddAccountingIpcServices();
        return services;
    }

    private static AccountingEventModel CreateEvent() => new()
    {
        EventKey = $"inv:{s_hash}:settled",
        Kind = AccountingEventKind.InvoiceSettled,
        OccurredAt = s_at,
        BlockHeight = 812_345,
        ChannelId = s_channel,
        ShortChannelId = new ShortChannelId(812_000, 12, 1),
        PaymentHash = s_hash,
        TxId = s_txId,
        OutputIndex = 3,
        Counterparty = s_peer,
        AmountMsat = -123_456,
        FeeMsat = 42,
        Finality = AccountingFinality.Confirmed,
        Flags = AccountingEventFlags.Backfilled,
        Details = AccountingDetailsCodec.Create(("description", "coffee")),
        LedgerSeq = 42,
        Hash = Enumerable.Repeat((byte)0xAA, 32).ToArray()
    };

    private static IpcError ReadError(IpcEnvelope response)
    {
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        return MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                           TestContext.Current.CancellationToken);
    }

    private static IpcEnvelope CreateEnvelope<T>(ClientCommand command, T request) => new()
    {
        Version = 1,
        Command = command,
        CorrelationId = Guid.NewGuid(),
        Kind = IpcEnvelopeKind.Request,
        Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
    };
}