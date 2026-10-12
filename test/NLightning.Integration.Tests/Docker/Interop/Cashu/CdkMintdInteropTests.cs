using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Cashu.PaymentProcessor;
using NLightning.Tests.Utils;

namespace NLightning.Integration.Tests.Docker.Interop.Cashu;

using Abcd;
using Bolt11.Models;
using Domain.Bitcoin.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Protocol.ValueObjects;
using Fixtures.Cashu;
using Utils;

/// <summary>
/// Proof C2 of the Cashu plan (NL-993): CDK's own mint, <c>cdk-mintd</c> 0.18.1 with <c>backend = "grpcprocessor"</c>,
/// runs on our node through the CDK payment processor (C1, NL-992), and CDK's own wallet, <c>cdk-cli</c>, mints and
/// melts ecash against it.
/// </summary>
/// <remarks>
/// <para>Two in-process nodes on the fixture's bitcoind: the mint's node (the processor on loopback) and a payer with
/// a channel to it. The wallet's mint quote is one of the mint node's invoices (label <c>cashu-mint</c>), which the
/// payer pays: the mint learns it through the processor and issues the ecash. The wallet's melt pays one of the
/// payer's invoices through the mint node's payment service, within the processor's fee reserve.</para>
/// <para>BOLT 11 only, as C1. The channel is opened by the payer with a push, so the mint node can pay from the
/// start.</para>
/// </remarks>
[Collection(CashuMintCollection.Name)]
[Trait("Category", CashuMintCollection.Category)]
public sealed partial class CdkMintdInteropTests
{
    private const int TestTimeoutMs = 20 * 60 * 1_000;

    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(200_000);
    private static readonly TimeSpan s_usableTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan s_walletTimeout = TimeSpan.FromMinutes(2);

    private readonly CashuMintFixture _fixture;

