using System.Security.Cryptography;
using Grpc.Core;

namespace NLightning.Testing.Lnd.Tests.Docker;

using Invoicesrpc;
using Lnrpc;
using Routerrpc;

/// <summary>
/// The in-tree LND client against two real LND 0.21.4 nodes. The test starts its own containers (a regtest bitcoind
/// and two <c>custom_lnd:0.21.4-beta</c> nodes, all named <c>nltg-lndgrpc-&lt;id&gt;-*</c>) on its own Docker network,
/// reaches LND's gRPC through ports published on 127.0.0.1, and removes only what it created, also on failure.
/// Explicit, category LndGrpc, in a Docker namespace (left out by CI's <c>!~Docker</c> filter). Run it alone, under the
/// machine's Docker lock:
/// <c>/path/to/docker-lock.sh dotnet run --project test/NLightning.Testing.Lnd.Tests -c Release -f net10.0 --
/// -explicit only -trait Category=LndGrpc</c>.
/// </summary>
public class LndGrpcLiveTests
{
    private const string BitcoindImage = "bitcoin/bitcoin:31.1";
    private const string LndImage = "custom_lnd:0.21.4-beta";
    private const string RpcUser = "nltg";
    private const string RpcPassword = "nltg";
    private const int ZmqBlockPort = 28334;
    private const int ZmqTxPort = 28335;
    private const string LndDir = "/home/lnd/.lnd";
    private const long ChannelCapacitySat = 1_000_000;
    private const long PaymentSat = 100_000;

    private static readonly TimeSpan s_waitTimeout = TimeSpan.FromSeconds(120);

    [Fact(Explicit = true)]
    [Trait("Category", "LndGrpc")]
    public async Task Given_TwoLnd0214Nodes_When_DrivenThroughTheInTreeClient_Then_AChannelOpensAndAPaymentSettles()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var network = $"nltg-lndgrpc-{id}-net";
        var bitcoind = $"nltg-lndgrpc-{id}-bitcoind";
        var nameA = $"nltg-lndgrpc-{id}-a";
        var nameB = $"nltg-lndgrpc-{id}-b";
        var workDir = Directory.CreateTempSubdirectory("nltg-lndgrpc-");
        Log($"run {id}: network {network}");

