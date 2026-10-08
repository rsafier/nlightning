using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NLightning.Application.Gossip.Announcements;
using NLightning.Application.Tests.Channels.Taproot;
using NLightning.Domain.Bitcoin.Enums;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Bitcoin.Wallet.Models;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Exceptions;
using NLightning.Domain.Money;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Protocol.ValueObjects;
using NLightning.Domain.Signing.Recovery;
using NLightning.Domain.Signing.Vls;
using NLightning.Infrastructure.Bitcoin.Gossip;
using NLightning.Infrastructure.Bitcoin.Signers;
using NLightning.Infrastructure.Repositories.Memory;
using NLightning.Infrastructure.VlsSigning;

namespace NLightning.RemoteSigning.Tests;

/// <summary>
/// Public channels, withdrawals and signmessage through the actual pinned VLS gateway (NL-1335). Every signature is
/// checked independently of VLS: gossip signatures with the gossip verifier, the withdrawal with NBitcoin's script
/// interpreter and the message signature by key recovery.
/// </summary>
public sealed class VlsPublicSigningTests
{
    [Fact(Explicit = true)]
    public async Task Given_VlsSigner_When_SigningALightningMessage_Then_TheLndStyleSignatureRecoversTheNodeKey()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var gateway = new VlsGatewayFixture();
        await gateway.InitializeAsync(ct);
        var (connection, signer) = CreateSigner(gateway, new UtxoMemoryRepository());
        var message = Encoding.UTF8.GetBytes("VLS signmessage acceptance");

        // Act
        var signature = signer.SignLightningMessage(message, singleHash: false);

