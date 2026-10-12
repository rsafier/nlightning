using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using Newtonsoft.Json.Linq;
using NLightning.Testing.Cluster.Nodes.BitcoinCore;
using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Lnrpc;
using NLightning.Testing.Lnd.Routerrpc;
using NLightning.Testing.Lnd.Walletrpc;

namespace NLightning.Integration.Tests.Docker;

using Abcd;
using Application.Bitcoin.WalletHistory;
using Domain.Accounting.Constants;
using Domain.Accounting.Labels;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.SilentPayments.Interfaces;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Options;
using Infrastructure.Bitcoin.Wallet;
using LndGrpc;
using LndGrpc.Macaroons;
using Utils;
using AddressType = Domain.Bitcoin.Enums.AddressType;
using Hash = Domain.Crypto.ValueObjects.Hash;
using OutPoint = NBitcoin.OutPoint;
using Secret = Domain.Crypto.ValueObjects.Secret;
using Transaction = NBitcoin.Transaction;

public partial class LndGrpcWave3FlowTests
{
    [Fact]
    public async Task Given_RealCoreAndLnd_When_DanglingFixesAreUsed_Then_WireAndRecoveryProofsAgree()
    {
        var ct = TestContext.Current.CancellationToken;
        // One network and channel setup also proves the existing five real subscription feeds.
        await Given_OurLndGrpc_When_AnLndClientAcceptsInterceptsAndSpends_Then_LndAndOurNodeAgree();
        // The subscription lifecycle proof closes its public payee channel. New forwards need a fresh live route.
        await ReopenPayeeChannelAsync(ct);
        await ResumeModifiedFlowAsync(ct);
        await ExactSilentPaymentInputFlowAsync(ct);
        await NamedAccountUnconfirmedPackageFlowAsync(ct);
        await HistoryRestartFlowAsync(ct);
        await PrunedHistoryRefusalFlowAsync(ct);
        await OnchainInterceptorRaceFlowAsync(ct);
        Console.WriteLine("dangling fixes: modified forwarding, exact silent-payment input, bounded history restart proved");
    }

    private async Task ReopenPayeeChannelAsync(CancellationToken ct)
    {
        var channel = await Node.OpenChannelAsync(new Domain.Client.Requests.OpenChannelClientRequest(Payee.Address,
            LightningMoney.Satoshis(1_000_000))
        { FeeRatePerKw = LightningMoney.Satoshis(10_000), IsPublic = true }, ct);
        var alice = _fixture.GetLndNode("alice");
        await Poll.UntilAsync(async () =>
        {
            if ((await Node.GetChannelAsync(channel.ChannelId, ct)).IsUsable()
                && (await Payee.GetChannelAsync(channel.ChannelId, ct)) is { ShortChannelId: not null } theirs
                && theirs.IsUsable()
                && Payee.Services.GetRequiredService<Application.Gossip.Interfaces.IChannelUpdateService>()
                    .TryGetRemoteChannelUpdate(channel.ChannelId, out _)) return true;
            await ChainSync.MineAndWaitAsync(_fixture, 1, [alice], _nodes, ct);
            return false;
        }, s_timeout, "fresh post-subscription payee channel usable with forwarding policy", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [alice], _nodes, ct);
    }

