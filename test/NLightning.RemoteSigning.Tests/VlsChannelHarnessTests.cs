using Microsoft.Extensions.DependencyInjection;
using NLightning.Application.Channels.Services;
using NLightning.Application.Tests.Channels.Taproot;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Exceptions;
using NLightning.Domain.Money;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Protocol.Messages;
using NLightning.Domain.Signing.Recovery;
using NLightning.Domain.Signing.Vls;
using NLightning.Infrastructure.VlsSigning;

namespace NLightning.RemoteSigning.Tests;

/// <summary>Real policy gateway and production channel handlers; FIFO peer transport and artificial funding.</summary>
public sealed class VlsChannelHarnessTests
{
    [Fact(Explicit = true)]
    public async Task Given_EqualValuedHtlcsBothWaysAndDustRefusal_When_VlsSigns_Then_PeersVerifyAndBalancesConverge()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var gateway = new VlsGatewayFixture();
        await gateway.InitializeAsync(ct);
        var connection = new VlsSignerConnection(new VlsSignerOptions
        { SocketPath = gateway.SocketPath, TokenFile = gateway.TokenFile });
        var approval = new VlsPaymentApprovalClient(gateway.ApprovalSocketPath, gateway.ApprovalTokenFile);
        await using var harness = await TaprootOpenHarness.CreateAsync(configureServices: (node, services) =>
        {
            // Hold automatic commits long enough to offer the equal-value HTLCs together.
            services.Configure<CommitSchedulerOptions>(o => o.Debounce = TimeSpan.FromSeconds(1));
            if (node.Name != "Alice") return;
            node.Options.MaxDustHtlcExposureMsat = 0;
            node.Options.HtlcMinimumAmount = LightningMoney.Satoshis(1_000);
            node.Options.Routing.FeeBaseMsat = 1_000;
            node.Options.Routing.FeeProportionalMillionths = 0;
            node.PublicKeyManager = new VlsSecureKeyManager(connection);
            services.AddSingleton<ISecureKeyManager>(node.PublicKeyManager);
            services.AddSingleton(connection);
            services.AddSingleton<VlsChannelMappingRegistry>();
            services.AddSingleton<ILightningSigner>(sp => new VlsLightningSigner(connection,
                sp.GetRequiredService<VlsChannelMappingRegistry>(), sp.GetRequiredService<IChannelSigningInfoSource>(),
                sp.GetRequiredService<IUtxoMemoryRepository>()));
            services.AddSingleton<IVlsChannelSigner>(sp => (IVlsChannelSigner)sp.GetRequiredService<ILightningSigner>());
            services.AddSingleton<IVlsGossipSigner>(sp => (IVlsGossipSigner)sp.GetRequiredService<ILightningSigner>());
            services.AddSingleton<IRemoteSigningWorkflowCoordinator>(sp => new VlsSigningWorkflowCoordinator(connection,
                sp.GetRequiredService<IServiceScopeFactory>()));
        }, simpleTaproot: false);
        var (id, funding) = await harness.OpenAsync(LightningMoney.Satoshis(1_000_000));
        await harness.ConfirmFundingAsync(id, funding.TransactionId);
        await harness.Alice.PayAsync(harness.Bob, id, LightningMoney.Satoshis(300_000),
            invoice => approval.AuthorizeInvoice(Guid.NewGuid(), invoice.Bolt11!));
        await harness.PumpAsync();
        var before = harness.Alice.Channel(id).Commitments!.LocalBalanceMsat;
        var addsBefore = harness.Sent.Count(x => x.Message is UpdateAddHtlcMessage);
        var refusal = await Assert.ThrowsAsync<CommitmentRefusedException>(() =>
            harness.Alice.PayAsync(harness.Bob, id, LightningMoney.Satoshis(100),
                invoice => approval.AuthorizeInvoice(Guid.NewGuid(), invoice.Bolt11!)));
        Assert.StartsWith("B2-DUST-", refusal.RequirementId);
        Assert.Equal(addsBefore, harness.Sent.Count(x => x.Message is UpdateAddHtlcMessage));
        Assert.True(harness.Alice.Channel(id).Commitments!.Htlcs.IsEmpty);
        Assert.Equal(before, harness.Alice.Channel(id).Commitments!.LocalBalanceMsat);
        await harness.Alice.PayAsync(harness.Bob, id, LightningMoney.Satoshis(50_000),
            invoice => approval.AuthorizeInvoice(Guid.NewGuid(), invoice.Bolt11!));
        await harness.Alice.PayAsync(harness.Bob, id, LightningMoney.Satoshis(50_000),
            invoice => approval.AuthorizeInvoice(Guid.NewGuid(), invoice.Bolt11!));
        await harness.Bob.PayAsync(harness.Alice, id, LightningMoney.Satoshis(50_000));
        await harness.PumpAsync();
        var alice = harness.Alice.Channel(id).Commitments!;
        var bob = harness.Bob.Channel(id).Commitments!;
        Assert.True(alice.Htlcs.IsEmpty && bob.Htlcs.IsEmpty);
        Assert.Equal(before - 50_000_000, alice.LocalBalanceMsat);
        Assert.Equal(alice.LocalBalanceMsat, bob.RemoteBalanceMsat);
        Assert.Equal(alice.RemoteBalanceMsat, bob.LocalBalanceMsat);
        Assert.NotEmpty(harness.Alice.Verified);
        Assert.NotEmpty(harness.Bob.Verified);
        // Two equal-valued outputs plus an opposite-direction output are signed in one commitment.
        Assert.Contains(harness.Sent.Select(x => x.Message).OfType<CommitmentSignedMessage>(),
            message => message.Payload.HtlcSignatures.Count() >= 3);
        foreach (var node in harness.Nodes)
            foreach (var commitment in node.Verified.GroupBy(x => x.Number))
                Assert.Single(commitment.Select(x => x.TxId).Distinct());
    }
}