using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Bitcoin.Wallet.Models;
using NLightning.Domain.Channels.Splicing;
using NLightning.Domain.Channels.Splicing.Enums;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Crypto.Interfaces;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Money;
using NLightning.Infrastructure.Bitcoin;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.Signing.Contracts;

namespace NLightning.RemoteSigning.Tests;

public sealed class NativeNoncePersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_UnusedNativeNonce_When_SignerIsKilled_Then_OriginalNonceSignsAndExactOutcomeRecovers(bool splice)
    {
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        using var crypto = new ServiceCollection().AddBitcoinInfrastructure().BuildServiceProvider();
        var musig = crypto.GetRequiredService<IMusig2Service>();
        var channel = new ChannelId(RandomUtils.GetBytes(32));
        var funding = new TxId(RandomUtils.GetBytes(32));
        SignedTransaction unsigned;
        List<SpentOutput> spent;
        MusigPublicNonce peerNonce;
        MusigPublicNonce nonce;
        SigningRequest allocation;
        uint peerIndex;
        TxId newFunding;
        using (var connection = new RemoteSignerConnection(daemon.Options()))
        {
            var signer = new RemoteLightningSigner(connection);
            var index = signer.CreateNewChannel(out var points, out _);
            peerIndex = signer.CreateNewChannel(out var peerPoints, out _);
            signer.RegisterChannel(channel, new ChannelSigningInfo(funding, 0, LightningMoney.Satoshis(100_000), points.FundingPubKey,
                peerPoints.FundingPubKey, index)
            { IsSimpleTaproot = true });
            daemon.LocalSigner.RegisterChannel(channel, new ChannelSigningInfo(funding, 0, LightningMoney.Satoshis(100_000),
                peerPoints.FundingPubKey, points.FundingPubKey, peerIndex)
            { IsSimpleTaproot = true });
            var transaction = Network.RegTest.CreateTransaction();
            transaction.Version = 2;
            transaction.Inputs.Add(new TxIn(new OutPoint(new uint256((byte[])funding), 0)));
            if (splice)
            {
                var ours = signer.GetFundingPubKey(channel, 1);
                var theirs = daemon.LocalSigner.GetFundingPubKey(peerIndex, 1);
                transaction.Outputs.Add(Money.Satoshis(99_000), new Script(musig.AggregateTaprootKeyPath(ours, theirs).GetTaprootScriptPubKey()));
                newFunding = transaction.GetHash().ToBytes();
                signer.RegisterFunding(channel, new ChannelFunding(newFunding, 0, 99_000, ours, theirs, 1, 0, 0,
                    ChannelFundingKind.Splice, ChannelFundingStatus.Pending));
                daemon.LocalSigner.RegisterFunding(channel, new ChannelFunding(newFunding, 0, 99_000, theirs, ours, 1, 0, 0,
                    ChannelFundingKind.Splice, ChannelFundingStatus.Pending));
                peerNonce = daemon.LocalSigner.CreateSpliceFundingNonce(channel);
            }
            else
            {
                transaction.Outputs.Add(Money.Satoshis(99_000), new Key().PubKey.WitHash.ScriptPubKey);
                newFunding = transaction.GetHash().ToBytes();
                peerNonce = default;
            }
            unsigned = new SignedTransaction(newFunding, transaction.ToBytes());
            spent = [new SpentOutput(funding, 0, LightningMoney.Satoshis(100_000),
                new BitcoinScript(musig.AggregateTaprootKeyPath(points.FundingPubKey, peerPoints.FundingPubKey).GetTaprootScriptPubKey()))];
            allocation = RemoteSignerConnection.Prepare(splice ? SignerOperations.CreateSpliceFundingNonce : SignerOperations.CreateClosingNonce, channel);
            nonce = SignerWire.Read<MusigPublicNonce>(connection.Execute(allocation)[0]);
        }
        // The fixture kills the real process; no graceful in-memory transfer occurs.
        await daemon.RestartAsync();
        SigningRequest sign;
        byte[] reply;
        using (var connection = new RemoteSignerConnection(daemon.Options()))
        {
            Assert.Equal(RequestOutcome.Completed, connection.Reconcile(allocation).Outcome);
            Assert.Equal(nonce, SignerWire.Read<MusigPublicNonce>(connection.Execute(allocation)[0]));
            var signer = new RemoteLightningSigner(connection);
            if (splice)
            {
                sign = RemoteSignerConnection.Prepare(SignerOperations.SignSpliceSharedInputPartial, channel, newFunding,
                    unsigned, 0, spent, nonce, peerNonce);
                var partial = SignerWire.Read<MusigPartialSignatureWithNonce>(connection.Execute(sign)[0]);
                var peerPartial = daemon.LocalSigner.SignSpliceSharedInputPartial(channel, newFunding, unsigned, 0, spent, peerNonce, nonce);
                var signature = signer.AggregateSpliceSharedInputSignature(channel, unsigned, 0, spent, partial, peerPartial);
                var transaction = Transaction.Load(unsigned.RawTxBytes, Network.RegTest);
                transaction.Inputs[0].WitScript = new WitScript([signature]);
                var prev = new TxOut(Money.Satoshis(100_000), new Script(spent[0].ScriptPubKey));
                Assert.Null(transaction.CreateValidator([prev]).ValidateInput(0).Error);
            }
            else
            {
                var peerPartial = daemon.LocalSigner.SignClosingAsCloser(channel, unsigned, nonce);
                sign = RemoteSignerConnection.Prepare(SignerOperations.SignClosingAsClosee, channel, unsigned, nonce, peerPartial);
                var partial = SignerWire.Read<MusigPartialSignature>(connection.Execute(sign)[0]);
                daemon.LocalSigner.ValidateClosingPartialSignature(channel, unsigned, partial, nonce, peerPartial.PublicNonce);
                var transaction = signer.AggregateClosingSignature(channel, unsigned, partial, nonce, peerPartial.PartialSignature, peerPartial.PublicNonce);
                Assert.Equal(64, Transaction.Load(transaction.RawTxBytes, Network.RegTest).Inputs[0].WitScript[0].Length);
            }
            reply = connection.Reconcile(sign).Response.Payload.ToByteArray();
        }
        await daemon.RestartAsync();
        using var recovered = new RemoteSignerConnection(daemon.Options());
        Assert.Equal(RequestOutcome.Completed, recovered.Reconcile(sign).Outcome);
        Assert.Equal(reply, recovered.Reconcile(sign).Response.Payload.ToByteArray());
        Assert.Equal(reply, recovered.ExecutePayload(sign));
    }
}