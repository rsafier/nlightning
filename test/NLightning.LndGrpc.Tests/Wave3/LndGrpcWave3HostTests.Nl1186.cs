using Google.Protobuf;
using Grpc.Core;
using Moq;

namespace NLightning.LndGrpc.Tests.Wave3;

using Application.Onchain.Anchors;
using Application.Onchain.Fees;
using Domain.Accounting.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Infrastructure.Bitcoin.Signers;
using Infrastructure.Bitcoin.Wallet.Models;
using LndGrpc.Macaroons;
using Testing.Lnd.Walletrpc;
using AddressType = Domain.Bitcoin.Enums.AddressType;

/// <summary>
/// The walletrpc methods of NL-1186 on the real listener: SignPsbt, GetTransaction, LabelTransaction,
/// RemoveTransaction, RequiredReserve, SubmitPackage, the message signatures, BumpFee/BumpForceCloseFee and the FundPsbt
/// options that were refused before.
/// </summary>
public sealed partial class LndGrpcWave3HostTests
{
    [Fact]
    public async Task Given_APsbt_When_SignPsbt_Then_TheSignedPsbtAndItsSignedInputsComeBack()
    {
        // Arrange
        _psbt.Setup(x => x.SignPsbtAsync(It.Is<byte[]>(b => b.SequenceEqual(new byte[] { 7, 7 })),
                                         It.IsAny<CancellationToken>()))
             .ReturnsAsync(new PsbtSignResult([8, 8, 8], [1, 3]));
        using var connection = Connect(LndMacaroonFiles.AdminFileName);

        // Act
        var response = await connection.WalletKitClient.SignPsbtAsync(
                           new SignPsbtRequest { FundedPsbt = ByteString.CopyFrom(7, 7) }, cancellationToken: Ct);
        var empty = await Assert.ThrowsAsync<RpcException>(async () =>
            await connection.WalletKitClient.SignPsbtAsync(new SignPsbtRequest(), cancellationToken: Ct));

        // Assert
        Assert.Equal([8, 8, 8], response.SignedPsbt.ToByteArray());
        Assert.Equal([1u, 3u], response.SignedInputs);
        Assert.Equal(StatusCode.InvalidArgument, empty.StatusCode);
    }

