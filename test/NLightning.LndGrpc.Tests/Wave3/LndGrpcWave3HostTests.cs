using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace NLightning.LndGrpc.Tests.Wave3;

using Application.Channels.Acceptance;
using Application.Payments.Interception;
using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Acceptance;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Payments.Interception;
using Domain.Payments.Interfaces;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.ValueObjects;
using Google.Protobuf;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using LndGrpc.Macaroons;
using LndGrpc.Services;
using LndGrpc.Tls;
using Testing.Lnd;
using Testing.Lnd.Lnrpc;
using Testing.Lnd.Routerrpc;
using Testing.Lnd.Walletrpc;
using AddressType = Domain.Bitcoin.Enums.AddressType;
using ListUnspentRequest = Testing.Lnd.Walletrpc.ListUnspentRequest;

/// <summary>
/// The wave 3 streams and wallet methods (NL-1180, NL-1183..NL-1185) on a real loopback TLS listener, driven by our
/// in-tree LND client: the channel acceptor against the real <see cref="ChannelOpenDecisionGate"/>, the HTLC
/// interceptor against the real <see cref="HtlcInterceptorHub"/>, WalletKit over a mocked wallet PSBT service and
/// GetTransactions over a mocked accounting feed.
/// </summary>
public sealed partial class LndGrpcWave3HostTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nltg-lndgrpc3-" + Guid.NewGuid().ToString("N"));
    private readonly ChannelOpenDecisionGate _gate = new(NullLogger<ChannelOpenDecisionGate>.Instance);
    private readonly HtlcInterceptorHub _hub = new(NullLogger<HtlcInterceptorHub>.Instance);
    private readonly Mock<IWalletPsbtService> _psbt = new();
    private readonly Mock<IBitcoinChainService> _chain = new();
    private readonly List<AccountingEventModel> _accountingEvents = [];
    private readonly List<BroadcastTransactionModel> _broadcastRows = [];
    private readonly List<ChannelCloseModel> _closes = [];
    private readonly List<OutputResolutionModel> _outputs = [];
    private readonly List<WalletAddressModel> _walletAddresses = [];
    private readonly List<UtxoModel> _unspent = [];
    private readonly Mock<ISecureKeyManager> _keys = new();

    private ServiceProvider? _services;
    private LndGrpcHost? _host;

    public async ValueTask InitializeAsync()
    {
        _services = BuildServices();
        _host = new LndGrpcHost(_services,
                                Options.Create(new LndGrpcOptions
                                {
                                    Enabled = true,
                                    Port = 0,
                                    AcceptorTimeout = TimeSpan.FromSeconds(2)
                                }), _directory, NullLogger<LndGrpcHost>.Instance);
        await _host.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.StopAsync(CancellationToken.None);
        if (_services is not null)
            await _services.DisposeAsync();
        _hub.Dispose();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task Given_AnAcceptorClient_When_AnOpenArrives_Then_ItsRejectionWithAnErrorIsTheDecision()
    {
        // Arrange
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        using var stream = connection.LightningClient.ChannelAcceptor(cancellationToken: Ct);
        await WaitUntilAsync(() => _gate.HasDeciders);

        // Act
        var decision = _gate.DecideAsync(CreateOpenRequest(1), Ct);
        Assert.True(await stream.ResponseStream.MoveNext(Ct));
        var request = stream.ResponseStream.Current;
        await stream.RequestStream.WriteAsync(new ChannelAcceptResponse
        {
            PendingChanId = request.PendingChanId,
            Accept = false,
            Error = "we only take private channels"
        }, Ct);

        // Assert
        var result = await decision;
        Assert.False(result.Accept);
        Assert.Equal("we only take private channels", result.Error);
        Assert.Equal(500_000UL, request.FundingAmt);
        Assert.Equal(Convert.FromHexString("02" + new string('a', 64)), request.NodePubkey.ToByteArray());
    }

    [Fact]
    public async Task Given_AnAcceptorClient_When_ItAcceptsWithValues_Then_TheDecisionCarriesThem()
    {
        // Arrange
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        using var stream = connection.LightningClient.ChannelAcceptor(cancellationToken: Ct);
        await WaitUntilAsync(() => _gate.HasDeciders);

        // Act
        var decision = _gate.DecideAsync(CreateOpenRequest(2), Ct);
        Assert.True(await stream.ResponseStream.MoveNext(Ct));
        await stream.RequestStream.WriteAsync(new ChannelAcceptResponse
        {
            PendingChanId = stream.ResponseStream.Current.PendingChanId,
            Accept = true,
            CsvDelay = 300,
            MaxHtlcCount = 50,
            MinAcceptDepth = 2
        }, Ct);

        // Assert
        var result = await decision;
        Assert.True(result.Accept);
        Assert.Equal((ushort)300, result.ToSelfDelay);
        Assert.Equal((ushort)50, result.MaxAcceptedHtlcs);
        Assert.Equal(2U, result.MinimumDepth);
    }

    [Fact]
    public async Task Given_AnAcceptorThatDoesNotAnswer_When_TheTimeoutPasses_Then_TheOpenIsRejected()
    {
        // Arrange
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        using var stream = connection.LightningClient.ChannelAcceptor(cancellationToken: Ct);
        await WaitUntilAsync(() => _gate.HasDeciders);

        // Act
        var result = await _gate.DecideAsync(CreateOpenRequest(3), Ct);

        // Assert
        Assert.False(result.Accept);
        Assert.Equal(ChannelOpenDecision.GenericRejection, result.Error);
    }

    [Fact]
    public async Task Given_NoAcceptorClient_When_AnOpenArrives_Then_ItIsAccepted()
    {
        // Arrange: a client that came and went
        using (var connection = Connect(LndMacaroonFiles.AdminFileName))
        {
            using var stream = connection.LightningClient.ChannelAcceptor(cancellationToken: Ct);
            await WaitUntilAsync(() => _gate.HasDeciders);
            await stream.RequestStream.CompleteAsync();
        }

        await WaitUntilAsync(() => !_gate.HasDeciders);

        // Act
        var result = await _gate.DecideAsync(CreateOpenRequest(4), Ct);

        // Assert
        Assert.True(result.Accept);
    }

    [Fact]
    public async Task Given_TheReadOnlyMacaroon_When_ChannelAcceptorOrFundPsbt_Then_PermissionDenied()
    {
        // Arrange
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var fund = await Assert.ThrowsAsync<RpcException>(async () =>
            await connection.WalletKitClient.FundPsbtAsync(new FundPsbtRequest(), cancellationToken: Ct));
        using var stream = connection.LightningClient.ChannelAcceptor(cancellationToken: Ct);
        var acceptor = await Assert.ThrowsAsync<RpcException>(async () => await stream.ResponseStream.MoveNext(Ct));

        // Assert
        Assert.Equal(StatusCode.PermissionDenied, fund.StatusCode);
        Assert.Equal(StatusCode.PermissionDenied, acceptor.StatusCode);
    }

    [Fact]
    public async Task Given_AnInterceptor_When_AForwardIsHeld_Then_ItsFailReachesTheSwitch()
    {
        // Arrange
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        using var stream = connection.RouterClient.HtlcInterceptor(cancellationToken: Ct);
        await WaitUntilAsync(() => _hub.IsActive);
        var resolved = new TaskCompletionSource<ForwardInterceptResolution>();

        // Act
        var outcome = _hub.Intercept(CreateForward(1), 100, false, r =>
        {
            resolved.TrySetResult(r);
            return Task.CompletedTask;
        });
        Assert.True(await stream.ResponseStream.MoveNext(Ct));
        var request = stream.ResponseStream.Current;
        await stream.RequestStream.WriteAsync(new ForwardHtlcInterceptResponse
        {
            IncomingCircuitKey = request.IncomingCircuitKey,
            Action = ResolveHoldForwardAction.Fail
        }, Ct);

        // Assert
        Assert.Equal(ForwardInterceptOutcome.Held, outcome);
        var resolution = await resolved.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(ForwardInterceptAction.Fail, resolution.Action);
        Assert.Equal(FailureCode.TemporaryChannelFailure, resolution.FailureCode);
        Assert.Equal(1UL, request.IncomingCircuitKey.HtlcId);
        Assert.Equal(500 - 19, request.AutoFailHeight);
        // The hub drops the hold only after the callback returned (NL-1234), and this callback completes `resolved`
        // inline, so the removal may still be running here (NL-1235)
        await WaitUntilAsync(() => _hub.HeldCount == 0);
    }

    [Fact]
    public async Task Given_AnInterceptor_When_ItSettlesWithTheRightPreimage_Then_TheSwitchGetsIt()
    {
        // Arrange
        var preimage = Enumerable.Repeat((byte)5, 32).ToArray();
        var hash = System.Security.Cryptography.SHA256.HashData(preimage);
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        using var stream = connection.RouterClient.HtlcInterceptor(cancellationToken: Ct);
        await WaitUntilAsync(() => _hub.IsActive);
        var resolved = new TaskCompletionSource<ForwardInterceptResolution>();

        // Act
        _hub.Intercept(CreateForward(2) with { PaymentHash = new Hash(hash) }, 100, false, r =>
        {
            resolved.TrySetResult(r);
            return Task.CompletedTask;
        });
        Assert.True(await stream.ResponseStream.MoveNext(Ct));
        await stream.RequestStream.WriteAsync(new ForwardHtlcInterceptResponse
        {
            IncomingCircuitKey = stream.ResponseStream.Current.IncomingCircuitKey,
            Action = ResolveHoldForwardAction.Settle,
            Preimage = ByteString.CopyFrom(preimage)
        }, Ct);

        // Assert
        var resolution = await resolved.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(ForwardInterceptAction.Settle, resolution.Action);
        Assert.Equal(preimage, (byte[])resolution.Preimage!.Value);
    }

    [Fact]
    public async Task Given_AnInterceptor_When_ItSettlesWithAWrongPreimage_Then_TheStreamEndsAndTheForwardResumes()
    {
        // Arrange
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        using var stream = connection.RouterClient.HtlcInterceptor(cancellationToken: Ct);
        await WaitUntilAsync(() => _hub.IsActive);
        var resolved = new TaskCompletionSource<ForwardInterceptResolution>();
        _hub.Intercept(CreateForward(3), 100, false, r =>
        {
            resolved.TrySetResult(r);
            return Task.CompletedTask;
        });
        Assert.True(await stream.ResponseStream.MoveNext(Ct));

        // Act
        await stream.RequestStream.WriteAsync(new ForwardHtlcInterceptResponse
        {
            IncomingCircuitKey = stream.ResponseStream.Current.IncomingCircuitKey,
            Action = ResolveHoldForwardAction.Settle,
            Preimage = ByteString.CopyFrom(new byte[32])
        }, Ct);
        var end = await Assert.ThrowsAsync<RpcException>(async () => await stream.ResponseStream.MoveNext(Ct));

        // Assert: LND ends the stream on a wrong preimage; without the interceptor the held forward resumes
        Assert.Equal(StatusCode.InvalidArgument, end.StatusCode);
        var resolution = await resolved.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(ForwardInterceptAction.Resume, resolution.Action);
        Assert.False(_hub.IsActive);
    }

    [Fact]
    public async Task Given_AnInterceptor_When_ASecondConnects_Then_ItIsRefused()
    {
        // Arrange
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        using var first = connection.RouterClient.HtlcInterceptor(cancellationToken: Ct);
        await WaitUntilAsync(() => _hub.IsActive);

        // Act
        using var second = connection.RouterClient.HtlcInterceptor(cancellationToken: Ct);
        var e = await Assert.ThrowsAsync<RpcException>(async () => await second.ResponseStream.MoveNext(Ct));

        // Assert
        Assert.Equal(StatusCode.AlreadyExists, e.StatusCode);
        Assert.True(_hub.IsActive);
    }

    [Fact]
    public async Task Given_TheWallet_When_ListUnspent_Then_TheOutputsComeBackInLndsShape()
    {
        // Arrange
        var txId = new TxId(Enumerable.Repeat((byte)0xab, 32).ToArray());
        _psbt.Setup(x => x.ListUnspentAsync(1, (uint)int.MaxValue, It.IsAny<CancellationToken>()))
             .ReturnsAsync([
                 new WalletUnspentOutput(txId, 1, LightningMoney.Satoshis(50_000), AddressType.P2Wpkh,
                                         "bcrt1qaddress", new BitcoinScript(new byte[] { 0x00, 0x14 }), 3)
             ]);
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var response = await connection.WalletKitClient.ListUnspentAsync(new ListUnspentRequest { MinConfs = 1 },
                                                                         cancellationToken: Ct);

        // Assert
        var utxo = Assert.Single(response.Utxos);
        Assert.Equal(50_000, utxo.AmountSat);
        Assert.Equal(3, utxo.Confirmations);
        Assert.Equal("0014", utxo.PkScript);
        Assert.Equal(txId.ToString(), utxo.Outpoint.TxidStr);
        Assert.Equal((byte[])txId, utxo.Outpoint.TxidBytes.ToByteArray());
        Assert.Equal(1U, utxo.Outpoint.OutputIndex);
    }

    [Fact]
    public async Task Given_ARawTemplate_When_FundPsbt_Then_TheServiceGetsTheOutputsRateAndLease()
    {
        // Arrange
        PsbtFundRequest? captured = null;
        var lockId = Enumerable.Repeat((byte)0x77, 32).ToArray();
        _psbt.Setup(x => x.FundPsbtAsync(It.IsAny<PsbtFundRequest>(), It.IsAny<CancellationToken>()))
             .Callback((PsbtFundRequest r, CancellationToken _) => captured = r)
             .ReturnsAsync(new PsbtFundResult([1, 2, 3], 1,
                                              [
                                                  new WalletLease(lockId, new TxId(new byte[32]), 0,
                                                                  DateTimeOffset.FromUnixTimeSeconds(1_000),
                                                                  LightningMoney.Satoshis(10_000),
                                                                  new BitcoinScript([0x51]))
                                              ], LightningMoney.Satoshis(150)));
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        var request = new FundPsbtRequest
        {
            Raw = new TxTemplate(),
            SatPerVbyte = 2,
            CustomLockId = ByteString.CopyFrom(lockId),
            LockExpirationSeconds = 600
        };
        request.Raw.Outputs["bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080"] = 20_000;

        // Act
        var response = await connection.WalletKitClient.FundPsbtAsync(request, cancellationToken: Ct);

        // Assert
        Assert.Equal([1, 2, 3], response.FundedPsbt.ToByteArray());
        Assert.Equal(1, response.ChangeOutputIndex);
        Assert.Equal(1_000UL, Assert.Single(response.LockedUtxos).Expiration);
        Assert.NotNull(captured);
        Assert.Equal(500, captured.FeeRatePerKw);
        Assert.Equal(lockId, captured.LockId);
        Assert.Equal(TimeSpan.FromSeconds(600), captured.LockDuration);
        Assert.Equal(LightningMoney.Satoshis(20_000), Assert.Single(captured.Outputs).Amount);
        Assert.Empty(captured.Inputs);
    }

    [Fact]
    public async Task Given_ARefusedLease_When_LeaseOutput_Then_TheRefusalIsAStatus()
    {
        // Arrange
        _psbt.Setup(x => x.LeaseAsync(It.IsAny<byte[]>(), It.IsAny<TxId>(), It.IsAny<uint>(), It.IsAny<TimeSpan>(),
                                      It.IsAny<CancellationToken>()))
             .ThrowsAsync(new WalletPsbtException(WalletPsbtError.FailedPrecondition, "output already locked"));
        using var connection = Connect(LndMacaroonFiles.AdminFileName);

        // Act
        var e = await Assert.ThrowsAsync<RpcException>(async () =>
            await connection.WalletKitClient.LeaseOutputAsync(new LeaseOutputRequest
            {
                Id = ByteString.CopyFrom(new byte[32]),
                Outpoint = new OutPoint { TxidBytes = ByteString.CopyFrom(new byte[32]), OutputIndex = 0 }
            }, cancellationToken: Ct));

        // Assert
        Assert.Equal(StatusCode.FailedPrecondition, e.StatusCode);
        Assert.Contains("already locked", e.Status.Detail);
    }

    [Fact]
    public async Task Given_ARefusedPublish_When_PublishTransaction_Then_ItIsAnRpcErrorLikeLnd()
    {
        // Arrange
        _psbt.Setup(x => x.PublishAsync(It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
             .ThrowsAsync(new WalletPsbtException(WalletPsbtError.PublishRefused,
                                                  "transaction rejected: output already spent"));
        using var connection = Connect(LndMacaroonFiles.AdminFileName);

        // Act
        var e = await Assert.ThrowsAsync<RpcException>(async () =>
            await connection.WalletKitClient.PublishTransactionAsync(new Testing.Lnd.Walletrpc.Transaction
            {
                TxHex = ByteString.CopyFrom([1, 2, 3])
            }, cancellationToken: Ct));

        // Assert
        Assert.Equal(StatusCode.Unknown, e.StatusCode);
        Assert.Equal("transaction rejected: output already spent", e.Status.Detail);
    }

    [Fact]
    public async Task Given_AnAcceptedPublish_When_PublishTransaction_Then_PublishErrorIsEmpty()
    {
        // Arrange
        _psbt.Setup(x => x.PublishAsync(It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(true);
        using var connection = Connect(LndMacaroonFiles.AdminFileName);

        // Act
        var response = await connection.WalletKitClient.PublishTransactionAsync(new Testing.Lnd.Walletrpc.Transaction
        {
            TxHex = ByteString.CopyFrom([1, 2, 3])
        }, cancellationToken: Ct);

        // Assert
        Assert.Equal("", response.PublishError);
    }

    [Fact]
    public async Task Given_BitcoindsMempoolMinimum_When_EstimateFee_Then_ItIsTheMinRelayFee()
    {
        // Arrange
        _chain.Setup(c => c.GetMempoolMinFeeRatePerKwAsync()).ReturnsAsync(1_000U);
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var response = await connection.WalletKitClient.EstimateFeeAsync(new Testing.Lnd.Walletrpc.EstimateFeeRequest { ConfTarget = 6 },
                                                                          cancellationToken: Ct);

        // Assert
        Assert.Equal(2_500, response.SatPerKw);
        Assert.Equal(1_000, response.MinRelayFeeSatPerKw);
    }

    [Fact]
    public async Task Given_ADepositNotBroadcastByUs_When_GetTransactions_Then_ItsRawTransactionComesFromItsBlock()
    {
        // Arrange
        var deposit = NBitcoin.Network.RegTest.CreateTransaction();
        deposit.Inputs.Add(new NBitcoin.TxIn(new NBitcoin.OutPoint(NBitcoin.RandomUtils.GetUInt256(), 0)));
        deposit.Outputs.Add(NBitcoin.Money.Satoshis(30_000), new NBitcoin.Key().PubKey.WitHash.ScriptPubKey);
        var block = NBitcoin.Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
        block.Transactions.Add(deposit);
        _chain.Setup(c => c.GetBlockAsync(101)).ReturnsAsync(block);
        AddEvent(AccountingEventKind.WalletReceived, new TxId(deposit.GetHash().ToBytes()), 0, 101, 30_000_000);
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var response = await connection.LightningClient.GetTransactionsAsync(new GetTransactionsRequest(),
                                                                              cancellationToken: Ct);

        // Assert
        var listed = Assert.Single(response.Transactions);
        Assert.Equal(Convert.ToHexStringLower(NBitcoin.BitcoinSerializableExtensions.ToBytes(deposit)), listed.RawTxHex);
        Assert.True(Assert.Single(listed.OutputDetails).IsOurAddress);
    }

    [Fact]
    public async Task Given_TheAccountingFeed_When_GetTransactions_Then_EachWalletTransactionHasItsNetAmount()
    {
        // Arrange: a 100,000 sat deposit at block 101, then a spend of it at block 120 paying 60,000 away with 39,000
        // change (fee 1,000)
        var deposit = new TxId(Enumerable.Repeat((byte)0x01, 32).ToArray());
        var spend = new TxId(Enumerable.Repeat((byte)0x02, 32).ToArray());
        AddEvent(AccountingEventKind.WalletReceived, deposit, 0, 101, 100_000_000);
        AddEvent(AccountingEventKind.WalletOutputSpent, deposit, 0, 120, -100_000_000, ("spentBy", spend.ToString()));
        AddEvent(AccountingEventKind.WalletReceived, spend, 1, 120, 39_000_000);
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var all = await connection.LightningClient.GetTransactionsAsync(new GetTransactionsRequest(),
                                                                         cancellationToken: Ct);
        var sinceBlock = await connection.LightningClient.GetTransactionsAsync(
                             new GetTransactionsRequest { StartHeight = 110, EndHeight = 130 }, cancellationToken: Ct);

        // Assert
        Assert.Equal(2, all.Transactions.Count);
        Assert.Equal(deposit.ToString(), all.Transactions[0].TxHash);
        Assert.Equal(100_000, all.Transactions[0].Amount);
        Assert.Equal(50, all.Transactions[0].NumConfirmations);
        Assert.Equal(-61_000, all.Transactions[1].Amount);
        Assert.Equal($"{deposit}:0", Assert.Single(all.Transactions[1].PreviousOutpoints).Outpoint);
        Assert.Equal(spend.ToString(), Assert.Single(sinceBlock.Transactions).TxHash);
    }

    [Fact]
    public async Task Given_AReorg_When_GetTransactions_Then_TheReversedDepositIsGone()
    {
        // Arrange
        var deposit = new TxId(Enumerable.Repeat((byte)0x03, 32).ToArray());
        var key = AddEvent(AccountingEventKind.WalletReceived, deposit, 0, 140, 5_000_000);
        _accountingEvents.Add(new AccountingEventModel
        {
            EventKey = $"{key}:rev:140",
            Kind = AccountingEventKind.Reversal,
            OccurredAt = DateTimeOffset.UnixEpoch,
            LedgerSeq = _accountingEvents.Count + 1
        });
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var response = await connection.LightningClient.GetTransactionsAsync(new GetTransactionsRequest(),
                                                                              cancellationToken: Ct);

        // Assert
        Assert.Empty(response.Transactions);
    }

    [Fact]
    public async Task Given_MoreThanOneEventPage_When_GetTransactions_Then_NetAmountsAreAggregatedBeforePaging()
    {
        var spend = new TxId(Enumerable.Repeat((byte)0x06, 32).ToArray());
        for (uint index = 0; index < 1_001; index++)
        {
            var previous = new TxId(Enumerable.Repeat((byte)0x05, 32).ToArray());
            AddEvent(AccountingEventKind.WalletOutputSpent, previous, index, 120, -1_000_000,
                     ("spentBy", spend.ToString()));
        }
        AddEvent(AccountingEventKind.WalletReceived, spend, 0, 120, 990_000_000);
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);

        var response = await connection.LightningClient.GetTransactionsAsync(
                           new GetTransactionsRequest { StartHeight = 120, EndHeight = 120, MaxTransactions = 1 },
                           cancellationToken: Ct);

        var listed = Assert.Single(response.Transactions);
        Assert.Equal(spend.ToString(), listed.TxHash);
        Assert.Equal(-11_000, listed.Amount);
        Assert.Equal(1_001, listed.PreviousOutpoints.Count);
    }

    private string AddEvent(AccountingEventKind kind, TxId txId, uint index, uint height, long amountMsat,
                            params (string Key, string Value)[] details)
    {
        var key = $"{kind}:{txId}:{index}";
        _accountingEvents.Add(new AccountingEventModel
        {
            EventKey = key,
            Kind = kind,
            OccurredAt = DateTimeOffset.UnixEpoch.AddSeconds(height),
            BlockHeight = height,
            TxId = txId,
            OutputIndex = index,
            AmountMsat = amountMsat,
            Details = details.ToDictionary(d => d.Key, d => d.Value),
            LedgerSeq = _accountingEvents.Count + 1
        });
        return key;
    }

    private static ChannelOpenRequest CreateOpenRequest(byte tag) =>
        new(new CompactPubKey(Convert.FromHexString("02" + new string('a', 64))), ChainConstants.Regtest,
            new ChannelId(Enumerable.Repeat(tag, 32).ToArray()), LightningMoney.Satoshis(500_000), LightningMoney.Zero,
            LightningMoney.Satoshis(354), LightningMoney.Satoshis(100_000), LightningMoney.Satoshis(5_000),
            LightningMoney.MilliSatoshis(1), 253, 144, 483, 0, null, false);

    private static InterceptedForward CreateForward(ulong htlcId) =>
        new(new ChannelId(Enumerable.Repeat((byte)1, 32).ToArray()), htlcId, new ShortChannelId(150, 1, 0),
            new ShortChannelId(151, 1, 0), null, new Hash(new byte[32]), LightningMoney.MilliSatoshis(10_100),
            LightningMoney.MilliSatoshis(10_000), 500, 460, 0, new byte[1366], []);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private LndNodeConnection Connect(string macaroonFile) =>
        LndNodeConnection.CreateWithoutNodeInfo(
            LndSettings.FromFiles($"https://127.0.0.1:{_host!.BoundPort}",
                                  Path.Combine(_directory, LndTlsFiles.CertificateFileName),
                                  Path.Combine(_directory, macaroonFile)));

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }));
        services.AddSingleton(new Mock<ILightningSigner>().Object);
        services.AddSingleton(new Mock<IChannelMemoryRepository>().Object);
        services.AddSingleton(new Mock<IPeerManager>().Object);
        services.AddSingleton(new Mock<IInvoiceService>().Object);
        services.AddSingleton<IChannelOpenDecisionGate>(_gate);
        services.AddSingleton(_hub);
        services.AddSingleton(_psbt.Object);
        var fees = new Mock<IFeeService>();
        fees.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LightningMoney.Satoshis(2_500));
        services.AddSingleton(fees.Object);
        services.AddSingleton(_chain.Object);

        var monitor = new Mock<IBlockchainMonitor>();
        monitor.SetupGet(x => x.LastProcessedBlockHeight).Returns(150);
        services.AddSingleton(monitor.Object);
        var utxos = new Mock<IUtxoMemoryRepository>();
        utxos.Setup(x => x.GetUnreservedUtxos()).Returns([]);
        services.AddSingleton(utxos.Object);

        services.AddScoped(_ =>
        {
            var accounting = new Mock<IAccountingEventDbRepository>();
            accounting.Setup(x => x.GetWalletHistoryAsync(It.IsAny<uint>(), It.IsAny<uint>(), It.IsAny<long>(),
                                                          It.IsAny<int>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync((uint start, uint end, long after, int take, CancellationToken _) =>
                      {
                          var reversed = _accountingEvents.Where(e => e.Kind == AccountingEventKind.Reversal)
                                                          .Select(e => e.EventKey[..e.EventKey.LastIndexOf(":rev:", StringComparison.Ordinal)])
                                                          .ToHashSet(StringComparer.Ordinal);
                          return _accountingEvents.Where(e => e.LedgerSeq > after
                                                           && e.Kind is AccountingEventKind.WalletReceived or AccountingEventKind.WalletOutputSpent
                                                           && e.BlockHeight >= start && e.BlockHeight <= end
                                                           && !reversed.Contains(e.EventKey))
                                                  .OrderBy(e => e.LedgerSeq).Take(take).ToList();
                      });
            var broadcasts = new Mock<IBroadcastTransactionDbRepository>();
            broadcasts.Setup(x => x.GetPendingAsync())
                      .ReturnsAsync(() => _broadcastRows.Where(r => r.State == Domain.Onchain.Enums.BroadcastState.Pending)
                                                        .ToList());
            broadcasts.Setup(x => x.GetByTransactionIdAsync(It.IsAny<TxId>()))
                      .ReturnsAsync((TxId id) => _broadcastRows.FirstOrDefault(r => r.TransactionId == id));
            broadcasts.Setup(x => x.GetByChannelIdAsync(It.IsAny<ChannelId>()))
                      .ReturnsAsync((ChannelId id) => _broadcastRows.Where(r => r.ChannelId == id).ToList());
            var resolutions = new Mock<IOnchainResolutionDbRepository>();
            resolutions.Setup(x => x.GetClosesAsync()).ReturnsAsync(() => _closes.ToList());
            resolutions.Setup(x => x.GetOutputsByChannelIdAsync(It.IsAny<ChannelId>()))
                       .ReturnsAsync((ChannelId id) => _outputs.Where(o => o.ChannelId == id).ToList());
            var addresses = new Mock<IWalletAddressesDbRepository>();
            addresses.Setup(x => x.GetAllAddresses()).Returns(() => _walletAddresses.ToList());
            var utxoRows = new Mock<IUtxoDbRepository>();
            utxoRows.Setup(x => x.GetUnspentAsync(It.IsAny<bool>()))
                    .ReturnsAsync(() => _unspent.ToList());
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.SetupGet(x => x.WalletAddressesDbRepository).Returns(addresses.Object);
            unitOfWork.SetupGet(x => x.UtxoDbRepository).Returns(utxoRows.Object);
            unitOfWork.SetupGet(x => x.OnchainResolutionDbRepository).Returns(resolutions.Object);
            unitOfWork.SetupGet(x => x.AccountingEventDbRepository).Returns(accounting.Object);
            unitOfWork.SetupGet(x => x.BroadcastTransactionDbRepository).Returns(broadcasts.Object);
            return unitOfWork.Object;
        });
        services.AddSingleton(new Mock<IPaymentService>().Object);
        services.AddSingleton<LightningService>();
        services.AddSingleton<RouterService>();
        services.AddSingleton<WalletKitService>();
        services.AddSingleton(_keys.Object);
        return services.BuildServiceProvider();
    }
}