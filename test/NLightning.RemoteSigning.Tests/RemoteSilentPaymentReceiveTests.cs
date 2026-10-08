using System.Buffers.Binary;
using System.Security.Cryptography;
using NBitcoin;
using NLightning.Domain.Bitcoin.SilentPayments.Models;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Infrastructure.Bitcoin.Crypto.SilentPayments;
using NLightning.Infrastructure.RemoteSigning;

namespace NLightning.RemoteSigning.Tests;

public sealed class RemoteSilentPaymentReceiveTests(SignerDaemonFixture daemon) : IClassFixture<SignerDaemonFixture>
{
    [Theory]
    [InlineData(null)]
    [InlineData(0u)]
    [InlineData(17u)]
    public void ActualSignerFindsTheSenderOutputWithoutExportingReceiverPrivateKeys(uint? label)
    {
        using var connection = new RemoteSignerConnection(daemon.Options());
        var receiver = new RemoteSilentPaymentKeySource(connection);
        var crypto = new SilentPaymentCrypto();
        using var sender = new Key();
        var secret = sender.ToBytes();
        var outpoint = new byte[36];
        RandomNumberGenerator.Fill(outpoint.AsSpan(0, 32));
        BinaryPrimitives.WriteUInt32LittleEndian(outpoint.AsSpan(32), 3);
        var shared = new byte[33];
        try
        {
            var spend = receiver.SpendPubKey;
            var labels = new Dictionary<uint, CompactPubKey>();
            if (label is { } index)
            {
                labels[index] = receiver.GetLabelPoint(index);
                Assert.True(crypto.TrySumPublicKeys([spend, labels[index]], out spend));
            }
            var output = Assert.Single(crypto.DeriveOutputs(
                [new SilentPaymentSenderInput(outpoint, secret, false)],
                [new SilentPaymentRecipient(receiver.ScanPubKey, spend)]));
            var sum = new CompactPubKey(sender.PubKey.ToBytes());
            var tweaked = crypto.TweakInputPublicKey(sum, crypto.ComputeInputHash(outpoint, sum));
            receiver.ComputeScanSharedSecret(tweaked, shared);
            var found = Assert.Single(crypto.Scan(shared, receiver.SpendPubKey,
                [new SilentPaymentScanCandidate(4, output.OutputKey32)], labels));
            Assert.Equal(4u, found.OutputIndex);
            Assert.Equal(label, found.Label);
            Assert.Equal(output.OutputKey32, found.OutputKey32);
            Assert.Throws<NotSupportedException>(() =>
                new RemoteSecureKeyManager(connection).GetSilentPaymentSpendKey(found.Tweak32, found.Label));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(17u)]
    [InlineData(uint.MaxValue)]
    public void LabelTweakAndPublicPointMatchInsideTheRealSigner(uint label)
    {
        using var connection = new RemoteSignerConnection(daemon.Options());
        var receiver = new RemoteSilentPaymentKeySource(connection);
        var tweak = new byte[32];
        var expected = new byte[32];
        try
        {
            receiver.GetLabelTweak(label, tweak);
            daemon.LocalKeys.GetLabelTweak(label, expected);
            Assert.Equal(expected, tweak);
            Assert.Equal(daemon.LocalKeys.GetLabelPoint(label), receiver.GetLabelPoint(label));
            Assert.Equal(daemon.LocalKeys.ScanPubKey, receiver.ScanPubKey);
            Assert.Equal(daemon.LocalKeys.SpendPubKey, receiver.SpendPubKey);
            Assert.Equal(daemon.LocalKeys.RecoverableElsewhere, receiver.RecoverableElsewhere);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tweak);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    [Fact]
    public async Task ReceiverOperationsRemainStableAcrossSignerRestart()
    {
        CompactPubKey before;
        using (var connection = new RemoteSignerConnection(daemon.Options()))
            before = new RemoteSilentPaymentKeySource(connection).ScanPubKey;
        await daemon.RestartAsync();
        using var restarted = new RemoteSignerConnection(daemon.Options());
        var receiver = new RemoteSilentPaymentKeySource(restarted);
        using var generator = new Key(Convert.FromHexString(new string('0', 63) + "1"));
        var shared = new byte[33];
        receiver.ComputeScanSharedSecret(generator.PubKey.ToBytes(), shared);
        Assert.Equal((byte[])before, shared);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(65)]
    public void InvalidScanInputsAreRejectedByTheRealSigner(int length)
    {
        using var connection = new RemoteSignerConnection(daemon.Options());
        var receiver = new RemoteSilentPaymentKeySource(connection);
        Assert.Throws<ArgumentException>(() => receiver.ComputeScanSharedSecret(new byte[length], new byte[33]));
        Assert.Equal(daemon.LocalKeys.ScanPubKey, receiver.ScanPubKey);
    }
}