    private async Task ExactSilentPaymentInputFlowAsync(CancellationToken ct)
    {
        var memory = Node.Services.GetRequiredService<IUtxoMemoryRepository>();
        await Node.FundWalletAsync(LightningMoney.Satoshis(40_000), AddressType.P2Wpkh, ct);
        var selected = Assert.Single(memory.GetUnreservedUtxos(), coin => coin.Amount == LightningMoney.Satoshis(40_000));
        await Node.FundWalletAsync(LightningMoney.Satoshis(800_000), AddressType.P2Wpkh, ct);
        var untouched = Assert.Single(memory.GetUnreservedUtxos(), coin => coin.Amount == LightningMoney.Satoshis(800_000));
        var destination = await Payee.Services.GetRequiredService<ISilentPaymentService>().GetAddressAsync(cancellationToken: ct);
        var result = await Node.Services.GetRequiredService<IWalletSpendService>().WithdrawAsync(
            new WalletWithdrawRequest(destination.Address, null, LightningMoney.Satoshis(2_500))
            {
                Inputs = [(selected.TxId, selected.Index)]
            }, ct);
        Assert.True(result.Published);
        Assert.Equal(1, result.InputCount);
        Assert.Equal(LightningMoney.Zero, result.Change);
        var wire = await _fixture.Bitcoin.GetRawTransactionAsync(new uint256((byte[])result.TxId), false, ct);
        var input = Assert.Single(wire.Inputs);
        Assert.Equal(new OutPoint(new uint256((byte[])selected.TxId), selected.Index), input.PrevOut);
        var output = Assert.Single(wire.Outputs);
        Assert.Equal(34, output.ScriptPubKey.ToBytes().Length);
        Assert.Equal(new byte[] { 0x51, 0x20 }, output.ScriptPubKey.ToBytes()[..2]);
        Assert.Equal(40_000L, output.Value.Satoshi + (long)result.Fee.Satoshi);
        Assert.True(memory.TryGetUtxo(untouched.TxId, untouched.Index, out _));
        await ChainSync.MineAndWaitAsync(_fixture, 4, [_fixture.GetLndNode("alice")], _nodes, ct);
        var received = Assert.Single(Payee.Services.GetRequiredService<IUtxoMemoryRepository>().GetUnreservedUtxos(),
            coin => coin.TxId == result.TxId);
        Assert.NotNull(received.SilentPayment);
        Assert.Equal(result.Amount, received.Amount);
        Console.WriteLine($"exact silent-payment input: {result.TxId}, chosen 40k, untouched 800k, fee {result.Fee}");
    }

