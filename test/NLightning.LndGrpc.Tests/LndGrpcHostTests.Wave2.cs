using System.Security.Cryptography;
using Grpc.Core;
using Moq;
using NBitcoin;

namespace NLightning.LndGrpc.Tests;

using Domain.Accounting.Labels;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Models;
using Domain.Node.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Payments.Enums;
using Domain.Payments.Events;
using Domain.Payments.Models;
using Domain.Protocol.ValueObjects;
using Google.Protobuf;
using LndGrpc.Macaroons;
using Testing.Lnd;
using Testing.Lnd.Invoicesrpc;
using Testing.Lnd.Lnrpc;
using Testing.Lnd.Routerrpc;
using Invoice = Testing.Lnd.Lnrpc.Invoice;
using InvoiceHtlc = Domain.Payments.Models.InvoiceHtlc;
using InvoiceHtlcState = Domain.Payments.Models.InvoiceHtlcState;
using LndChannelPoint = Testing.Lnd.Lnrpc.ChannelPoint;
using MacaroonId = LndGrpc.Macaroons.MacaroonId;
using Payment = Testing.Lnd.Lnrpc.Payment;

/// <summary>
/// Wave 2 of the LND gRPC server (NL-1164 .. NL-1169) through our in-tree LND client: peers, opens, closes, policies,
/// the wallet, routerrpc payments, invoicesrpc hold invoices, invoice subscriptions, closed-channel resolutions,
/// invoice HTLCs and baked macaroons. The node commands run on <see cref="FakeDispatcher"/>.
/// </summary>
public sealed partial class LndGrpcHostTests
{
    private static CancellationToken Bounded
    {
        get
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            source.CancelAfter(TimeSpan.FromSeconds(20));
            return source.Token;
        }
    }

    [Fact]
    public async Task Given_APeer_When_ConnectPeer_Then_ItIsDialedAtTheDefaultPortAndAKnownOneAnswersAStatus()
    {
        // Arrange
        var peerId = CreatePubKey(9);
        PeerAddressInfo? dialed = null;
        _peers.Setup(x => x.DialPeerAsync(It.IsAny<PeerAddressInfo>(), It.IsAny<CancellationToken>()))
              .Callback((PeerAddressInfo address, CancellationToken _) => dialed = address)
              .ReturnsAsync(new PeerModel(peerId, "10.1.2.3", 9735, "IPv4"));
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var connected = await connection.LightningClient.ConnectPeerAsync(new ConnectPeerRequest
        {
            Addr = new LightningAddress { Pubkey = peerId.ToString(), Host = "10.1.2.3" }
        }, cancellationToken: Ct);
        _peers.Setup(x => x.GetPeer(peerId)).Returns(new PeerModel(peerId, "10.1.2.3", 9735, "IPv4"));
        var again = await connection.LightningClient.ConnectPeerAsync(new ConnectPeerRequest
        {
            Addr = new LightningAddress { Pubkey = peerId.ToString(), Host = "10.1.2.3:9735" }
        }, cancellationToken: Ct);

        // Assert
        Assert.Equal($"{peerId}@10.1.2.3:9735", dialed?.Address);
        Assert.Contains("successful", connected.Status);
        Assert.Contains("already connected", again.Status);
    }

    [Fact]
    public async Task Given_APeerWithAnOpenChannel_When_DisconnectPeer_Then_RefusedAsLnd()
    {
        // Arrange
        _channels.Add(CreateChannel(7, ChannelState.Open));
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var refused = await Assert.ThrowsAsync<RpcException>(
            () => connection.LightningClient.DisconnectPeerAsync(
                new DisconnectPeerRequest { PubKey = CreatePubKey(7).ToString() }, cancellationToken: Ct).ResponseAsync);
        await connection.LightningClient.DisconnectPeerAsync(
            new DisconnectPeerRequest { PubKey = CreatePubKey(8).ToString() }, cancellationToken: Ct);

        // Assert
        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Equal(CreatePubKey(8), Assert.IsType<DisconnectPeerClientRequest>(_dispatcher.Requests.Single()).NodeId);
    }

    [Fact]
    public async Task Given_AnOpenRequest_When_OpenChannelSync_Then_ItAnswersAtThePublishedFunding()
    {
        // Arrange
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x61, 32).ToArray());
        var fundingTxId = new TxId(Enumerable.Repeat((byte)0x62, 32).ToArray());
        _dispatcher.On<OpenChannelClientRequest, OpenChannelClientResponse>(_ => new OpenChannelClientResponse(channelId));
        _dispatcher.On<OpenChannelClientSubscriptionRequest, OpenChannelClientSubscriptionResponse>(
            _ => new OpenChannelClientSubscriptionResponse(channelId)
            {
                ChannelState = ChannelState.V1FundingSigned,
                TxId = fundingTxId,
                Index = 1
            });
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var point = await connection.LightningClient.OpenChannelSyncAsync(new OpenChannelRequest
        {
            NodePubkey = ByteString.CopyFrom((byte[])CreatePubKey(9)),
            LocalFundingAmount = 500_000,
            PushSat = 10_000,
            Private = true,
            SatPerVbyte = 4,
            Memo = "from lnd grpc"
        }, cancellationToken: Ct);
        var refused = await Assert.ThrowsAsync<RpcException>(
            () => connection.LightningClient.OpenChannelSyncAsync(new OpenChannelRequest
            {
                NodePubkey = ByteString.CopyFrom((byte[])CreatePubKey(9)),
                LocalFundingAmount = 500_000,
                CommitmentType = CommitmentType.StaticRemoteKey
            }, cancellationToken: Ct).ResponseAsync);

        // Assert
        Assert.Equal((byte[])fundingTxId, point.FundingTxidBytes.ToByteArray());
        Assert.Equal(1u, point.OutputIndex);
        var open = Assert.IsType<OpenChannelClientRequest>(_dispatcher.Requests[0]);
        Assert.Equal(CreatePubKey(9).ToString(), open.NodeInfo);
        Assert.Equal(LightningMoney.Satoshis(500_000), open.FundingAmount);
        Assert.Equal(LightningMoney.Satoshis(10_000), open.PushAmount);
        Assert.False(open.IsPublic);
        Assert.Equal(LightningMoney.Satoshis(1_000), open.FeeRatePerKw);
        Assert.Equal("from lnd grpc", open.Label);
        Assert.True(Assert.IsType<OpenChannelClientSubscriptionRequest>(_dispatcher.Requests[1]).ReportFundingChanges);
        Assert.Equal(StatusCode.Unimplemented, refused.StatusCode);
    }

    [Fact]
    public async Task Given_AnOpen_When_OpenChannelIsStreamed_Then_PendingThenOpen()
    {
        // Arrange
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x63, 32).ToArray());
        var fundingTxId = new TxId(Enumerable.Repeat((byte)0x64, 32).ToArray());
        var states = new Queue<ChannelState>([ChannelState.V1FundingSigned, ChannelState.ReadyForThem]);
        _dispatcher.On<OpenChannelClientRequest, OpenChannelClientResponse>(_ => new OpenChannelClientResponse(channelId));
        _dispatcher.On<OpenChannelClientSubscriptionRequest, OpenChannelClientSubscriptionResponse>(
            _ => new OpenChannelClientSubscriptionResponse(channelId)
            {
                ChannelState = states.Dequeue(),
                TxId = fundingTxId,
                Index = 0
            });
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        using var call = connection.LightningClient.OpenChannel(new OpenChannelRequest
        {
            NodePubkey = ByteString.CopyFrom((byte[])CreatePubKey(9)),
            LocalFundingAmount = 500_000
        }, cancellationToken: Bounded);
        var updates = await ReadAllAsync(call.ResponseStream);

        // Assert
        Assert.Equal(2, updates.Count);
        Assert.Equal((byte[])fundingTxId, updates[0].ChanPending.Txid.ToByteArray());
        Assert.Equal((byte[])fundingTxId, updates[1].ChanOpen.ChannelPoint.FundingTxidBytes.ToByteArray());
    }

    [Fact]
    public async Task Given_AnOpenChannel_When_ClosedCooperatively_Then_PendingThenCloseOnceConfirmed()
    {
        // Arrange
        var channel = CreateChannel(7, ChannelState.Open);
        _channels.Add(channel);
        var closingTxId = new TxId(Enumerable.Repeat((byte)0x71, 32).ToArray());
        var state = ChannelState.Closing;
        _channelMemory.Setup(x => x.TryGetChannelState(channel.ChannelId, out It.Ref<ChannelState>.IsAny))
                      .Returns(new TryGetChannelState((ChannelId _, out ChannelState current) =>
                      {
                          current = state;
                          return true;
                      }));
        _dispatcher.On<CloseChannelClientRequest, CloseChannelClientResponse>(
            _ => new CloseChannelClientResponse(channel.ChannelId, ChannelState.Closing, closingTxId));
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        using var call = connection.LightningClient.CloseChannel(new CloseChannelRequest
        {
            ChannelPoint = new LndChannelPoint
            {
                FundingTxidBytes = ByteString.CopyFrom((byte[])channel.FundingOutput!.TransactionId!.Value),
                OutputIndex = 0
            },
            SatPerVbyte = 2
        }, cancellationToken: Bounded);
        Assert.True(await call.ResponseStream.MoveNext(Bounded));
        var pending = call.ResponseStream.Current;
        state = ChannelState.Closed;
        var rest = await ReadAllAsync(call.ResponseStream);

        // Assert
        Assert.Equal((byte[])closingTxId, pending.ClosePending.Txid.ToByteArray());
        var closed = Assert.Single(rest);
        Assert.True(closed.ChanClose.Success);
        Assert.Equal((byte[])closingTxId, closed.ChanClose.ClosingTxid.ToByteArray());
        Assert.Equal(500u, Assert.IsType<CloseChannelClientRequest>(_dispatcher.Requests.Single()).FeeRatePerKw);
    }

    [Fact]
    public async Task Given_AGlobalPolicy_When_UpdateChannelPolicy_Then_EveryOpenChannelGetsItAndRefusalsAreReported()
    {
        // Arrange
        _channels.Add(CreateChannel(7, ChannelState.Open));
        _channels.Add(CreateChannel(8, ChannelState.Open));
        _channels.Add(CreateChannel(9, ChannelState.V1FundingSigned));
        _dispatcher.On<SetChannelPolicyClientRequest, ChannelPolicyClientResponse>(r =>
            r.Channel.ChannelId == _channels[1].ChannelId
                ? throw new ClientException(ErrorCodes.InvalidOperation, "refused")
                : null!);
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var response = await connection.LightningClient.UpdateChannelPolicyAsync(new PolicyUpdateRequest
        {
            Global = true,
            BaseFeeMsat = 1_500,
            FeeRate = 0.000_25,
            TimeLockDelta = 80,
            MaxHtlcMsat = 900_000_000
        }, cancellationToken: Ct);

        // Assert
        var updates = _dispatcher.Requests.Cast<SetChannelPolicyClientRequest>().ToList();
        Assert.Equal(2, updates.Count);
        Assert.All(updates, u =>
        {
            Assert.Equal(1_500u, u.FeeBaseMsat);
            Assert.Equal(250u, u.FeeProportionalMillionths);
            Assert.Equal((ushort)80, u.CltvExpiryDelta);
            Assert.Equal(900_000_000ul, u.HtlcMaximumMsat);
            Assert.Null(u.HtlcMinimumMsat);
        });
        var failed = Assert.Single(response.FailedUpdates);
        Assert.Equal(UpdateFailure.InvalidParameter, failed.Reason);
        Assert.Equal("refused", failed.UpdateError);
    }

    [Fact]
    public async Task Given_TheWallet_When_SendCoinsAndNewAddress_Then_WithdrawAndAFreshAddress()
    {
        // Arrange
        var txId = new TxId(Enumerable.Repeat((byte)0x81, 32).ToArray());
        _dispatcher.On<WithdrawClientRequest, WithdrawClientResponse>(
            _ => new WithdrawClientResponse(txId, 20_000, 300, 0, 1_000, 500, 1, 0, true));
        _wallet.Setup(x => x.GetUnusedAddressAsync(Domain.Bitcoin.Enums.AddressType.P2Tr, false))
               .ReturnsAsync(new WalletAddressModel(Domain.Bitcoin.Enums.AddressType.P2Tr, 4, false, "bcrt1ptaproot"));
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var sent = await connection.LightningClient.SendCoinsAsync(new SendCoinsRequest
        {
            Addr = "bcrt1qdest",
            Amount = 20_000,
            SatPerVbyte = 3,
            Label = "out"
        }, cancellationToken: Ct);
        var address = await connection.LightningClient.NewAddressAsync(
                          new NewAddressRequest { Type = Testing.Lnd.Lnrpc.AddressType.TaprootPubkey },
                          cancellationToken: Ct);
        var nested = await Assert.ThrowsAsync<RpcException>(
            () => connection.LightningClient.NewAddressAsync(
                new NewAddressRequest { Type = Testing.Lnd.Lnrpc.AddressType.NestedPubkeyHash },
                cancellationToken: Ct).ResponseAsync);

        // Assert
        Assert.Equal(txId.ToString(), sent.Txid);
        var withdraw = Assert.IsType<WithdrawClientRequest>(_dispatcher.Requests.Single());
        Assert.Equal("bcrt1qdest", withdraw.Address);
        Assert.Equal(20_000ul, withdraw.AmountSat);
        Assert.Equal(3ul, withdraw.SatPerVbyte);
        Assert.Equal("out", withdraw.Label);
        Assert.Equal("bcrt1ptaproot", address.Address);
        Assert.Equal(StatusCode.Unimplemented, nested.StatusCode);
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(0, 60)]
    public async Task Given_AnInvoice_When_SendPaymentV2_Then_InFlightThenSucceededAreStreamedAndTrackPaymentAgrees(int timeout, int expectedTimeout)
    {
        // Arrange
        var (bolt11, hash) = CreateBolt11(0x91, 12_000);
        var payment = new PaymentModel(hash, bolt11, CreatePubKey(9), LightningMoney.Satoshis(12_000),
                                       LightningMoney.MilliSatoshis(250), DateTimeOffset.UtcNow)
        {
            PaymentIndex = 7
        };
        var release = new TaskCompletionSource();
        PayInvoiceOptions? options = null;
        PaymentModel? stored = null;
        _paymentService.Setup(x => x.GetPaymentAsync(hash, It.IsAny<CancellationToken>()))
                       .ReturnsAsync(() => stored);
        _paymentService.Setup(x => x.PayInvoiceAsync(bolt11, null, It.IsAny<PayInvoiceOptions>(),
                                                     It.IsAny<CancellationToken>()))
                       .Returns(async (string _, LightningMoney? _, PayInvoiceOptions o, CancellationToken _) =>
                       {
                           options = o;
                           stored = payment;
                           await release.Task;
                           payment.Succeed(new Secret(Enumerable.Repeat((byte)0x92, 32).ToArray()),
                                           DateTimeOffset.UtcNow);
                           return new PayInvoiceResult(payment, 1, 1);
                       });
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        using var call = connection.RouterClient.SendPaymentV2(new SendPaymentRequest
        {
            PaymentRequest = bolt11,
            TimeoutSeconds = timeout,
            FeeLimitMsat = 5_000,
            MaxParts = 3
        }, cancellationToken: Bounded);
        Assert.True(await call.ResponseStream.MoveNext(Bounded));
        var inFlight = call.ResponseStream.Current;
        release.SetResult();
        var rest = await ReadAllAsync(call.ResponseStream);
        using var track = connection.RouterClient.TrackPaymentV2(
            new TrackPaymentRequest { PaymentHash = ByteString.CopyFrom((byte[])hash) }, cancellationToken: Bounded);
        var tracked = await ReadAllAsync(track.ResponseStream);
        var paidAgain = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            using var again = connection.RouterClient.SendPaymentV2(
                new SendPaymentRequest { PaymentRequest = bolt11, TimeoutSeconds = 30 }, cancellationToken: Bounded);
            await again.ResponseStream.MoveNext(Bounded);
        });

        // Assert
        Assert.Equal(Payment.Types.PaymentStatus.InFlight, inFlight.Status);
        var succeeded = Assert.Single(rest);
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, succeeded.Status);
        Assert.Equal(string.Concat(Enumerable.Repeat("92", 32)), succeeded.PaymentPreimage);
        Assert.Equal(7ul, succeeded.PaymentIndex);
        Assert.Equal(LightningMoney.MilliSatoshis(5_000), options!.MaxFee);
        Assert.Equal(3, options.MaxParts);
        Assert.Equal(TimeSpan.FromSeconds(expectedTimeout), options.Timeout);
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, Assert.Single(tracked).Status);
        Assert.Equal(StatusCode.AlreadyExists, paidAgain.StatusCode);
    }

    [Fact]
    public async Task Given_AnUnknownHash_When_TrackPaymentV2_Then_NotFound()
    {
        // Arrange
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            using var call = connection.RouterClient.TrackPaymentV2(
                new TrackPaymentRequest { PaymentHash = ByteString.CopyFrom(new byte[32]) }, cancellationToken: Bounded);
            await call.ResponseStream.MoveNext(Bounded);
        });

        // Assert
        Assert.Equal(StatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task Given_AHoldInvoice_When_SubscribedHeldAndSettled_Then_OpenAcceptedSettledAreStreamed()
    {
        // Arrange
        var preimage = Enumerable.Repeat((byte)0xA1, 32).ToArray();
        var hash = new Hash(SHA256.HashData(preimage));
        _invoiceService.Setup(x => x.CreateHoldInvoiceAsync(hash, LightningMoney.Satoshis(3_000), "held", 600u,
                                                            (ushort?)90, It.IsAny<SourceLabels>(),
                                                            It.IsAny<CancellationToken>()))
                       .ReturnsAsync(() =>
                       {
                           var invoice = new InvoiceModel(hash, null, new Secret(new byte[32]),
                                                          LightningMoney.Satoshis(3_000), "held", "lnbcrt30u1hold",
                                                          DateTimeOffset.UtcNow, 600, 90)
                           { AddIndex = 11 };
                           _invoices.Add(invoice);
                           return invoice;
                       });
        _holdInvoices.Setup(x => x.SettleHoldInvoiceAsync(hash, It.IsAny<Secret>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(() =>
                     {
                         var invoice = _invoices.Single(i => i.PaymentHash == hash);
                         invoice.SettleHeld(new Secret(preimage), DateTimeOffset.UtcNow);
                         _events.Publish(new InvoiceSettledEvent(hash, LightningMoney.Satoshis(3_000),
                                                                 DateTimeOffset.UtcNow));
                         return invoice;
                     });
        using var connection = await ConnectAsync(LndMacaroonFiles.InvoiceFileName);

        // Act
        var added = await connection.InvoiceClient.AddHoldInvoiceAsync(new AddHoldInvoiceRequest
        {
            Hash = ByteString.CopyFrom((byte[])hash),
            Value = 3_000,
            Memo = "held",
            Expiry = 600,
            CltvExpiry = 90
        }, cancellationToken: Ct);
        using var call = connection.InvoiceClient.SubscribeSingleInvoice(
            new SubscribeSingleInvoiceRequest { RHash = ByteString.CopyFrom((byte[])hash) }, cancellationToken: Bounded);
        Assert.True(await call.ResponseStream.MoveNext(Bounded));
        var open = call.ResponseStream.Current;
        var held = _invoices.Single(i => i.PaymentHash == hash);
        held.Hold(LightningMoney.Satoshis(3_000));
        held.Htlcs = [new InvoiceHtlc(new ShortChannelId(150, 7, 0), 4, 3_000_000, 151, DateTimeOffset.UtcNow, null,
                                      260, InvoiceHtlcState.Accepted, 3_000_000)];
        Assert.True(await call.ResponseStream.MoveNext(Bounded));
        var accepted = call.ResponseStream.Current;
        await connection.InvoiceClient.SettleInvoiceAsync(
            new SettleInvoiceMsg { Preimage = ByteString.CopyFrom(preimage) }, cancellationToken: Ct);
        var rest = await ReadAllAsync(call.ResponseStream);

        // Assert
        Assert.Equal("lnbcrt30u1hold", added.PaymentRequest);
        Assert.Equal(11ul, added.AddIndex);
        Assert.Equal(Invoice.Types.InvoiceState.Open, open.State);
        Assert.Equal(Invoice.Types.InvoiceState.Accepted, accepted.State);
        var htlc = Assert.Single(accepted.Htlcs);
        Assert.Equal(new ShortChannelId(150, 7, 0), new ShortChannelId(htlc.ChanId));
        Assert.Equal(InvoiceHTLCState.Accepted, htlc.State);
        Assert.Equal(260, htlc.ExpiryHeight);
        Assert.Equal(Invoice.Types.InvoiceState.Settled, Assert.Single(rest).State);
    }

    [Fact]
    public async Task Given_AnOpenInvoice_When_CancelInvoice_Then_TheHoldServiceCancelsIt()
    {
        // Arrange
        var hash = new Hash(Enumerable.Repeat((byte)0xB1, 32).ToArray());
        using var connection = await ConnectAsync(LndMacaroonFiles.InvoiceFileName);

        // Act
        await connection.InvoiceClient.CancelInvoiceAsync(
            new CancelInvoiceMsg { PaymentHash = ByteString.CopyFrom((byte[])hash) }, cancellationToken: Ct);

        // Assert
        _holdInvoices.Verify(x => x.CancelHoldInvoiceAsync(hash, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Given_InvoiceSubscription_When_InvoicesAreAddedAndSettled_Then_ReplayThenLiveUpdates()
    {
        // Arrange: two invoices exist, the subscriber has seen add_index 1
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        _invoices.Add(CreateInvoice(1, start, InvoiceStatus.Open));
        _invoices.Add(CreateInvoice(2, start.AddMinutes(1), InvoiceStatus.Open));
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        using var call = connection.LightningClient.SubscribeInvoices(new InvoiceSubscription { AddIndex = 1 },
                                                                      cancellationToken: Bounded);
        Assert.True(await call.ResponseStream.MoveNext(Bounded));
        var replayed = call.ResponseStream.Current;
        _invoices.Add(CreateInvoice(3, start.AddMinutes(2), InvoiceStatus.Open));
        Assert.True(await call.ResponseStream.MoveNext(Bounded));
        var added = call.ResponseStream.Current;
        var settling = _invoices[0];
        settling.Accept(settling.Amount!);
        settling.Settle(start.AddMinutes(3));
        settling.SettleIndex = 1;
        _events.Publish(new InvoiceSettledEvent(settling.PaymentHash, settling.Amount!, start.AddMinutes(3)));
        Assert.True(await call.ResponseStream.MoveNext(Bounded));
        var settled = call.ResponseStream.Current;

        // Assert
        Assert.Equal(2ul, replayed.AddIndex);
        Assert.Equal(3ul, added.AddIndex);
        Assert.Equal(Invoice.Types.InvoiceState.Settled, settled.State);
        Assert.Equal(1ul, settled.SettleIndex);
    }

    [Fact]
    public async Task Given_AForceClosedChannel_When_ClosedChannels_Then_ItsResolutionsAndBalancesAreListed()
    {
        // Arrange: our to_local swept by us, an incoming HTLC the peer took, our anchor ignored
        var channel = CreateChannel(7, ChannelState.Closed);
        _closedChannels.Add(channel);
        var commitment = new TxId(Enumerable.Repeat((byte)0xC1, 32).ToArray());
        _closes.Add(new ChannelCloseModel(channel.ChannelId, ChannelCloseKind.LocalCommitment, commitment, 3, 170,
                                          new Hash(new byte[32]), DateTimeOffset.UtcNow));
        _outputs.Add(new OutputResolutionModel
        {
            TransactionId = commitment,
            OutputIndex = 0,
            ChannelId = channel.ChannelId,
            Descriptor = OutputDescriptorKind.DelayedToLocal,
            State = OutputResolutionState.Irrevocable,
            ResolvingTransactionId = new TxId(Enumerable.Repeat((byte)0xC2, 32).ToArray())
        });
        _outputs.Add(new OutputResolutionModel
        {
            TransactionId = commitment,
            OutputIndex = 1,
            ChannelId = channel.ChannelId,
            Descriptor = OutputDescriptorKind.LocalReceivedHtlc,
            State = OutputResolutionState.Resolved
        });
        _outputs.Add(new OutputResolutionModel
        {
            TransactionId = commitment,
            OutputIndex = 2,
            ChannelId = channel.ChannelId,
            Descriptor = OutputDescriptorKind.OurAnchor,
            State = OutputResolutionState.Ignored
        });
        _outputs.Add(new OutputResolutionModel
        {
            TransactionId = commitment,
            OutputIndex = 3,
            ChannelId = channel.ChannelId,
            Descriptor = OutputDescriptorKind.PeerOutput,
            State = OutputResolutionState.Ignored
        });
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var closed = await connection.LightningClient.ClosedChannelsAsync(new ClosedChannelsRequest(),
                                                                           cancellationToken: Ct);

        // Assert
        var summary = Assert.Single(closed.Channels);
        Assert.Equal(ChannelCloseSummary.Types.ClosureType.LocalForceClose, summary.CloseType);
        Assert.Equal(Initiator.Local, summary.CloseInitiator);
        Assert.Equal(170u, summary.CloseHeight);
        Assert.Equal([ResolutionType.Commit, ResolutionType.IncomingHtlc, ResolutionType.Anchor],
                     summary.Resolutions.Select(r => r.ResolutionType));
        Assert.Equal([ResolutionOutcome.Claimed, ResolutionOutcome.Unclaimed, ResolutionOutcome.Abandoned],
                     summary.Resolutions.Select(r => r.Outcome));
        Assert.Equal(new TxId(Enumerable.Repeat((byte)0xC2, 32).ToArray()).ToString(),
                     summary.Resolutions[0].SweepTxid);
    }

    [Fact]
    public async Task Given_ABakedMacaroon_When_UsedListedAndDeleted_Then_ItWorksUntilItsRootKeyIsGone()
    {
        // Arrange
        using var admin = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var baked = await admin.LightningClient.BakeMacaroonAsync(new BakeMacaroonRequest
        {
            RootKeyId = 5,
            Permissions = { new MacaroonPermission { Entity = "info", Action = "read" } }
        }, cancellationToken: Ct);
        var certificate = await File.ReadAllBytesAsync(Path.Combine(_directory, "tls.cert"), Ct);
        using var limited = LndNodeConnection.CreateWithoutNodeInfo(
            LndSettings.FromBytes(Endpoint, certificate, Convert.FromHexString(baked.Macaroon)));
        var info = await limited.LightningClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: Ct);
        var denied = await Assert.ThrowsAsync<RpcException>(
            () => limited.LightningClient.WalletBalanceAsync(new WalletBalanceRequest(), cancellationToken: Ct)
                         .ResponseAsync);
        var ids = await admin.LightningClient.ListMacaroonIDsAsync(new ListMacaroonIDsRequest(),
                                                                   cancellationToken: Ct);
        var deleted = await admin.LightningClient.DeleteMacaroonIDAsync(new DeleteMacaroonIDRequest { RootKeyId = 5 },
                                                                        cancellationToken: Ct);
        var revoked = await Assert.ThrowsAsync<RpcException>(
            () => limited.LightningClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: Ct).ResponseAsync);
        var defaultKept = await Assert.ThrowsAsync<RpcException>(
            () => admin.LightningClient.DeleteMacaroonIDAsync(new DeleteMacaroonIDRequest { RootKeyId = 0 },
                                                              cancellationToken: Ct).ResponseAsync);
        var badEntity = await Assert.ThrowsAsync<RpcException>(
            () => admin.LightningClient.BakeMacaroonAsync(new BakeMacaroonRequest
            {
                Permissions = { new MacaroonPermission { Entity = "everything", Action = "read" } }
            }, cancellationToken: Ct).ResponseAsync);

        // Assert
        Assert.Equal(NodeIdHex, info.IdentityPubkey);
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
        Assert.Equal([0ul, 5ul], ids.RootKeyIds);
        Assert.True(deleted.Deleted);
        Assert.Equal(StatusCode.Unauthenticated, revoked.StatusCode);
        Assert.Equal(StatusCode.InvalidArgument, defaultKept.StatusCode);
        Assert.Equal(StatusCode.InvalidArgument, badEntity.StatusCode);
        Assert.Equal("5"u8.ToArray(),
                     MacaroonId.Decode(Macaroon.Deserialize(Convert.FromHexString(baked.Macaroon)).Identifier)
                               .StorageId);
    }

    private static (string Bolt11, Hash Hash) CreateBolt11(byte tag, long sat)
    {
        using var key = new Key(s_nodeKey);
        var hash = Enumerable.Repeat(tag, 32).ToArray();
        var bolt11 = new Bolt11.Models.Invoice(LightningMoney.Satoshis(sat), "pay me", new uint256(hash),
                                               new uint256(Enumerable.Repeat((byte)0xEE, 32).ToArray()),
                                               BitcoinNetwork.Regtest).Encode(key);
        return (bolt11, new Hash(hash));
    }

    private static async Task<List<T>> ReadAllAsync<T>(IAsyncStreamReader<T> stream)
    {
        var items = new List<T>();
        while (await stream.MoveNext(Bounded))
            items.Add(stream.Current);
        return items;
    }

    private delegate bool TryGetChannelState(ChannelId channelId, out ChannelState state);

    /// <summary>A node command dispatcher with canned answers; records every request.</summary>
    private sealed class FakeDispatcher : INodeCommandDispatcher
    {
        private readonly Dictionary<Type, Func<object, object?>> _handlers = [];

        public List<object> Requests { get; } = [];

        public void On<TRequest, TResponse>(Func<TRequest, TResponse> handler) =>
            _handlers[typeof(TRequest)] = request => handler((TRequest)request);

        public Task<TResponse> DispatchAsync<TRequest, TResponse>(TRequest request,
                                                                  CancellationToken cancellationToken)
            where TRequest : notnull
        {
            lock (Requests)
                Requests.Add(request);
            return _handlers.TryGetValue(typeof(TRequest), out var handler)
                       ? Task.FromResult((TResponse)handler(request)!)
                       : Task.FromResult(default(TResponse)!);
        }
    }
}