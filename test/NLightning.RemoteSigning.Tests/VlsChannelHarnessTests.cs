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

    [Fact(Explicit = true)]
    public async Task Given_VlsDefaultPolicy_When_FractionalSatoshiPaymentsFlowBothWays_Then_PeersVerifyAndExactMsatBalancesConverge()
    {
        // VLS's default policy (enforce_balance off): commitments carry millisatoshi balances and HTLC amounts, and the
        // fractional remainders go to the commitment fee exactly as every BOLT 3 implementation floors them
        var ct = TestContext.Current.CancellationToken;
        await using var gateway = new VlsGatewayFixture();
        await gateway.InitializeAsync(ct);
        var connection = new VlsSignerConnection(new VlsSignerOptions
        { SocketPath = gateway.SocketPath, TokenFile = gateway.TokenFile });
        var approval = new VlsPaymentApprovalClient(gateway.ApprovalSocketPath, gateway.ApprovalTokenFile);
        await using var harness = await TaprootOpenHarness.CreateAsync(configureServices: (node, services) =>
        {
            services.Configure<CommitSchedulerOptions>(o => o.Debounce = TimeSpan.FromSeconds(1));
            if (node.Name != "Alice") return;
            node.Options.MaxDustHtlcExposureMsat = 0;
            node.Options.HtlcMinimumAmount = LightningMoney.Satoshis(1_000);
            node.Options.Routing.FeeBaseMsat = 1_001;
            node.Options.Routing.FeeProportionalMillionths = 1_234;
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
        var start = harness.Alice.Channel(id).Commitments!.LocalBalanceMsat;

        // Act: sequential fractional payments, then fractional HTLCs in flight both ways in one commitment (all above
        // the trimming threshold: the zero-dust restriction stays)
        // Bob first needs more than his 10,000-sat channel reserve to pay back
        await harness.Alice.PayAsync(harness.Bob, id, LightningMoney.MilliSatoshis(300_000_123),
            invoice => approval.AuthorizeInvoice(Guid.NewGuid(), invoice.Bolt11!));
        await harness.PumpAsync();
        await harness.Alice.PayAsync(harness.Bob, id, LightningMoney.MilliSatoshis(10_000_001),
            invoice => approval.AuthorizeInvoice(Guid.NewGuid(), invoice.Bolt11!));
        await harness.PumpAsync();
        await harness.Bob.PayAsync(harness.Alice, id, LightningMoney.MilliSatoshis(12_345_567));
        await harness.PumpAsync();
        await harness.Alice.PayAsync(harness.Bob, id, LightningMoney.MilliSatoshis(20_000_999),
            invoice => approval.AuthorizeInvoice(Guid.NewGuid(), invoice.Bolt11!));
        await harness.Alice.PayAsync(harness.Bob, id, LightningMoney.MilliSatoshis(30_000_333),
            invoice => approval.AuthorizeInvoice(Guid.NewGuid(), invoice.Bolt11!));
        await harness.Bob.PayAsync(harness.Alice, id, LightningMoney.MilliSatoshis(40_000_777));
        await harness.PumpAsync();

        // Assert: exact msat accounting on both sides, every commitment verified by the peer
        var alice = harness.Alice.Channel(id).Commitments!;
        var bob = harness.Bob.Channel(id).Commitments!;
        Assert.True(alice.Htlcs.IsEmpty && bob.Htlcs.IsEmpty);
        Assert.Equal(start - 300_000_123 - 10_000_001 + 12_345_567 - 20_000_999 - 30_000_333 + 40_000_777, alice.LocalBalanceMsat);
        Assert.Equal(alice.LocalBalanceMsat, bob.RemoteBalanceMsat);
        Assert.Equal(alice.RemoteBalanceMsat, bob.LocalBalanceMsat);
        Assert.NotEqual(0UL, alice.LocalBalanceMsat % 1_000);
        Assert.NotEmpty(harness.Alice.Verified);
        Assert.NotEmpty(harness.Bob.Verified);
        Assert.Contains(harness.Sent.Select(x => x.Message).OfType<CommitmentSignedMessage>(),
            message => message.Payload.HtlcSignatures.Count() >= 3);
        foreach (var node in harness.Nodes)
            foreach (var commitment in node.Verified.GroupBy(x => x.Number))
                Assert.Single(commitment.Select(x => x.TxId).Distinct());
    }
}