    [Fact]
    public async Task Given_AWalletTransaction_When_GetTransaction_Then_ItsHistoryEntryAndAnUnknownOneIsNotFound()
    {
        // Arrange
        var deposit = new TxId(Enumerable.Repeat((byte)0x21, 32).ToArray());
        AddEvent(AccountingEventKind.WalletReceived, deposit, 0, 101, 70_000_000);
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var found = await connection.WalletKitClient.GetTransactionAsync(
                        new GetTransactionRequest { Txid = deposit.ToString() }, cancellationToken: Ct);
        var missing = await Assert.ThrowsAsync<RpcException>(async () =>
            await connection.WalletKitClient.GetTransactionAsync(
                new GetTransactionRequest { Txid = new string('1', 64) }, cancellationToken: Ct));

        // Assert
        Assert.Equal(deposit.ToString(), found.TxHash);
        Assert.Equal(70_000, found.Amount);
        Assert.Equal(StatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Given_OurBroadcast_When_Labelled_Then_TheLabelIsKeptAndOnlyOverwrittenOnRequest()
    {
        // Arrange
        var send = AddBroadcast(BroadcastPurpose.WalletSend, 0x31, BroadcastState.Pending, null);
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        LabelTransactionRequest Label(string label, bool overwrite = false) => new()
        {
            Txid = ByteString.CopyFrom((byte[])send),
            Label = label,
            Overwrite = overwrite
        };

        // Act
        var first = await connection.WalletKitClient.LabelTransactionAsync(Label("rent"), cancellationToken: Ct);
        var again = await Assert.ThrowsAsync<RpcException>(async () =>
            await connection.WalletKitClient.LabelTransactionAsync(Label("food"), cancellationToken: Ct));
        await connection.WalletKitClient.LabelTransactionAsync(Label("food", true), cancellationToken: Ct);
        var empty = await Assert.ThrowsAsync<RpcException>(async () =>
            await connection.WalletKitClient.LabelTransactionAsync(Label(""), cancellationToken: Ct));
        var tooLong = await Assert.ThrowsAsync<RpcException>(async () =>
            await connection.WalletKitClient.LabelTransactionAsync(Label(new string('x', 300), true),
                                                                   cancellationToken: Ct));

        // Assert
        Assert.Equal("transaction label 'rent' added", first.Status);
        Assert.Equal(StatusCode.AlreadyExists, again.StatusCode);
        Assert.Equal("transaction already labelled", again.Status.Detail);
        Assert.Equal("food", _broadcastRows.Single(r => r.TransactionId == send).Label);
        Assert.Equal(StatusCode.InvalidArgument, empty.StatusCode);
        Assert.Equal(StatusCode.InvalidArgument, tooLong.StatusCode);
    }

    [Fact]
    public async Task Given_ADepositOrAnUnknownTransaction_When_Labelled_Then_RefusedAsLndWouldOrAsNotOurs()
    {
        // Arrange
        var deposit = new TxId(Enumerable.Repeat((byte)0x22, 32).ToArray());
        AddEvent(AccountingEventKind.WalletReceived, deposit, 0, 101, 70_000_000);
        using var connection = Connect(LndMacaroonFiles.AdminFileName);

        // Act
        var ofDeposit = await Assert.ThrowsAsync<RpcException>(async () =>
            await connection.WalletKitClient.LabelTransactionAsync(new LabelTransactionRequest
            {
                Txid = ByteString.CopyFrom((byte[])deposit),
                Label = "salary"
            }, cancellationToken: Ct));
        var unknown = await Assert.ThrowsAsync<RpcException>(async () =>
            await connection.WalletKitClient.LabelTransactionAsync(new LabelTransactionRequest
            {
                Txid = ByteString.CopyFrom(new byte[32]),
                Label = "salary"
            }, cancellationToken: Ct));

        // Assert
        Assert.Equal(StatusCode.FailedPrecondition, ofDeposit.StatusCode);
        Assert.Equal(StatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("cannot label transaction not known to wallet", unknown.Status.Detail);
    }

    [Fact]
    public async Task Given_PendingAndOtherBroadcasts_When_RemoveTransaction_Then_OnlyAnUnconfirmedWalletSendIsRemoved()
    {
        // Arrange
        var send = AddBroadcast(BroadcastPurpose.WalletSend, 0x41, BroadcastState.Pending, null);
        var confirmed = AddBroadcast(BroadcastPurpose.WalletSend, 0x42, BroadcastState.Confirmed, 140);
        var funding = AddBroadcast(BroadcastPurpose.Funding, 0x43, BroadcastState.Pending, null);
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        Task<RemoveTransactionResponse> Remove(TxId txId) =>
            connection.WalletKitClient.RemoveTransactionAsync(new GetTransactionRequest { Txid = txId.ToString() },
                                                              cancellationToken: Ct).ResponseAsync;

        // Act
        var removed = await Remove(send);
        var twice = await Assert.ThrowsAsync<RpcException>(() => Remove(send));
        var ofConfirmed = await Assert.ThrowsAsync<RpcException>(() => Remove(confirmed));
        var ofFunding = await Assert.ThrowsAsync<RpcException>(() => Remove(funding));

        // Assert
        Assert.Equal("Successfully removed transaction", removed.Status);
        Assert.Equal(BroadcastState.Abandoned, _broadcastRows.Single(r => r.TransactionId == send).State);
        Assert.Equal(StatusCode.NotFound, twice.StatusCode);
        Assert.Equal(StatusCode.FailedPrecondition, ofConfirmed.StatusCode);
        Assert.Equal(StatusCode.FailedPrecondition, ofFunding.StatusCode);
        Assert.Equal(BroadcastState.Pending, _broadcastRows.Single(r => r.TransactionId == funding).State);
    }

    [Fact]
    public async Task Given_TheAnchorsReserve_When_RequiredReserve_Then_ItCountsTheAdditionalChannels()
    {
        // Arrange
        _reserve.Setup(r => r.GetRequiredReserve(3)).Returns(LightningMoney.Satoshis(50_000));
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var response = await connection.WalletKitClient.RequiredReserveAsync(
                           new RequiredReserveRequest { AdditionalPublicChannels = 3 }, cancellationToken: Ct);

        // Assert
        Assert.Equal(50_000, response.RequiredReserve);
    }

    [Fact]
    public async Task Given_APackage_When_SubmitPackage_Then_BitcoindsAnswerComesBackWithTheMaxFeeRate()
    {
        // Arrange
        var parent = NBitcoin.Network.RegTest.CreateTransaction();
        parent.Inputs.Add(new NBitcoin.TxIn(new NBitcoin.OutPoint(NBitcoin.uint256.One, 0)));
        parent.Outputs.Add(NBitcoin.Money.Satoshis(10_000), new NBitcoin.Key().PubKey.WitHash);
        decimal? maxFeeRate = -1;
        _chain.Setup(c => c.SubmitRawPackageAsync(It.IsAny<IReadOnlyList<NBitcoin.Transaction>>(),
                                                  It.IsAny<decimal?>()))
              .Callback((IReadOnlyList<NBitcoin.Transaction> _, decimal? rate) => maxFeeRate = rate)
              .ReturnsAsync(new RawPackageSubmitResult(
                                "success", [new RawPackageTransactionResult("aa", "bb", null, null)], ["cc"]));
        using var connection = Connect(LndMacaroonFiles.AdminFileName);

        // Act
        var response = await connection.WalletKitClient.SubmitPackageAsync(new SubmitPackageRequest
        {
            RawTxs = { ByteString.CopyFrom(NBitcoin.BitcoinSerializableExtensions.ToBytes(parent)) },
            SatPerVbyte = 20
        }, cancellationToken: Ct);
        var empty = await Assert.ThrowsAsync<RpcException>(async () =>
            await connection.WalletKitClient.SubmitPackageAsync(new SubmitPackageRequest(), cancellationToken: Ct));

        // Assert: 20 sat/vB = 0.0002 BTC/kvB
        Assert.Equal(0.0002m, maxFeeRate);
        Assert.Equal("success", response.PackageMsg);
        Assert.Equal("bb", response.TxResults["aa"].Txid);
        Assert.Equal(["cc"], response.ReplacedTransactions);
        Assert.Equal(StatusCode.InvalidArgument, empty.StatusCode);
    }

    [Fact]
    public async Task Given_AWalletAddress_When_SignMessageWithAddr_Then_TheSignerSignsForThatAddress()
    {
        // Arrange
        var address = new NBitcoin.Key().PubKey.GetAddress(NBitcoin.ScriptPubKeyType.Segwit, NBitcoin.Network.RegTest)
                                      .ToString();
        _walletAddresses.Add(new WalletAddressModel(AddressType.P2Wpkh, 5, false, address));
        _signer.Setup(s => s.SignWalletMessage(It.Is<WalletAddressModel>(a => a.Index == 5), It.IsAny<byte[]>()))
               .Returns(Enumerable.Repeat((byte)9, 65).ToArray());
        using var connection = Connect(LndMacaroonFiles.AdminFileName);

        // Act
        var signed = await connection.WalletKitClient.SignMessageWithAddrAsync(new SignMessageWithAddrRequest
        {
            Addr = address,
            Msg = ByteString.CopyFromUtf8("hi")
        }, cancellationToken: Ct);
        var foreign = await Assert.ThrowsAsync<RpcException>(async () =>
            await connection.WalletKitClient.SignMessageWithAddrAsync(new SignMessageWithAddrRequest
            {
                Addr = new NBitcoin.Key().PubKey.GetAddress(NBitcoin.ScriptPubKeyType.Segwit,
                                                            NBitcoin.Network.RegTest).ToString(),
                Msg = ByteString.CopyFromUtf8("hi")
            }, cancellationToken: Ct));

        // Assert
        Assert.Equal(Convert.ToBase64String(Enumerable.Repeat((byte)9, 65).ToArray()), signed.Signature);
        Assert.Equal(StatusCode.NotFound, foreign.StatusCode);
    }

    [Theory]
    [InlineData(NBitcoin.ScriptPubKeyType.Segwit)]
    [InlineData(NBitcoin.ScriptPubKeyType.TaprootBIP86)]
    [InlineData(NBitcoin.ScriptPubKeyType.SegwitP2SH)]
    [InlineData(NBitcoin.ScriptPubKeyType.Legacy)]
    public async Task Given_ASignatureOfAnyAddressType_When_VerifyMessageWithAddr_Then_ValidAndItsKey(
        NBitcoin.ScriptPubKeyType type)
    {
        // Arrange
        var key = new NBitcoin.Key();
        var message = "verify me"u8.ToArray();
        var compact = key.SignCompact(new NBitcoin.uint256(BitcoinMessageSignature.Digest(message)), false);
        byte[] signature = [(byte)(31 + compact.RecoveryId), .. compact.Signature];
        var address = key.PubKey.GetAddress(type, NBitcoin.Network.RegTest).ToString();
        using var connection = Connect(LndMacaroonFiles.AdminFileName);

        // Act
        var response = await connection.WalletKitClient.VerifyMessageWithAddrAsync(new VerifyMessageWithAddrRequest
        {
            Addr = address,
            Msg = ByteString.CopyFrom(message),
            Signature = Convert.ToBase64String(signature)
        }, cancellationToken: Ct);
        var tampered = await connection.WalletKitClient.VerifyMessageWithAddrAsync(new VerifyMessageWithAddrRequest
        {
            Addr = address,
            Msg = ByteString.CopyFromUtf8("other"),
            Signature = Convert.ToBase64String(signature)
        }, cancellationToken: Ct);

        // Assert
        Assert.True(response.Valid);
        Assert.Equal(key.PubKey.ToBytes(), response.Pubkey.ToByteArray());
        Assert.False(tampered.Valid);
    }

    [Fact]
    public async Task Given_AFundPsbtWithLndsOptions_When_Called_Then_P2TrChangeAndFeeRatioReachTheWallet()
    {
        // Arrange
        PsbtFundRequest? captured = null;
        _psbt.Setup(x => x.FundPsbtAsync(It.IsAny<PsbtFundRequest>(), It.IsAny<CancellationToken>()))
             .Callback((PsbtFundRequest r, CancellationToken _) => captured = r)
             .ReturnsAsync(new PsbtFundResult([1], -1, [], LightningMoney.Zero));
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        var request = new FundPsbtRequest
        {
            Raw = new TxTemplate(),
            SatPerVbyte = 2,
            ChangeType = ChangeAddressType.P2Tr,
            MaxFeeRatio = 0.1,
            CoinSelectionStrategy = Testing.Lnd.Lnrpc.CoinSelectionStrategy.StrategyLargest
        };
        request.Raw.Outputs["bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080"] = 20_000;

        // Act
        await connection.WalletKitClient.FundPsbtAsync(request, cancellationToken: Ct);
        request.CoinSelectionStrategy = Testing.Lnd.Lnrpc.CoinSelectionStrategy.StrategyRandom;
        var random = await Assert.ThrowsAsync<RpcException>(async () =>
            await connection.WalletKitClient.FundPsbtAsync(request, cancellationToken: Ct));

        // Assert
        Assert.NotNull(captured);
        Assert.Equal(AddressType.P2Tr, captured.ChangeAddressType);
        Assert.Equal(0.1, captured.MaxFeeRatio);
        Assert.Equal(StatusCode.Unimplemented, random.StatusCode);
    }

    [Fact]
    public async Task Given_AnUnknownChannelPoint_When_BumpForceCloseFee_Then_NotFoundAndNothingRequested()
    {
        // Arrange
        var fundingTxId = new TxId(Enumerable.Repeat((byte)0x62, 32).ToArray());
        _channels.Setup(c => c.FindChannels(It.IsAny<Func<Domain.Channels.Models.ChannelModel, bool>>()))
                 .Returns([]);
        using var connection = Connect(LndMacaroonFiles.AdminFileName);

        // Act
        var unknown = await Assert.ThrowsAsync<RpcException>(async () =>
            await connection.WalletKitClient.BumpForceCloseFeeAsync(new BumpForceCloseFeeRequest
            {
                ChanPoint = new Testing.Lnd.Lnrpc.ChannelPoint
                {
                    FundingTxidBytes = ByteString.CopyFrom((byte[])fundingTxId),
                    OutputIndex = 0
                },
                StartingFeerate = 10
            }, cancellationToken: Ct));

        // Assert
        Assert.Equal(StatusCode.NotFound, unknown.StatusCode);
        _anchors.Verify(a => a.RequestBumpAsync(It.IsAny<ChannelId>(), It.IsAny<OperatorFeeBumpRequest>(),
                                                It.IsAny<(TxId, uint)?>(), It.IsAny<CancellationToken>()),
                        Times.Never);
    }

    [Fact]
    public async Task Given_OurCommitmentsAnchor_When_BumpFee_Then_TheAnchorServiceGetsTheOutpointAndParameters()
    {
        // Arrange
        var commitment = AddBroadcast(BroadcastPurpose.LocalCommitment, 0x71, BroadcastState.Pending, null);
        (TxId, uint)? anchor = null;
        OperatorFeeBumpRequest? captured = null;
        _anchors.Setup(a => a.RequestBumpAsync(It.IsAny<ChannelId>(), It.IsAny<OperatorFeeBumpRequest>(),
                                               It.IsAny<(TxId, uint)?>(), It.IsAny<CancellationToken>()))
                .Callback((ChannelId _, OperatorFeeBumpRequest r, (TxId, uint)? a, CancellationToken _) =>
                          {
                              captured = r;
                              anchor = a;
                          })
                .ReturnsAsync(AnchorBumpOutcome.Registered);
        using var connection = Connect(LndMacaroonFiles.AdminFileName);

        // Act
        var response = await connection.WalletKitClient.BumpFeeAsync(new BumpFeeRequest
        {
            Outpoint = new Testing.Lnd.Lnrpc.OutPoint { TxidBytes = ByteString.CopyFrom((byte[])commitment), OutputIndex = 2 },
            SatPerVbyte = 30,
            Budget = 25_000,
            DeadlineDelta = 10,
            Immediate = true
        }, cancellationToken: Ct);

        // Assert: 30 sat/vB = 7,500 sat/kw; the deadline counts from the tip (150)
        Assert.Equal("Successfully registered CPFP-tx with the sweeper", response.Status);
        Assert.Equal((commitment, 2u), anchor);
        Assert.Equal(new OperatorFeeBumpRequest(7_500, 25_000, 160, null, true), captured);
    }

    [Fact]
    public async Task Given_ASweptOutput_When_BumpFee_Then_ItsRequestIsStoredAndTheRoundRunsNow()
    {
        // Arrange
        var commitment = new TxId(Enumerable.Repeat((byte)0xC1, 32).ToArray());
        var sweep = AddBroadcast(BroadcastPurpose.Sweep, 0x72, BroadcastState.Pending, null);
        AddOutput(commitment, 0, OutputDescriptorKind.DelayedToLocal, OutputResolutionState.Broadcast,
                  waitUntil: null, deadline: null, resolving: sweep);
        AddOutput(commitment, 1, OutputDescriptorKind.LocalOfferedHtlc, OutputResolutionState.Broadcast,
                  waitUntil: null, deadline: null);
        using var connection = Connect(LndMacaroonFiles.AdminFileName);

        // Act
        var response = await connection.WalletKitClient.BumpFeeAsync(new BumpFeeRequest
        {
            Outpoint = new Testing.Lnd.Lnrpc.OutPoint { TxidStr = commitment.ToString(), OutputIndex = 0 },
            SatPerVbyte = 12,
            Immediate = true
        }, cancellationToken: Ct);
        var htlc = await Assert.ThrowsAsync<RpcException>(async () =>
            await connection.WalletKitClient.BumpFeeAsync(new BumpFeeRequest
            {
                Outpoint = new Testing.Lnd.Lnrpc.OutPoint { TxidStr = commitment.ToString(), OutputIndex = 1 }
            }, cancellationToken: Ct));
        var nowhere = await Assert.ThrowsAsync<RpcException>(async () =>
            await connection.WalletKitClient.BumpFeeAsync(new BumpFeeRequest
            {
                Outpoint = new Testing.Lnd.Lnrpc.OutPoint { TxidStr = commitment.ToString(), OutputIndex = 9 }
            }, cancellationToken: Ct));

        // Assert
        Assert.Equal("Successfully registered rbf-tx with sweeper", response.Status);
        Assert.Equal(3_000u, _bumps.GetOutput(commitment, 0, out var fresh)?.StartingFeeratePerKw);
        Assert.True(fresh);
        _executor.Verify(e => e.ResolveChannelAsync(s_closedChannel, 150, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(StatusCode.FailedPrecondition, htlc.StatusCode);
        Assert.Equal(StatusCode.NotFound, nowhere.StatusCode);
    }

    [Fact]
    public async Task Given_PublicKeys_When_ImportPublicKey_Then_EachAddressTypesScriptIsWatched()
    {
        // Arrange
        var key = new NBitcoin.Key().PubKey;
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        Task<ImportPublicKeyResponse> Import(byte[] publicKey, Testing.Lnd.Walletrpc.AddressType type) =>
            connection.WalletKitClient.ImportPublicKeyAsync(new ImportPublicKeyRequest
            {
                PublicKey = ByteString.CopyFrom(publicKey),
                AddressType = type
            }, cancellationToken: Ct).ResponseAsync;

        // Act
        var p2wpkh = await Import(key.ToBytes(), Testing.Lnd.Walletrpc.AddressType.WitnessPubkeyHash);
        await Import(key.ToBytes(), Testing.Lnd.Walletrpc.AddressType.NestedWitnessPubkeyHash);
        await Import(key.TaprootInternalKey.ToBytes(), Testing.Lnd.Walletrpc.AddressType.TaprootPubkey);
        await Import(key.ToBytes(), Testing.Lnd.Walletrpc.AddressType.WitnessPubkeyHash);
        var shortTaproot = await Assert.ThrowsAsync<RpcException>(() => Import(
                                                                       key.ToBytes(),
                                                                       Testing.Lnd.Walletrpc.AddressType.TaprootPubkey));
        var unknown = await Assert.ThrowsAsync<RpcException>(() => Import(
                                                                  key.ToBytes(),
                                                                  Testing.Lnd.Walletrpc.AddressType.Unknown));

        // Assert: three scripts, the repeated import changed nothing
        Assert.Equal($"public key {Convert.ToHexStringLower(key.ToBytes())} imported", p2wpkh.Status);
        Assert.Equal([key.WitHash.ScriptPubKey.ToBytes(), key.WitHash.ScriptPubKey.Hash.ScriptPubKey.ToBytes(),
                      key.GetTaprootFullPubKey().ScriptPubKey.ToBytes()],
                     _imported.Select(i => i.Script));
        Assert.All(_imported, i => Assert.Equal(32, i.InternalKey.Length));
        Assert.Equal(StatusCode.InvalidArgument, shortTaproot.StatusCode);
        Assert.Equal(StatusCode.InvalidArgument, unknown.StatusCode);
    }
}