    private async Task ResumeModifiedFlowAsync(CancellationToken ct)
    {
        var alice = _fixture.GetLndNode("alice");
        using var ours = LndNodeConnection.CreateWithoutNodeInfo(Settings(LndMacaroonFiles.AdminFileName));
        var channels = await alice.LightningClient.ListChannelsAsync(new ListChannelsRequest
        {
            ActiveOnly = true,
            Peer = ByteString.CopyFrom(Convert.FromHexString(Node.NodeIdHex))
        }, cancellationToken: ct);
        var incoming = Assert.Single(channels.Channels, channel => channel.Initiator);
        var hub = Node.Services.GetRequiredService<Application.Payments.Interception.HtlcInterceptorHub>();
        // Install requireinterceptor through the real RPC's settings, then disconnect it.
        Node.Services.GetRequiredService<IOptions<LndGrpcOptions>>().Value.RequireInterceptor = true;
        using (var warmupLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            using var warmup = ours.RouterClient.HtlcInterceptor(cancellationToken: warmupLifetime.Token);
            await Poll.UntilAsync(() => Task.FromResult(hub.IsActive), s_timeout, "required interceptor configured", ct);
            warmupLifetime.Cancel();
            await Poll.UntilAsync(() => Task.FromResult(!hub.IsActive), s_timeout, "required interceptor disconnected", ct);
        }
        Assert.True(hub.IsRequired);
        var refusedInvoice = await Payee.CreateInvoiceAsync(LightningMoney.Satoshis(PaymentSat), "required client absent", ct);
        await LndTestHelpers.ResetMissionControlAsync(alice, ct);
        var refused = await LndTestHelpers.SendPaymentV2Async(alice,
            LndTestHelpers.PinnedPayment(refusedInvoice.Bolt11!, [incoming.ChanId]), ct);
        Assert.Equal(Payment.Types.PaymentStatus.Failed, refused.Status);
        Assert.Contains(refused.Htlcs, attempt => attempt.Failure is
        { Code: Failure.Types.FailureCode.TemporaryChannelFailure, FailureSourceIndex: 1 });
        Assert.Equal(0, hub.HeldCount);
        var invoice = await Payee.CreateInvoiceAsync(LightningMoney.Satoshis(PaymentSat), "resume modified required client", ct);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var interceptor = ours.RouterClient.HtlcInterceptor(cancellationToken: lifetime.Token);
        var observed = new TaskCompletionSource<ForwardHtlcInterceptRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responder = Task.Run(async () =>
        {
            Assert.True(await interceptor.ResponseStream.MoveNext(lifetime.Token));
            var request = interceptor.ResponseStream.Current;
            var response = new ForwardHtlcInterceptResponse
            {
                IncomingCircuitKey = request.IncomingCircuitKey,
                Action = ResolveHoldForwardAction.ResumeModified,
                InAmountMsat = request.IncomingAmountMsat + 1,
                OutAmountMsat = request.OutgoingAmountMsat + 1
            };
            response.OutWireCustomRecords.Add(65537, ByteString.CopyFrom(new byte[] { 0xCA, 0xFE }));
            await interceptor.RequestStream.WriteAsync(response, lifetime.Token);
            observed.TrySetResult(request);
        }, CancellationToken.None);
        await Poll.UntilAsync(() => Task.FromResult(hub.IsActive), s_timeout, "modified interceptor active", ct);
        await LndTestHelpers.ResetMissionControlAsync(alice, ct);
        var payment = await LndTestHelpers.SendPaymentV2Async(alice,
            LndTestHelpers.PinnedPayment(invoice.Bolt11!, [incoming.ChanId]), ct);
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, payment.Status);
        var offered = await observed.Task.WaitAsync(s_timeout, ct);
        await responder;
        await using var scope = Node.Services.CreateAsyncScope();
        var forwards = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ForwardCircuitDbRepository
            .ListAsync(new ForwardCircuitListQuery(0, 100), ct);
        var circuit = Assert.Single(forwards, forward => forward.PaymentHash == invoice.PaymentHash);
        Assert.Equal(LightningMoney.MilliSatoshis(offered.IncomingAmountMsat + 1), circuit.IncomingAmount);
        Assert.Equal(LightningMoney.MilliSatoshis(offered.OutgoingAmountMsat + 1), circuit.OutgoingAmount);
        Assert.Equal(LightningMoney.MilliSatoshis(offered.IncomingAmountMsat), circuit.ActualIncomingAmount);
        var fact = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingEventDbRepository.GetByKeyAsync(
            AccountingEventKeys.ForwardSettled(circuit.IncomingChannelId, circuit.IncomingHtlcId), ct);
        Assert.NotNull(fact);
        Assert.Equal((long)(offered.IncomingAmountMsat - offered.OutgoingAmountMsat - 1), fact.AmountMsat);
        lifetime.Cancel();
        await Poll.UntilAsync(() => Task.FromResult(!hub.IsActive), s_timeout, "modified interceptor disconnected", ct);
        Console.WriteLine("RESUME_MODIFIED: required-client refusal then payment settled with overridden amounts and odd wire record");
    }

    private async Task NamedAccountUnconfirmedPackageFlowAsync(CancellationToken ct)
    {
        using var ours = LndNodeConnection.CreateWithoutNodeInfo(Settings(LndMacaroonFiles.AdminFileName));
        const string account = "dangling";
        await ours.WalletKitClient.XCreateAccountAsync(new XCreateAccountRequest
        {
            Name = account,
            AddressType = NLightning.Testing.Lnd.Walletrpc.AddressType.TaprootPubkey,
            IKnowWhatIAmDoing = true
        }, cancellationToken: ct);
        var address = await ours.WalletKitClient.NextAddrAsync(new AddrRequest
        {
            Account = account,
            Type = NLightning.Testing.Lnd.Walletrpc.AddressType.TaprootPubkey
        }, cancellationToken: ct);
        var changeAddress = await ours.WalletKitClient.NextAddrAsync(new AddrRequest
        {
            Account = account,
            Type = NLightning.Testing.Lnd.Walletrpc.AddressType.TaprootPubkey,
            Change = true
        }, cancellationToken: ct);
        var alice = _fixture.GetLndNode("alice");
        var external = await alice.LightningClient.NewAddressAsync(new NewAddressRequest
        {
            Type = NLightning.Testing.Lnd.Lnrpc.AddressType.WitnessPubkeyHash
        }, cancellationToken: ct);
        var memory = Node.Services.GetRequiredService<IUtxoMemoryRepository>();
        var custody = memory.GetUnreservedUtxos().Select(coin => (coin.TxId, coin.Index)).ToHashSet();
        var parent = await _fixture.Bitcoin.SendToAddressAsync(BitcoinAddress.Create(address.Addr, Network.RegTest),
            Money.Satoshis(100_000), ct);
        var parentWire = await _fixture.Bitcoin.GetRawTransactionAsync(parent, false, ct);
        var parentIndex = (uint)Assert.Single(parentWire.Outputs.Select((output, index) => (output, index)),
            pair => pair.output.ScriptPubKey == BitcoinAddress.Create(address.Addr, Network.RegTest).ScriptPubKey).index;
        var template = Network.RegTest.CreateTransaction();
        template.Outputs.Add(Money.Satoshis(20_000), BitcoinAddress.Create(external.Address, Network.RegTest));
        template.Outputs.Add(Money.Zero, BitcoinAddress.Create(changeAddress.Addr, Network.RegTest));
        // Core supplies the standard inputless BIP174 template without NBitcoin's
        // transaction clone treating its zero-input encoding as a witness marker.
        var templateResponse = await _fixture.Bitcoin.SendCommandAsync("createpsbt", ct,
            Array.Empty<object>(), new JArray
            {
                new JObject { [external.Address] = 0.0002m },
                new JObject { [changeAddress.Addr] = 0m }
            });
        var templateBytes = Convert.FromBase64String(templateResponse.Result.Value<string>()!);
        var funded = await ours.WalletKitClient.FundPsbtAsync(new FundPsbtRequest
        {
            Account = account,
            MinConfs = 0,
            SpendUnconfirmed = true,
            SatPerVbyte = 2,
            CoinSelect = new PsbtCoinSelect { Psbt = ByteString.CopyFrom(templateBytes), ExistingOutputIndex = 1 }
        }, cancellationToken: ct);
        Assert.Single(funded.LockedUtxos);
        Assert.Equal(1, funded.ChangeOutputIndex);
        var fundedPacket = PSBT.Load(funded.FundedPsbt.ToByteArray(), Network.RegTest);
        Assert.Equal(new OutPoint(parent, parentIndex), Assert.Single(fundedPacket.GetGlobalTransaction().Inputs).PrevOut);
        Assert.Equal(template.Outputs[0].ScriptPubKey, fundedPacket.GetGlobalTransaction().Outputs[0].ScriptPubKey);
        Assert.Equal(template.Outputs[1].ScriptPubKey, fundedPacket.GetGlobalTransaction().Outputs[1].ScriptPubKey);
        var finalized = await ours.WalletKitClient.FinalizePsbtAsync(new FinalizePsbtRequest
        {
            FundedPsbt = funded.FundedPsbt,
            Account = account
        }, cancellationToken: ct);
        var child = Transaction.Load(finalized.RawFinalTx.ToByteArray(), Network.RegTest);
        var publication = await ours.WalletKitClient.PublishTransactionAsync(new NLightning.Testing.Lnd.Walletrpc.Transaction
        {
            TxHex = finalized.RawFinalTx
        }, cancellationToken: ct);
        Assert.True(string.IsNullOrEmpty(publication.PublishError), publication.PublishError);
        await ours.WalletKitClient.LabelTransactionAsync(new LabelTransactionRequest
        {
            Txid = ByteString.CopyFrom(child.GetHash().ToBytes()),
            Label = "dangling package"
        }, cancellationToken: ct);
        await Assert.ThrowsAsync<RpcException>(async () => await ours.WalletKitClient.LabelTransactionAsync(
            new LabelTransactionRequest { Txid = ByteString.CopyFrom(child.GetHash().ToBytes()), Label = "refused overwrite" },
            cancellationToken: ct));
        await ours.WalletKitClient.LabelTransactionAsync(new LabelTransactionRequest
        {
            Txid = ByteString.CopyFrom(child.GetHash().ToBytes()),
            Label = "dangling overwrite",
            Overwrite = true
        }, cancellationToken: ct);
        var mempoolBefore = (await _fixture.Bitcoin.GetRawMempoolAsync(ct)).ToHashSet();
        await ours.WalletKitClient.BumpFeeAsync(new BumpFeeRequest
        {
            Outpoint = new NLightning.Testing.Lnd.Lnrpc.OutPoint
            {
                TxidBytes = ByteString.CopyFrom(child.GetHash().ToBytes()),
                OutputIndex = 1
            },
            SatPerVbyte = 10,
            Immediate = true,
            Budget = 20_000
        }, cancellationToken: ct);
        var grandchildId = Assert.Single(await _fixture.Bitcoin.GetRawMempoolAsync(ct), id => !mempoolBefore.Contains(id));
        var grandchild = await _fixture.Bitcoin.GetRawTransactionAsync(grandchildId, false, ct);
        Assert.Equal(new OutPoint(child.GetHash(), 1), Assert.Single(grandchild.Inputs).PrevOut);
        var entry = (await _fixture.Bitcoin.SendCommandAsync("getmempoolentry", grandchildId.ToString()).WaitAsync(ct)).Result;
        var ancestorFees = entry["fees"]!["ancestor"]!.Value<decimal>() * 100_000_000m;
        var ancestorVsize = entry["ancestorsize"]!.Value<long>();
        Assert.True(entry["ancestorcount"]!.Value<long>() >= 3, "CPFP must account for both unconfirmed ancestors");
        Assert.True(ancestorFees >= 10 * ancestorVsize, $"package fee {ancestorFees} must meet 10 sat/vB × {ancestorVsize}");
        var leasesBeforeReplacement = await ours.WalletKitClient.ListLeasesAsync(new ListLeasesRequest(), cancellationToken: ct);
        await ours.WalletKitClient.BumpFeeAsync(new BumpFeeRequest
        {
            Outpoint = new NLightning.Testing.Lnd.Lnrpc.OutPoint
            {
                TxidBytes = ByteString.CopyFrom(child.GetHash().ToBytes()),
                OutputIndex = 1
            },
            SatPerVbyte = 20,
            Immediate = true,
            Budget = 20_000
        }, cancellationToken: ct);
        var mempoolAfterReplacement = await _fixture.Bitcoin.GetRawMempoolAsync(ct);
        Assert.DoesNotContain(grandchildId, mempoolAfterReplacement);
        var replacementId = Assert.Single(mempoolAfterReplacement, id => !mempoolBefore.Contains(id));
        var replacement = await _fixture.Bitcoin.GetRawTransactionAsync(replacementId, false, ct);
        Assert.Equal(new OutPoint(child.GetHash(), 1), Assert.Single(replacement.Inputs).PrevOut);
        Assert.Equal(Assert.Single(grandchild.Outputs).ScriptPubKey, Assert.Single(replacement.Outputs).ScriptPubKey);
        var replacementEntry = (await _fixture.Bitcoin.SendCommandAsync("getmempoolentry", replacementId.ToString()).WaitAsync(ct)).Result;
        Assert.True(replacementEntry["fees"]!["ancestor"]!.Value<decimal>() * 100_000_000m >=
            20 * replacementEntry["ancestorsize"]!.Value<long>(), "replacement must meet the actual 20 sat/vB ancestor-package target");
        var leasesAfterReplacement = await ours.WalletKitClient.ListLeasesAsync(new ListLeasesRequest(), cancellationToken: ct);
        Assert.Equal(leasesBeforeReplacement.LockedUtxos.Select(lease => lease.Outpoint.ToString()).Order(),
            leasesAfterReplacement.LockedUtxos.Select(lease => lease.Outpoint.ToString()).Order());
        Assert.True(custody.SetEquals(memory.GetUnreservedUtxos().Select(coin => (coin.TxId, coin.Index))),
            "unconfirmed account funds must not enter confirmed custody");
        await ChainSync.MineAndWaitAsync(_fixture, 4, [alice], _nodes, ct);
        var history = await ours.LightningClient.GetTransactionsAsync(new GetTransactionsRequest(), cancellationToken: ct);
        Assert.Equal("dangling overwrite", Assert.Single(history.Transactions, transaction => transaction.TxHash == child.GetHash().ToString()).Label);
        Console.WriteLine("WalletKit: isolated named account, existing-change template, unconfirmed signing, two-ancestor CPFP replacement and durable label proved");
    }

    private async Task OnchainInterceptorRaceFlowAsync(CancellationToken ct)
    {
        var alice = _fixture.GetLndNode("alice");
        using var ours = LndNodeConnection.CreateWithoutNodeInfo(Settings(LndMacaroonFiles.AdminFileName));
        var channels = await alice.LightningClient.ListChannelsAsync(new ListChannelsRequest
        {
            ActiveOnly = true,
            Peer = ByteString.CopyFrom(Convert.FromHexString(Node.NodeIdHex))
        }, cancellationToken: ct);
        var incoming = Assert.Single(channels.Channels, channel => channel.Initiator);
        var preimage = new Secret(RandomNumberGenerator.GetBytes(32));
        var hash = new Hash(SHA256.HashData((byte[])preimage));
        var invoice = await Payee.Services.GetRequiredService<IInvoiceService>().CreateHoldInvoiceAsync(hash,
            LightningMoney.Satoshis(PaymentSat), "onchain existing outgoing race", null, null, SourceLabels.None, ct);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var interceptor = ours.RouterClient.HtlcInterceptor(cancellationToken: lifetime.Token);
        var hub = Node.Services.GetRequiredService<Application.Payments.Interception.HtlcInterceptorHub>();
        await Poll.UntilAsync(() => Task.FromResult(hub.IsActive), s_timeout, "onchain interceptor active", ct);
        await LndTestHelpers.ResetMissionControlAsync(alice, ct);
        var request = LndTestHelpers.PinnedPayment(invoice.Bolt11!, [incoming.ChanId]);
        request.TimeoutSeconds = 120;
        var paying = LndTestHelpers.SendPaymentV2Async(alice, request, lifetime.Token, TimeSpan.FromMinutes(3));
        Assert.True(await interceptor.ResponseStream.MoveNext(ct).WaitAsync(s_timeout, ct));
        var first = interceptor.ResponseStream.Current;
        await interceptor.RequestStream.WriteAsync(new ForwardHtlcInterceptResponse
        {
            IncomingCircuitKey = first.IncomingCircuitKey,
            Action = ResolveHoldForwardAction.Resume
        }, ct);
        await Poll.UntilAsync(async () => (await Payee.Services.GetRequiredService<IInvoiceService>().GetInvoiceAsync(hash, ct))
            is { Status: InvoiceStatus.Held }, s_timeout, "downstream hold invoice has the offered outgoing leg", ct);
        var point = incoming.ChannelPoint.Split(':');
        using var closing = alice.LightningClient.CloseChannel(new CloseChannelRequest
        {
            ChannelPoint = new ChannelPoint { FundingTxidStr = point[0], OutputIndex = uint.Parse(point[1]) },
            Force = true
        }, cancellationToken: ct);
        Assert.True(await closing.ResponseStream.MoveNext(ct).WaitAsync(s_timeout, ct));
        Assert.NotNull(closing.ResponseStream.Current.ClosePending);
        await ChainSync.MineAndWaitAsync(_fixture, 1, [alice], _nodes, ct);
        Assert.True(await interceptor.ResponseStream.MoveNext(ct).WaitAsync(s_timeout, ct));
        var onchain = interceptor.ResponseStream.Current;
        Assert.Equal(first.IncomingCircuitKey, onchain.IncomingCircuitKey);
        Assert.Equal((int)onchain.IncomingExpiry, onchain.AutoFailHeight);
        await interceptor.RequestStream.WriteAsync(new ForwardHtlcInterceptResponse
        {
            IncomingCircuitKey = onchain.IncomingCircuitKey,
            Action = ResolveHoldForwardAction.Settle,
            Preimage = ByteString.CopyFrom((byte[])preimage)
        }, ct);
        await Poll.UntilAsync(async () =>
        {
            await using var scope = Node.Services.CreateAsyncScope();
            var circuits = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ForwardCircuitDbRepository
                .ListAsync(new ForwardCircuitListQuery(0, 100), ct);
            return circuits.Single(circuit => circuit.PaymentHash == hash).IncomingClaimedPreimage == preimage;
        }, s_timeout, "onchain interceptor claim persisted on the existing circuit", ct);
        await using (var before = Node.Services.CreateAsyncScope())
        {
            var work = before.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var circuit = (await work.ForwardCircuitDbRepository.ListAsync(new ForwardCircuitListQuery(0, 100), ct))
                .Single(circuit => circuit.PaymentHash == hash);
            Assert.Null(await work.AccountingEventDbRepository.GetByKeyAsync(
                AccountingEventKeys.InterceptedHtlcSettled(circuit.IncomingChannelId, circuit.IncomingHtlcId), ct));
            Assert.Null(await work.AccountingEventDbRepository.GetByKeyAsync(
                AccountingEventKeys.ForwardSettled(circuit.IncomingChannelId, circuit.IncomingHtlcId), ct));
        }
        await Payee.Services.GetRequiredService<IHoldInvoiceService>().SettleHoldInvoiceAsync(hash, preimage, ct);
        await Poll.UntilAsync(async () =>
        {
            await using var scope = Node.Services.CreateAsyncScope();
            var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var circuit = (await work.ForwardCircuitDbRepository.ListAsync(new ForwardCircuitListQuery(0, 100), ct))
                .Single(circuit => circuit.PaymentHash == hash);
            if (circuit.Status != ForwardCircuitStatus.Fulfilled) return false;
            var fact = await work.AccountingEventDbRepository.GetByKeyAsync(
                AccountingEventKeys.ForwardSettled(circuit.IncomingChannelId, circuit.IncomingHtlcId), ct);
            Assert.NotNull(fact);
            Assert.Equal((long)circuit.ActualFee.MilliSatoshi, fact.AmountMsat);
            Assert.Null(await work.AccountingEventDbRepository.GetByKeyAsync(
                AccountingEventKeys.InterceptedHtlcSettled(circuit.IncomingChannelId, circuit.IncomingHtlcId), ct));
            return true;
        }, s_timeout, "existing outgoing settles with exactly the forwarding fee, no intercepted income", ct);
        lifetime.Cancel();
        Console.WriteLine("real upstream onchain interceptor: existing outgoing claim persisted, downstream fulfilled, one forwarding fact");
        // The upstream payer follows its force-close on chain; close our observer without assuming offchain success.
        try { await paying; }
        catch (Exception exception) when (exception is RpcException or OperationCanceledException) { }
    }

    private async Task PrunedHistoryRefusalFlowAsync(CancellationToken ct)
    {
        // A small empty-block chain proves real pruneheight handling without another cluster runner/network flow.
        var core = await BitcoinCoreNode.DeployAsync(_fixture.Cluster.Run, new BitcoinCoreOptions
        {
            Name = "history-pruned",
            TxIndex = false,
            ExtraArgs = ["-rest", "-prune=550", "-fastprune=1"]
        }, s_timeout, ct);
        var rpc = core.CreateNBitcoinClient(RpcRoute.PodIp, "miner");
        await rpc.GenerateToAddressAsync(1_001, await rpc.GetNewAddressAsync(ct), ct);
        await rpc.SendCommandAsync("pruneblockchain", 700).WaitAsync(ct);
        var chain = new BitcoinChainService(Microsoft.Extensions.Options.Options.Create(new BitcoinOptions
        {
            // The test process reaches the pod by its IP (the Service DNS name resolves only inside the cluster)
            RpcEndpoint = $"http://{core.GetHost(RpcRoute.PodIp)}:{BitcoinCorePorts.Rpc}",
            RpcUser = core.Options.RpcUser,
            RpcPassword = core.Options.RpcPassword
        }), NullLogger<BitcoinChainService>.Instance, Node.Services.GetRequiredService<IOptions<NodeOptions>>());
        var floor = await chain.GetBlockDataStartHeightAsync();
        Assert.True(floor > 10, $"Core must actually prune the requested birthday, floor={floor}");
        var before = await Node.Services.GetRequiredService<IWalletHistoryService>().GetStatusAsync(ct);
        var recovery = new WalletHistoryService(Node.Services.GetRequiredService<IServiceScopeFactory>(), chain,
            Node.Services.GetRequiredService<IBlockPrevoutSource>(), Node.Services.GetRequiredService<IWalletHistoryGate>(),
            Node.Services.GetRequiredService<ISilentPaymentRecoveryAddressSource>(),
            Node.Services.GetRequiredService<IOptions<NodeOptions>>(), NullLogger<WalletHistoryService>.Instance);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.StartRescanAsync(10, cancellationToken: ct));
        Assert.Contains("pruned", error.Message);
        var after = await recovery.GetStatusAsync(ct);
        Assert.Equal(before?.Generation, after?.Generation);
        Assert.Equal(before?.CursorHeight, after?.CursorHeight);
        Assert.Equal(before?.CursorHash, after?.CursorHash);
        Assert.Equal(before?.IsActive, after?.IsActive);
        Console.WriteLine($"real Core pruned history refusal: birthday 10, pruneheight {floor}, durable job unchanged");
    }

    private async Task HistoryRestartFlowAsync(CancellationToken ct)
    {
        var target = Node.BlockchainMonitor.LastProcessedBlockHeight;
        List<WalletTransactionRecord> original;
        int walletFacts;
        await Node.Services.GetRequiredService<WalletHistoryService>().StopAsync(ct);
        await using (var scope = Node.Services.CreateAsyncScope())
        {
            var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            original = (await work.WalletTransactionDbRepository.GetHistoryAsync(0, target, false, ct)).ToList();
            Assert.NotEmpty(original);
            walletFacts = (await work.AccountingEventDbRepository.GetByKeyPrefixAsync("wallet:", ct)).Count;
            foreach (var record in original) await work.WalletTransactionDbRepository.StageRemoveAsync(record.TxId);
            await work.SaveChangesAsync();
        }
        var birthday = original.Min(record => record.BlockHeight!.Value);
        var beforeCustody = Node.Services.GetRequiredService<IUtxoMemoryRepository>().GetUnreservedUtxos()
            .Select(coin => (coin.TxId, coin.Index)).ToHashSet();
        var state = await Node.Services.GetRequiredService<IWalletHistoryService>().StartRescanAsync(birthday, target, cancellationToken: ct);
        Assert.True(state.IsActive);
        Assert.False(state.IsPartial);
        Assert.Equal(birthday - 1, state.CursorHeight);
        var accountKey = Node.SecureKeyManager.GetDepositAccount(Domain.Bitcoin.Enums.AddressType.P2Tr);
        Assert.NotNull(accountKey);
        await _host!.StopAsync(ct);
        await Node.StopAsync();
        Assert.Equal(accountKey.ExtendedPublicKey,
            Node.SecureKeyManager.GetDepositAccount(Domain.Bitcoin.Enums.AddressType.P2Tr)!.ExtendedPublicKey);
        await Node.StartAsync(ct);
        Assert.Equal(accountKey.ExtendedPublicKey,
            Node.SecureKeyManager.GetDepositAccount(Domain.Bitcoin.Enums.AddressType.P2Tr)!.ExtendedPublicKey);
        _host = Node.Services.GetServices<IHostedService>().OfType<LndGrpcHost>().Single();
        await _host.StartAsync(ct);
        await Poll.UntilAsync(async () => (await Node.Services.GetRequiredService<IWalletHistoryService>().GetStatusAsync(ct))
            is { IsActive: false, Error: null }, s_timeout, "persisted bounded history job completed after restart", ct);
        await using var restored = Node.Services.CreateAsyncScope();
        var restoredWork = restored.ServiceProvider.GetRequiredService<IUnitOfWork>();
        foreach (var expected in original)
        {
            var actual = await restoredWork.WalletTransactionDbRepository.GetByIdAsync(expected.TxId, ct);
            Assert.NotNull(actual);
            Assert.Equal(expected.RawTransaction, actual.RawTransaction);
            Assert.Equal(expected.BlockHash, actual.BlockHash);
            Assert.Equal(expected.BlockHeight, actual.BlockHeight);
            Assert.Equal(expected.OurOutputs.Order(), actual.OurOutputs.Order());
            Assert.Equal(expected.OurInputs.OrderBy(input => input.InputIndex), actual.OurInputs.OrderBy(input => input.InputIndex));
        }
        Assert.Equal(walletFacts, (await restoredWork.AccountingEventDbRepository.GetByKeyPrefixAsync("wallet:", ct)).Count);
        Assert.True(beforeCustody.SetEquals(Node.Services.GetRequiredService<IUtxoMemoryRepository>().GetUnreservedUtxos()
            .Select(coin => (coin.TxId, coin.Index))), "history recovery must preserve custody exactly");
        using var ours = LndNodeConnection.CreateWithoutNodeInfo(Settings(LndMacaroonFiles.AdminFileName));
        var history = await ours.LightningClient.GetTransactionsAsync(new GetTransactionsRequest
        {
            StartHeight = (int)birthday,
            EndHeight = (int)target
        }, cancellationToken: ct);
        foreach (var record in original) Assert.Contains(history.Transactions, transaction => transaction.TxHash == record.TxId.ToString());
        Assert.Contains(history.Transactions, transaction => transaction.Label == "dangling overwrite");
        var accounts = await ours.WalletKitClient.ListAccountsAsync(new ListAccountsRequest(), cancellationToken: ct);
        Assert.Contains(accounts.Accounts, account => account.Name == "dangling");
        Console.WriteLine($"history restart: {original.Count} raw records restored, bounded {birthday}..{target}, custody and accounting unchanged");
    }
}