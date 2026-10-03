using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NLightning.Cashu.PaymentProcessor;
using NLightning.Tests.Utils;

namespace NLightning.Integration.Tests.Docker.Interop.Cashu;

using Domain.Bitcoin.Enums;
using Domain.Cashu.Enums;
using Domain.Money;
using Domain.Offers.Enums;
using Domain.Offers.Interfaces;
using Domain.Offers.Models;
using Domain.Payments.Enums;
using Domain.Persistence.Interfaces;
using Fixtures.Cashu;
using Utils;

/// <summary>
/// Proof of NL-997 (the processor's breadth) with CDK's own <c>cdk-mintd</c> and <c>cdk-cli</c> 0.18.1: BOLT 12 mint
/// and melt quotes over our offers and the payer's (NUT-25), and on-chain mint and melt quotes from the mint node's own
/// wallet (NUT-30), the on-chain melt resolved after a restart of the mint while it was pending.
/// </summary>
public sealed partial class CdkMintdInteropTests
{
    private static readonly TimeSpan s_onchainTimeout = TimeSpan.FromMinutes(3);

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ACdkMintOnOurNode_When_TheWalletMintsAndMeltsOverBolt12_Then_OffersCarryBothWays()
    {
        // Arrange: the mint's node with the processor, a payer with a channel to it, and cdk-mintd on the processor
        var ct = TestContext.Current.CancellationToken;
        var processorPort = await PortPoolUtil.GetAvailablePortAsync();
        await using var mintNode = await NLightningTestNode.CreateAsync(_fixture.Bitcoin, "nltg-cashu-mint12");
        mintNode.ExtraConfiguration["Cashu:PaymentProcessor:Enabled"] = "true";
        mintNode.ExtraConfiguration["Cashu:PaymentProcessor:Port"] = processorPort.ToString();
        mintNode.ExtraConfiguration["Cashu:PaymentProcessor:AllowInsecureLoopback"] = "true";
        await using var payer = await NLightningTestNode.CreateAsync(_fixture.Bitcoin, "nltg-cashu-payer12");
        await mintNode.StartAsync(ct);
        await payer.StartAsync(ct);
        var processor = ActivatorUtilities.CreateInstance<CashuPaymentProcessorHost>(mintNode.Services);
        await processor.StartAsync(ct);
        try
        {
            var channelId = await OpenPayerChannelAsync(payer, mintNode, ct);
            Assert.True(mintNode.Services.GetRequiredService<CdkPaymentProcessorService>().Bolt12Available);
            await _fixture.StartMintAsync(processorPort, ct);
            var wallet = await _fixture.CreateWalletDirectoryAsync(ct);

            // Act 1: the wallet asks for 3,000 sat of ecash over BOLT 12; the payer pays the mint's offer
            var mintRun = _fixture.StartWallet(wallet, [
                              "mint", _fixture.MintUrl, "3000", "--method", "bolt12", "--wait-duration", "20"
                          ], ct);
            var mintOffer = await WaitForWalletOutputAsync(mintRun, Bolt12OfferRegex(), "the mint quote's offer", ct);
            var paid = await payer.Services.GetRequiredService<IOfferPaymentService>()
                                  .PayOfferAsync(new PayOfferRequest(mintOffer), new PayOfferOptions(), ct);
            var (mintExit, mintOutput) = await _fixture.WaitWalletExitAsync(mintRun, ct)
                                                       .WaitAsync(s_walletTimeout, ct);
            Console.WriteLine($"[cashu] cdk-cli mint --method bolt12 (exit {mintExit}):\n{mintOutput}");

            // Assert 1: the offer was paid once and the wallet holds the ecash: 3,000 sat plus the fee of our own
            // dummy blinded hops, which the payer paid to us too (NL-526), rounded down to whole sats
            Assert.Equal(FetchInvoiceStatus.Received, paid.Fetch.Status);
            Assert.Equal(PaymentStatus.Succeeded, paid.Payment?.Payment.Status);
            Assert.Equal(0, mintExit);
            var minted = ulong.Parse(MintedRegex().Match(mintOutput).Groups[1].Value);
            Assert.InRange(minted, 3_000UL, 3_010UL);
            Assert.Equal(minted, await GetWalletBalanceAsync(wallet, ct));

            // Act 2: the wallet melts into an offer of the payer's (1,000 sat)
            var payerOffer = await payer.Services.GetRequiredService<IOfferService>()
                                        .CreateOfferAsync(new CreateOfferRequest(LightningMoney.Satoshis(1_000),
                                                                                 "cashu bolt12 melt"), ct);
            var meltOutput = await _fixture.RunWalletAsync(wallet, [
                                 "melt", "--mint-url", _fixture.MintUrl, "--method", "bolt12", "--offer",
                                 payerOffer.Offer.Bolt12
                             ], ct);

            // Assert 2: the mint node paid the payer's offer within the reserve (0.5 %, at least 5 sat)
            Assert.Contains("Fee Reserve: 5", meltOutput);
            Assert.Contains("Payment successful: Paid 1000", meltOutput);
            var counts = await payer.Services.GetRequiredService<IOfferService>()
                                    .GetInvoiceCountsAsync(payerOffer.Offer.OfferId, ct);
            Assert.Equal(1, counts.Paid);
            var balance = await GetWalletBalanceAsync(wallet, ct);
            Assert.InRange(balance, minted - 1_000UL - 5UL, minted - 1_000UL);
            Console.WriteLine($"[cashu] bolt12 melt done, wallet balance {balance} sat:\n{meltOutput}");
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

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ACdkMintOnOurNode_When_TheWalletMintsAndMeltsOnchain_Then_TheMeltSurvivesAMintRestart()
    {
        // Arrange: the mint's node with on-chain quotes (1 confirmation, one fee option) and a funded wallet
        var ct = TestContext.Current.CancellationToken;
        var processorPort = await PortPoolUtil.GetAvailablePortAsync();
        await using var mintNode = await NLightningTestNode.CreateAsync(_fixture.Bitcoin, "nltg-cashu-mintoc");
        mintNode.ExtraConfiguration["Cashu:PaymentProcessor:Enabled"] = "true";
        mintNode.ExtraConfiguration["Cashu:PaymentProcessor:Port"] = processorPort.ToString();
        mintNode.ExtraConfiguration["Cashu:PaymentProcessor:AllowInsecureLoopback"] = "true";
        mintNode.ExtraConfiguration["Cashu:PaymentProcessor:OnchainEnabled"] = "true";
        mintNode.ExtraConfiguration["Cashu:PaymentProcessor:OnchainConfirmations"] = "1";
        mintNode.ExtraConfiguration["Cashu:PaymentProcessor:OnchainFeeTargets"] = "6";
        await mintNode.StartAsync(ct);
        var processor = ActivatorUtilities.CreateInstance<CashuPaymentProcessorHost>(mintNode.Services);
        await processor.StartAsync(ct);
        try
        {
            await mintNode.FundWalletAsync(LightningMoney.Satoshis(100_000), AddressType.P2Wpkh, ct);
            Assert.True(mintNode.Services.GetRequiredService<CdkPaymentProcessorService>().OnchainAvailable);
            await _fixture.StartMintAsync(processorPort, ct);
            var wallet = await _fixture.CreateWalletDirectoryAsync(ct);

            // Act 1: an on-chain mint quote; bitcoind pays 50,000 sat to its address and mines a block
            var mintRun = _fixture.StartWallet(wallet, [
                              "mint", _fixture.MintUrl, "--method", "onchain", "--wait-duration", "30"
                          ], ct);
            var address = await WaitForWalletOutputAsync(mintRun, OnchainAddressRegex(), "the mint quote's address",
                                                         ct);
            await _fixture.Bitcoin.Rpc.SendToAddressAsync(BitcoinAddress.Create(address, Network.RegTest),
                                                          Money.Satoshis(50_000), cancellationToken: ct);
            await _fixture.MineAsync(1, ct);
            var (mintExit, mintOutput) = await _fixture.WaitWalletExitAsync(mintRun, ct)
                                                       .WaitAsync(s_onchainTimeout, ct);
            Console.WriteLine($"[cashu] cdk-cli mint --method onchain (exit {mintExit}):\n{mintOutput}");

            // Assert 1: the deposit became ecash
            Assert.Equal(0, mintExit);
            Assert.Contains("Minted 50000 sat", mintOutput);
            Assert.Equal(50_000UL, await GetWalletBalanceAsync(wallet, ct));

            // Act 2: an on-chain melt of 20,000 sat to bitcoind; the mint restarts while it is pending, then a block
            var destination = await _fixture.Bitcoin.Rpc.GetNewAddressAsync(ct);
            var meltRun = _fixture.StartWallet(wallet, [
                              "melt", "--mint-url", _fixture.MintUrl, "--method", "onchain", "--address",
                              destination.ToString(), "--amount", "20000"
                          ], ct);
            var quoteId = await WaitForWalletOutputAsync(meltRun, QuoteIdRegex(), "the melt quote's id", ct);
            await Poll.UntilAsync(async () => await GetProcessorQuoteStateAsync(mintNode, quoteId)
                                                  is CashuQuoteState.Pending or CashuQuoteState.Paid,
                                  s_onchainTimeout, "the melt's transaction was sent", ct);
            // The wallet waits for the melt to be final; the mint restarts under it
            await _fixture.RestartMintAsync(ct);
            await _fixture.MineAsync(1, ct);

            // Assert 2: the processor and the restarted mint report it paid; bitcoind received it
            await Poll.UntilAsync(async () => await GetProcessorQuoteStateAsync(mintNode, quoteId)
                                                  == CashuQuoteState.Paid, s_onchainTimeout, "the melt confirmed",
                                  ct);
            var mintQuote = await Poll.ForAsync(async () =>
            {
                var json = await _fixture.GetFromMintAsync($"/v1/melt/quote/onchain/{quoteId}", ct);
                return json.Contains("\"PAID\"", StringComparison.Ordinal) ? json : null;
            }, s_onchainTimeout, "the restarted mint reported the melt paid", ct);
            Console.WriteLine($"[cashu] mint's melt quote: {mintQuote}");
            var received = await _fixture.Bitcoin.Rpc.GetReceivedByAddressAsync(destination, 1);
            Assert.Equal(Money.Satoshis(20_000), received);
            Assert.Contains("\"PAID\"", mintQuote);

            // The wallet's own wait ended with the restart, with the payment, or not yet: it is only logged
            try
            {
                var (meltExit, meltOutput) = await _fixture.WaitWalletExitAsync(meltRun, ct)
                                                           .WaitAsync(TimeSpan.FromSeconds(30), ct);
                Console.WriteLine($"[cashu] cdk-cli melt --method onchain (exit {meltExit}):\n{meltOutput}");
            }
            catch (TimeoutException)
            {
                Console.WriteLine("[cashu] cdk-cli melt --method onchain still waiting:\n"
                                + await _fixture.GetWalletOutputAsync(meltRun, ct));
                await _fixture.StopWalletAsync(meltRun, CancellationToken.None);
            }
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

    /// <summary>The processor's own record of a quote on the mint node.</summary>
    private static async Task<CashuQuoteState?> GetProcessorQuoteStateAsync(NLightningTestNode node, string quoteId)
    {
        using var scope = node.Services.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return (await unitOfWork.CashuQuoteDbRepository.GetAsync(quoteId))?.State;
    }

    /// <summary>The first group of <paramref name="regex"/> in a background wallet command's output.</summary>
    private async Task<string> WaitForWalletOutputAsync(CashuMintFixture.WalletRun walletRun, Regex regex, string what,
                                                        CancellationToken ct) =>
        await Poll.ForAsync(async () =>
        {
            var match = regex.Match(await _fixture.GetWalletOutputAsync(walletRun, ct));
            return match.Success ? match.Groups[1].Value : null;
        }, s_walletTimeout, $"cdk-cli printed {what}", ct);

    [GeneratedRegex(@"Please pay: (lno1[0-9a-z]+)")]
    private static partial Regex Bolt12OfferRegex();

    [GeneratedRegex(@"Send sats to: (bcrt1[0-9a-z]+)")]
    private static partial Regex OnchainAddressRegex();

    [GeneratedRegex(@"Minted (\d+) sat")]
    private static partial Regex MintedRegex();

    [GeneratedRegex(@"Quote ID: (\S+)")]
    private static partial Regex QuoteIdRegex();
}