    public CdkMintdInteropTests(CashuMintFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        fixture.SkipIfUnavailable();
        Console.SetOut(new TestOutputWriter(output));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ACdkMintOnOurNode_When_TheWalletMintsAndMelts_Then_OurNodeReceivesAndPays()
    {
        // Arrange: the mint's node with the processor, a payer with a channel to it, and cdk-mintd on the processor
        var ct = TestContext.Current.CancellationToken;
        var processorPort = await PortPoolUtil.GetAvailablePortAsync();
        await using var mintNode = await NLightningTestNode.CreateAsync(_fixture.Bitcoin, "nltg-cashu-mint");
        mintNode.ExtraConfiguration["Cashu:PaymentProcessor:Enabled"] = "true";
        mintNode.ExtraConfiguration["Cashu:PaymentProcessor:Port"] = processorPort.ToString();
        // h2c on loopback, as cdk-mintd's allow_insecure on its side (NL-998)
        mintNode.ExtraConfiguration["Cashu:PaymentProcessor:AllowInsecureLoopback"] = "true";
        await using var payer = await NLightningTestNode.CreateAsync(_fixture.Bitcoin, "nltg-cashu-payer");
        await mintNode.StartAsync(ct);
        await payer.StartAsync(ct);
        var processor = ActivatorUtilities.CreateInstance<CashuPaymentProcessorHost>(mintNode.Services);
        await processor.StartAsync(ct);
        try
        {
            var channelId = await OpenPayerChannelAsync(payer, mintNode, ct);
            await _fixture.StartMintAsync(processorPort, ct);
            var wallet = await _fixture.CreateWalletDirectoryAsync(ct);

            // Act 1: the wallet asks the mint for 10,000 sat of ecash; the payer pays the mint's invoice
            var mintRun = _fixture.StartWallet(wallet, ["mint", _fixture.MintUrl, "10000"], ct);
            var mintInvoice = await WaitForInvoiceAsync(mintRun, ct);
            var mintHash = HashOf(mintInvoice);
            var paid = await payer.PayInvoiceAsync(mintInvoice, ct, 120);
            var (mintExit, mintOutput) = await _fixture.WaitWalletExitAsync(mintRun, ct)
                                                       .WaitAsync(s_walletTimeout, ct);
            Console.WriteLine($"[cashu] cdk-cli mint (exit {mintExit}):\n{mintOutput}");

            // Assert 1: the mint quote was our labelled invoice, settled, and the wallet holds the ecash
            Assert.Equal(PaymentStatus.Succeeded, paid.Status);
            Assert.Equal(0, mintExit);
            // The mint saw the payment through the processor (PAID) and issued the ecash (ISSUED)
            Assert.Contains("Quote state: PAID", mintOutput);
            Assert.Contains("Minted 10000 sat", mintOutput);
            var received = await mintNode.GetInvoiceAsync(mintHash, ct);
            Assert.NotNull(received);
            Assert.Equal(InvoiceStatus.Settled, received.Status);
            Assert.Equal(LightningMoney.Satoshis(10_000), received.AmountReceived);
            Assert.Equal(CashuPaymentProcessorOptions.DefaultLabel, received.Label);
            Assert.Equal(10_000UL, await GetWalletBalanceAsync(wallet, ct));

            // Act 2: the wallet melts 4,000 sat of ecash into one of the payer's invoices
            var payerInvoice = await payer.CreateInvoiceAsync(LightningMoney.Satoshis(4_000), "cashu melt", ct);
            var meltOutput = await _fixture.RunWalletAsync(wallet, [
                                 "melt", "--mint-url", _fixture.MintUrl, "--invoice", payerInvoice.Bolt11!
                             ], ct);

            // Assert 2: our node paid it (labelled), the payer's invoice settled, the wallet spent 4,000 sat plus at
            // most the fee reserve (0.5 %, at least 5 sat: 20 sat here) and got its change back
            var melted = await Poll.ForAsync(async () =>
            {
                var payment = await mintNode.GetPaymentAsync(payerInvoice.PaymentHash, ct);
                return payment?.Status == PaymentStatus.Succeeded ? payment : null;
            }, TimeSpan.FromSeconds(60), "the mint node's melt payment succeeded", ct);
            Assert.Equal(CashuPaymentProcessorOptions.DefaultLabel, melted.Label);
            // The melt quote's fee reserve is the processor's (0.5 % of 4,000 sat) and the mint reported it paid
            Assert.Contains("Fee Reserve: 20", meltOutput);
            Assert.Contains("state=PAID", meltOutput);
            var settled = await payer.GetInvoiceAsync(payerInvoice.PaymentHash, ct);
            Assert.Equal(InvoiceStatus.Settled, settled!.Status);
            var balance = await GetWalletBalanceAsync(wallet, ct);
            Assert.InRange(balance, 6_000UL - 20UL, 6_000UL);
            Console.WriteLine($"[cashu] melt done, wallet balance {balance} sat:\n{meltOutput}");
            await WaitUsableAsync(payer, mintNode, channelId, ct);
        }
        catch (Exception)
        {
            Console.WriteLine("[cashu] cdk-mintd log:\n" + await _fixture.GetMintLogAsync(200, CancellationToken.None));
            throw;
        }
        finally
        {
            await _fixture.StopMintAsync(CancellationToken.None);
            await processor.StopAsync(CancellationToken.None);
            PortPoolUtil.ReleasePort(processorPort);
        }
    }

    /// <summary>The payer opens a private channel to the mint's node with a push and waits until both ends use it.</summary>
    private async Task<ChannelId> OpenPayerChannelAsync(NLightningTestNode payer, NLightningTestNode mintNode,
                                                        CancellationToken ct)
    {
        // The fundee of an anchors channel keeps an on-chain reserve (NL-379)
        await mintNode.FundWalletAsync(LightningMoney.Satoshis(100_000), AddressType.P2Wpkh, ct);
        await payer.FundWalletAsync(LightningMoney.Satoshis(s_capacity.Satoshi * 2), AddressType.P2Wpkh, ct);
        await payer.ConnectToAsync(mintNode, ct);
        var opened = await payer.OpenChannelAsync(
                         new OpenChannelClientRequest($"{mintNode.NodeIdHex}@127.0.0.1:{mintNode.Port}", s_capacity)
                         {
                             PushAmount = s_push
                         }, ct);
        Console.WriteLine($"[cashu] payer opened {opened.ChannelId}, state {opened.ChannelState}");
        await WaitUsableAsync(payer, mintNode, opened.ChannelId, ct);
        return opened.ChannelId;
    }

    private async Task WaitUsableAsync(NLightningTestNode payer, NLightningTestNode mintNode, ChannelId channelId,
                                       CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + s_usableTimeout;
        while (true)
        {
            var ours = (await payer.ListChannelsAsync(ct)).Channels.FirstOrDefault(c => c.ChannelId == channelId);
            var theirs = (await mintNode.ListChannelsAsync(ct)).Channels.FirstOrDefault(c => c.ChannelId == channelId);
            if (ours is { ShortChannelId: not null, OfferedHtlcCount: 0, ReceivedHtlcCount: 0 } && ours.IsUsable()
             && theirs is { OfferedHtlcCount: 0, ReceivedHtlcCount: 0 } && theirs.IsUsable())
                return;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The channel was not usable on both ends in time: payer "
                                         + $"{ours?.Describe() ?? "-"}; mint {theirs?.Describe() ?? "-"}");

            await _fixture.MineAsync(1, ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    /// <summary>The BOLT 11 invoice <c>cdk-cli mint</c> prints for its mint quote.</summary>
    private async Task<string> WaitForInvoiceAsync(CashuMintFixture.WalletRun walletRun, CancellationToken ct) =>
        await Poll.ForAsync(async () =>
        {
            var match = Bolt11Regex().Match(await _fixture.GetWalletOutputAsync(walletRun, ct));
            return match.Success ? match.Value : null;
        }, s_walletTimeout, "cdk-cli printed the mint quote's invoice", ct);

    /// <summary>The wallet's balance in sat, read from <c>cdk-cli balance</c> (the mint's line).</summary>
    private async Task<ulong> GetWalletBalanceAsync(string wallet, CancellationToken ct)
    {
        var output = await _fixture.RunWalletAsync(wallet, ["balance"], ct);
        var match = BalanceRegex().Match(output);
        Assert.True(match.Success, $"No balance in cdk-cli's output:\n{output}");
        return ulong.Parse(match.Groups[1].Value);
    }

    private static Hash HashOf(string bolt11) =>
        new(Convert.FromHexString(Invoice.Decode(bolt11, BitcoinNetwork.Regtest).PaymentHash!.ToString()));

    [GeneratedRegex(@"lnbcrt[0-9a-z]+")]
    private static partial Regex Bolt11Regex();

    [GeneratedRegex(@"(\d+)\s*sat", RegexOptions.IgnoreCase)]
    private static partial Regex BalanceRegex();
}