        // Assert: LND's format (header 31 + recovery id, then r || s) over SHA256d of the prefixed message
        Assert.Equal(LightningMessageSignature.Length, signature.Length);
        Assert.InRange(signature[0], 31, 34);
        Assert.Equal(connection.Identity.NodePublicKey, LightningMessageSignature.Recover(message, signature));
        // Any message recovers some key: only the signed one recovers ours
        Assert.NotEqual(connection.Identity.NodePublicKey, LightningMessageSignature.Recover("another message"u8, signature));
        Assert.Throws<NotSupportedException>(() => signer.SignLightningMessage(message, singleHash: true));
    }

    [Fact(Explicit = true)]
    public async Task Given_ReservedVlsWalletInputs_When_TheOperatorAllowlistsTheDestination_Then_VlsSignsAScriptValidWithdrawal()
    {
        // Arrange: one confirmed P2WPKH output of the VLS wallet, reserved for this withdrawal
        var ct = TestContext.Current.CancellationToken;
        await using var gateway = new VlsGatewayFixture();
        await gateway.InitializeAsync(ct);
        var wallet = new UtxoMemoryRepository();
        var (connection, signer) = CreateSigner(gateway, wallet);
        var keys = new VlsSecureKeyManager(connection);
        var receive = WalletScript(keys, 0, false);
        var change = WalletScript(keys, 0, true);
        var funding = new TxId(Enumerable.Repeat((byte)0x7a, 32).ToArray());
        const long inputSat = 1_000_000;
        wallet.Add(new UtxoModel(funding, 1, LightningMoney.Satoshis(inputSat), 100,
                                 new WalletAddressModel(AddressType.P2Wpkh, 0, false,
                                                        receive.GetDestinationAddress(Network.RegTest)!.ToString())));
        var reservation = Guid.NewGuid();
        Assert.True(wallet.TryReserveForFee([(funding, 1u)], reservation));
        var destination = new Key().PubKey.WitHash.GetAddress(Network.RegTest);
        var tx = Network.RegTest.CreateTransaction();
        tx.Version = 2;
        tx.Inputs.Add(new TxIn(new OutPoint(new uint256((byte[])funding), 1)) { Sequence = new Sequence(0xFFFFFFFD) });
        tx.Outputs.Add(Money.Satoshis(400_000), destination.ScriptPubKey);
        tx.Outputs.Add(Money.Satoshis(inputSat - 400_000 - 1_000), change);

        // Act and assert: an unknown destination is refused by VLS's on-chain policy
        Assert.ThrowsAny<Exception>(() => signer.SignWalletTransaction(Unsigned(tx), reservation, []));
        // ... another reservation's spend and a non-wallet input are refused before VLS
        Assert.Throws<SignerException>(() => signer.SignWalletTransaction(Unsigned(tx), Guid.NewGuid(), []));
        Assert.Throws<NotSupportedException>(() => signer.SignWalletTransaction(
            Unsigned(tx), reservation, [new SpentOutput(funding, 9, LightningMoney.Satoshis(1), new BitcoinScript([0x51]))]));
        // ... the node credential cannot allowlist
        var byNode = await gateway.ExchangeAsync(new { op = "allowlist_address", address = destination.ToString() },
                                                 ct: ct);
        Assert.False(byNode.GetProperty("ok").GetBoolean());
        Assert.Contains("forbidden", byNode.GetProperty("error").GetString());

        // Act: the operator allowlists the destination through the approval socket
        new VlsWalletApprovalClient(gateway.ApprovalSocketPath, gateway.ApprovalTokenFile)
           .AllowlistAddress(Guid.NewGuid(), destination.ToString());
        var signed = Unsigned(tx);
        Assert.True(signer.SignWalletTransaction(signed, reservation, []));

        // Assert: Bitcoin's script interpreter accepts the witness; outputs and txid are unchanged
        var result = Transaction.Load(signed.RawTxBytes, Network.RegTest);
        Assert.Equal(tx.GetHash(), result.GetHash());
        var validator = result.CreateValidator([new TxOut(Money.Satoshis(inputSat), receive)]);
        Assert.True(validator.ValidateInput(0).Error is null or ScriptError.OK);

        // The allowlist survives a gateway restart with the policy state
        await gateway.RestartAsync(ct);
        var (_, restarted) = CreateSigner(gateway, wallet);
        Assert.True(restarted.SignWalletTransaction(Unsigned(tx), reservation, []));
    }

    [Fact(Explicit = true)]
    public async Task Given_APublicVlsChannel_When_SigningItsAnnouncement_Then_BothSignaturesVerifyAndVlsBindsTheChannel()
    {
        // Arrange: a real VLS channel (Alice) with a local-signer peer (Bob), funded and confirmed
        var ct = TestContext.Current.CancellationToken;
        await using var gateway = new VlsGatewayFixture();
        await gateway.InitializeAsync(ct);
        var connection = new VlsSignerConnection(new VlsSignerOptions
        { SocketPath = gateway.SocketPath, TokenFile = gateway.TokenFile });
        await using var harness = await TaprootOpenHarness.CreateAsync(configureServices: (node, services) =>
        {
            if (node.Name != "Alice") return;
            node.Options.MaxDustHtlcExposureMsat = 0;
            node.Options.HtlcMinimumAmount = LightningMoney.Satoshis(1_000);
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
        var channel = harness.Alice.Channel(id);
        var signer = Assert.IsType<VlsLightningSigner>(harness.Alice.Services.GetRequiredService<ILightningSigner>());
        var scid = new ShortChannelId(TaprootOpenHarness.FundingHeight, 1, channel.FundingOutput!.Index!.Value);
        var unsigned = ChannelAnnouncementBuilder.BuildUnsigned(channel, scid, signer.GetNodePublicKey(),
                                                                BitcoinNetwork.Regtest.ChainHash);

        // Act and assert: the harness channel is private, so its announcement is refused
        Assert.Throws<SignerException>(() => signer.SignChannelAnnouncement(id, unsigned.GetSignedData(), scid));

        // Act: the same channel registered as announced with its confirmed short channel id
        signer.RegisterChannel(id, channel.GetSigningInfo() with { AnnounceChannel = true, ShortChannelId = scid });
        var signatures = signer.SignChannelAnnouncement(id, unsigned.GetSignedData(), scid);

        // Assert: our node and funding keys signed SHA256d of the announcement (BOLT 7), checked independently
        var hash = new NLightning.Domain.Crypto.ValueObjects.Hash(unsigned.GetSignatureHash());
        var verifier = new GossipSignatureVerifier();
        Assert.True(verifier.Verify(hash, signatures.NodeSignature, signer.GetNodePublicKey()));
        Assert.True(verifier.Verify(hash, signatures.BitcoinSignature, channel.LocalFundingPubKey));
        Assert.Throws<SignerException>(() => signer.SignChannelAnnouncement(
            id, unsigned.GetSignedData(), new ShortChannelId(TaprootOpenHarness.FundingHeight + 1, 1, scid.OutputIndex)));

        // Act and assert: VLS refuses, by itself, an announcement whose funding keys are not the channel's
        var data = unsigned.GetSignedData();
        var keys = data.Length - 66;
        var swapped = data[..keys].Concat(data[(keys + 33)..]).Concat(data[keys..(keys + 33)]).ToArray();
        var mapping = harness.Alice.Services.GetRequiredService<VlsChannelMappingRegistry>().GetByChannelId(id);
        var refused = await gateway.ExchangeAsync(new
        {
            op = "sign_channel_announcement",
            channel = Convert.ToHexString(mapping.VlsChannelId!).ToLowerInvariant(),
            payload = Convert.ToHexString(swapped).ToLowerInvariant()
        }, ct: ct);
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Contains("does not match the channel", refused.GetProperty("error").GetString());
    }

    private static (VlsSignerConnection Connection, VlsLightningSigner Signer) CreateSigner(VlsGatewayFixture gateway,
        IUtxoMemoryRepository wallet)
    {
        var connection = new VlsSignerConnection(new VlsSignerOptions
        { SocketPath = gateway.SocketPath, TokenFile = gateway.TokenFile });
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return (connection, new VlsLightningSigner(connection, new VlsChannelMappingRegistry(connection, scopes),
                                                   wallet: wallet));
    }

    private static Script WalletScript(VlsSecureKeyManager keys, uint index, bool change) =>
        new PubKey((byte[])keys.GetWalletPublicKey(index, change, AddressType.P2Wpkh)).WitHash.ScriptPubKey;

    private static SignedTransaction Unsigned(Transaction tx) =>
        new(new TxId(tx.GetHash().ToBytes()), tx.ToBytes());
}