        try
        {
            await DockerCli.RunAsync(ct, "network", "create", "--label", $"nltg-lndgrpc={id}", network);
            await StartBitcoindAsync(bitcoind, network, id, ct);
            await StartLndAsync(nameA, bitcoind, network, id, ct);
            await StartLndAsync(nameB, bitcoind, network, id, ct);
            var settingsA = await ReadSettingsAsync(nameA, workDir.FullName, ct);
            var settingsB = await ReadSettingsAsync(nameB, workDir.FullName, ct);

            using var a = await ConnectWhenActiveAsync(settingsA, ct);
            using var b = await ConnectWhenActiveAsync(settingsB, ct);

            // Act + Assert: GetInfo
            var infoA = await a.LightningClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: ct);
            var infoB = await b.LightningClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: ct);
            Log($"a {infoA.IdentityPubkey} {infoA.Version}; b {infoB.IdentityPubkey} {infoB.Version}");
            Assert.StartsWith("0.21.4-beta", infoA.Version);
            Assert.StartsWith("0.21.4-beta", infoB.Version);
            Assert.Equal(infoA.IdentityPubkey, a.LocalNodePubKey);
            Assert.Equal(nameA, a.LocalAlias);

            // NewAddress + fund by mining 101 blocks to a's address (its first coinbases mature)
            var address = await a.LightningClient.NewAddressAsync(
                new NewAddressRequest { Type = AddressType.WitnessPubkeyHash }, cancellationToken: ct);
            Assert.StartsWith("bcrt1q", address.Address);
            var height = await MineAsync(bitcoind, 101, address.Address, ct);
            await WaitSyncedAsync(a, height, ct);
            await WaitSyncedAsync(b, height, ct);
            var balance = await WaitUntilAsync("a's confirmed wallet balance", async () =>
            {
                var response = await a.LightningClient.WalletBalanceAsync(new WalletBalanceRequest(),
                                                                          cancellationToken: ct);
                return response.ConfirmedBalance > ChannelCapacitySat ? response : null;
            }, ct);
            Log($"a funded: confirmed {balance.ConfirmedBalance} sat at height {height}");

            // ConnectPeer
            await a.LightningClient.ConnectPeerAsync(new ConnectPeerRequest
            {
                Addr = new LightningAddress { Pubkey = b.LocalNodePubKey, Host = $"{nameB}:9735" },
                Timeout = 30
            }, cancellationToken: ct);
            await WaitUntilAsync("b to list a as a peer", async () =>
            {
                var peers = await b.LightningClient.ListPeersAsync(new ListPeersRequest(), cancellationToken: ct);
                return peers.Peers.Any(x => x.PubKey == a.LocalNodePubKey) ? peers : null;
            }, ct);

            // OpenChannelSync, mined, active on both sides
            var channelPoint = await a.LightningClient.OpenChannelSyncAsync(new OpenChannelRequest
            {
                NodePubkey = Google.Protobuf.ByteString.CopyFrom(b.LocalNodePubKeyBytes),
                LocalFundingAmount = ChannelCapacitySat,
                SatPerVbyte = 2
            }, cancellationToken: ct);
            var fundingTxId = Convert.ToHexStringLower(channelPoint.FundingTxidBytes.ToByteArray().Reverse().ToArray());
            Log($"channel opened: {fundingTxId}:{channelPoint.OutputIndex}");
            height = await MineAsync(bitcoind, 6, null, ct);
            await WaitSyncedAsync(a, height, ct);
            await WaitSyncedAsync(b, height, ct);
            var channelA = await WaitActiveChannelAsync(a, b.LocalNodePubKey, ct);
            await WaitActiveChannelAsync(b, a.LocalNodePubKey, ct);
            Assert.Equal($"{fundingTxId}:{channelPoint.OutputIndex}", channelA.ChannelPoint);
            Assert.Equal(ChannelCapacitySat, channelA.Capacity);

            // AddInvoice on b, followed through Invoices.SubscribeSingleInvoice
            var invoice = await b.LightningClient.AddInvoiceAsync(
                new Invoice { Value = PaymentSat, Memo = "nltg-lndgrpc" }, cancellationToken: ct);
            using var subscription = b.InvoiceClient.SubscribeSingleInvoice(
                new SubscribeSingleInvoiceRequest { RHash = invoice.RHash }, cancellationToken: ct);
            var settled = ReadUntilSettledAsync(subscription, ct);

            // Router.SendPaymentV2 from a, streamed to SUCCEEDED
            var payment = await PayAsync(a, invoice.PaymentRequest, ct);
            Assert.Equal(Payment.Types.PaymentStatus.Succeeded, payment.Status);
            Assert.Equal(PaymentSat, payment.ValueSat);
            Assert.Equal(Convert.ToHexStringLower(invoice.RHash.ToByteArray()), payment.PaymentHash);
            Log($"paid {payment.PaymentHash}: {payment.Status}, preimage {payment.PaymentPreimage}");

            var settledInvoice = await settled.WaitAsync(s_waitTimeout, ct);
            Assert.Equal(Invoice.Types.InvoiceState.Settled, settledInvoice.State);
            Assert.Equal(PaymentSat * 1000, settledInvoice.AmtPaidMsat);

            // Invoices.LookupInvoiceV2
            var lookup = await b.InvoiceClient.LookupInvoiceV2Async(
                new LookupInvoiceMsg { PaymentHash = invoice.RHash }, cancellationToken: ct);
            Assert.Equal(Invoice.Types.InvoiceState.Settled, lookup.State);
            Assert.Equal(invoice.PaymentRequest, lookup.PaymentRequest);

            // ListChannels balances
            var afterA = await WaitSettledChannelAsync(a, b.LocalNodePubKey, ct);
            var afterB = await WaitSettledChannelAsync(b, a.LocalNodePubKey, ct);
            Log($"after payment: a local {afterA.LocalBalance} remote {afterA.RemoteBalance}; "
              + $"b local {afterB.LocalBalance} remote {afterB.RemoteBalance}");
            Assert.Equal(PaymentSat, afterB.LocalBalance);
            Assert.Equal(PaymentSat, afterA.RemoteBalance);
            Assert.Equal(afterA.LocalBalance, afterB.RemoteBalance);

            // WalletKit.ListUnspent: a's change output and the other matured coinbases
            var unspent = await a.WalletKitClient.ListUnspentAsync(
                new Walletrpc.ListUnspentRequest { MinConfs = 1, MaxConfs = int.MaxValue }, cancellationToken: ct);
            Log($"a ListUnspent: {unspent.Utxos.Count} outputs, {unspent.Utxos.Sum(x => x.AmountSat)} sat");
            Assert.NotEmpty(unspent.Utxos);
            Assert.Contains(unspent.Utxos, x => x.Outpoint.TxidStr == fundingTxId);

            // A pool round over both nodes: readiness, lookup by key, and LNUnit's 50/50 rebalance
            using var pool = new LndNodePool(new LndNodePoolConfig { StartBackgroundUpdates = false }
                                             .AddConnectionSettings(settingsA)
                                             .AddConnectionSettings(settingsB));
            await pool.WaitUntilAllReadyAsync(s_waitTimeout, ct);
            Assert.Equal(2, pool.TotalNodes);
            Assert.Equal(2, pool.ReadyNodes.Count);
            Assert.True(pool.AllReady);
            Assert.Equal(nameB, pool.GetLndNodeConnection(b.LocalNodePubKey)?.LocalAlias);
            var stats = await pool.RebalanceNodePoolAsync(100_000, ct);
            var rebalancedA = await WaitSettledChannelAsync(a, b.LocalNodePubKey, ct);
            Log($"pool rebalance: {stats.TotalRebalanceCount} payment(s), {stats.TotalAmount} sat; a local "
              + $"{rebalancedA.LocalBalance} remote {rebalancedA.RemoteBalance}");
            Assert.Equal(1, stats.TotalRebalanceCount);
            Assert.Equal(PaymentSat + (long)stats.TotalAmount, rebalancedA.RemoteBalance);
            Assert.True(Math.Abs(rebalancedA.LocalBalance - rebalancedA.RemoteBalance) < 100_000);
        }
        finally
        {
            await CleanUpAsync([nameA, nameB, bitcoind], network);
            workDir.Delete(recursive: true);
        }
    }

    private static async Task StartBitcoindAsync(string name, string network, string id, CancellationToken ct)
    {
        await DockerCli.RunAsync(ct, "run", "-d", "--pull=never", "--name", name, "--network", network, "--label",
                                 $"nltg-lndgrpc={id}", BitcoindImage, "bitcoind", "-regtest", "-server=1",
                                 "-txindex=1", $"-rpcuser={RpcUser}", $"-rpcpassword={RpcPassword}",
                                 "-rpcallowip=0.0.0.0/0", "-rpcbind=0.0.0.0", "-fallbackfee=0.0002", "-dnsseed=0",
                                 $"-zmqpubrawblock=tcp://0.0.0.0:{ZmqBlockPort}",
                                 $"-zmqpubrawtx=tcp://0.0.0.0:{ZmqTxPort}", "-printtoconsole");
        await WaitUntilAsync($"{name} RPC", async () =>
        {
            var result = await DockerCli.TryRunAsync(ct, BitcoinCli(name, "getblockcount"));
            return result.ExitCode == 0 ? result.Stdout : null;
        }, ct);
        await DockerCli.RunAsync(ct, BitcoinCli(name, "createwallet", "miner"));

        // LND waits for a chain backend whose tip is recent before its server starts, as the LNUnit fixture mines.
        await MineAsync(name, 101, null, ct);
    }

    private static async Task StartLndAsync(string name, string bitcoind, string network, string id,
                                            CancellationToken ct)
    {
        // The regtest arguments of test/Docker/custom_lnd as the LNUnit fixture starts it, minus REST.
        await DockerCli.RunAsync(ct, "run", "-d", "--pull=never", "--name", name, "--hostname", name, "--network",
                                 network, "--label", $"nltg-lndgrpc={id}", "-p", "127.0.0.1::10009", LndImage, "lnd",
                                 "--bitcoin.regtest", "--bitcoin.node=bitcoind", $"--bitcoind.rpchost={bitcoind}",
                                 $"--bitcoind.rpcuser={RpcUser}", $"--bitcoind.rpcpass={RpcPassword}",
                                 $"--bitcoind.zmqpubrawblock=tcp://{bitcoind}:{ZmqBlockPort}",
                                 $"--bitcoind.zmqpubrawtx=tcp://{bitcoind}:{ZmqTxPort}", "--noseedbackup",
                                 "--rpclisten=0.0.0.0:10009", "--listen=0.0.0.0:9735", "--norest",
                                 $"--alias={name}", $"--tlsextradomain={name}", "--trickledelay=50",
                                 "--debuglevel=info");
    }

    private static async Task<LndSettings> ReadSettingsAsync(string name, string workDir, CancellationToken ct)
    {
        var macaroonInContainer = $"{LndDir}/data/chain/bitcoin/regtest/admin.macaroon";
        await WaitUntilAsync($"{name} admin.macaroon", async () =>
        {
            var result = await DockerCli.TryRunAsync(ct, "exec", name, "test", "-f", macaroonInContainer);
            return result.ExitCode == 0 ? "ok" : null;
        }, ct);

        var certPath = Path.Combine(workDir, $"{name}.tls.cert");
        var macaroonPath = Path.Combine(workDir, $"{name}.admin.macaroon");
        await DockerCli.RunAsync(ct, "cp", $"{name}:{LndDir}/tls.cert", certPath);
        await DockerCli.RunAsync(ct, "cp", $"{name}:{macaroonInContainer}", macaroonPath);
        var published = await DockerCli.RunAsync(ct, "port", name, "10009/tcp");
        var port = int.Parse(published.Split('\n')[0].Trim().Split(':')[^1]);
        Log($"{name}: gRPC at 127.0.0.1:{port}");
        return LndSettings.FromFiles("127.0.0.1", port, certPath, macaroonPath);
    }

    private static async Task<LndNodeConnection> ConnectWhenActiveAsync(LndSettings settings, CancellationToken ct) =>
        await WaitUntilAsync($"{settings.GrpcEndpoint} SERVER_ACTIVE", async () =>
        {
            LndNodeConnection? connection = null;
            try
            {
                connection = await LndNodeConnection.ConnectAsync(settings, cancellationToken: ct);
                var state = await connection.GetStateSafeAsync(TimeSpan.FromSeconds(3), ct);
                if (state == WalletState.ServerActive)
                    return connection;
                Log($"{settings.GrpcEndpoint}: {state}");
            }
            catch (RpcException e)
            {
                Log($"{settings.GrpcEndpoint}: {e.StatusCode} {e.Status.Detail}");
            }

            connection?.Dispose();
            return null;
        }, ct);

    private static async Task<int> MineAsync(string bitcoind, int blocks, string? address, CancellationToken ct)
    {
        address ??= await DockerCli.RunAsync(ct, BitcoinCli(bitcoind, "-rpcwallet=miner", "getnewaddress"));
        await DockerCli.RunAsync(ct, BitcoinCli(bitcoind, "generatetoaddress", blocks.ToString(), address));
        return int.Parse(await DockerCli.RunAsync(ct, BitcoinCli(bitcoind, "getblockcount")));
    }

    private static string[] BitcoinCli(string bitcoind, params string[] args) =>
    [
        "exec", bitcoind, "bitcoin-cli", "-regtest", $"-rpcuser={RpcUser}", $"-rpcpassword={RpcPassword}", .. args
    ];

    private static async Task WaitSyncedAsync(LndNodeConnection node, int height, CancellationToken ct) =>
        await WaitUntilAsync($"{node.LocalAlias} synced to {height}", async () =>
        {
            var info = await node.LightningClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: ct);
            return info.SyncedToChain && info.BlockHeight >= height ? info : null;
        }, ct);

    private static async Task<Channel> WaitActiveChannelAsync(LndNodeConnection node, string peer,
                                                              CancellationToken ct,
                                                              Func<Channel, bool>? condition = null) =>
        await WaitUntilAsync($"{node.LocalAlias}'s active channel", async () =>
        {
            var channels = await node.LightningClient.ListChannelsAsync(new ListChannelsRequest { ActiveOnly = true },
                                                                        cancellationToken: ct);
            return channels.Channels.FirstOrDefault(x => x.RemotePubkey == peer && (condition?.Invoke(x) ?? true));
        }, ct);

    /// <summary>
    /// The channel once no HTLC is pending on it: LND leaves a settled HTLC out of both balances until the commitment
    /// dance that removes it is done, which can be after the payment and the invoice report success.
    /// </summary>
    private static Task<Channel> WaitSettledChannelAsync(LndNodeConnection node, string peer, CancellationToken ct) =>
        WaitActiveChannelAsync(node, peer, ct, x => x.PendingHtlcs.Count == 0 && x.UnsettledBalance == 0);

    private static async Task<Payment> PayAsync(LndNodeConnection payer, string paymentRequest, CancellationToken ct) =>
        await WaitUntilAsync("a payment that succeeds", async () =>
        {
            // A fresh channel can be active before the router has its edge (NL-319); a failed attempt is retried.
            using var call = payer.RouterClient.SendPaymentV2(new SendPaymentRequest
            {
                PaymentRequest = paymentRequest,
                TimeoutSeconds = 30,
                FeeLimitSat = 10,
                NoInflightUpdates = true
            }, cancellationToken: ct);
            Payment? last = null;
            await foreach (var update in call.ResponseStream.ReadAllAsync(ct))
            {
                Log($"payment update: {update.Status} {update.FailureReason}");
                last = update;
            }

            return last?.Status == Payment.Types.PaymentStatus.Succeeded ? last : null;
        }, ct);

    private static async Task<Invoice> ReadUntilSettledAsync(AsyncServerStreamingCall<Invoice> call,
                                                             CancellationToken ct)
    {
        await foreach (var update in call.ResponseStream.ReadAllAsync(ct))
        {
            Log($"invoice update: {update.State}");
            if (update.State == Invoice.Types.InvoiceState.Settled)
                return update;
        }

        throw new InvalidOperationException("SubscribeSingleInvoice ended before the invoice settled.");
    }

    private static async Task<T> WaitUntilAsync<T>(string what, Func<Task<T?>> probe, CancellationToken ct)
        where T : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(s_waitTimeout);
        Exception? lastError = null;
        while (true)
        {
            try
            {
                if (await probe() is { } value)
                    return value;
            }
            catch (RpcException e)
            {
                lastError = e;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out after {s_waitTimeout.TotalSeconds} s waiting for {what}.",
                                           lastError);
            }
        }
    }

    private static async Task CleanUpAsync(string[] containers, string network)
    {
        // Only what this test created, by name, and never with the test's (possibly cancelled) token.
        foreach (var container in containers)
        {
            var result = await DockerCli.TryRunAsync(CancellationToken.None, "rm", "-f", "-v", container);
            Log($"removed {container}: exit {result.ExitCode}");
        }

        var networkResult = await DockerCli.TryRunAsync(CancellationToken.None, "network", "rm", network);
        Log($"removed {network}: exit {networkResult.ExitCode}");
    }

    private static void Log(string message) =>
        TestContext.Current.SendDiagnosticMessage($"[lndgrpc] {message}");
}