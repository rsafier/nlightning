namespace NLightning.Domain.Tests.Bitcoin.SilentPayments;

using Domain.Bitcoin.SilentPayments;
using Domain.Crypto.ValueObjects;

public class SilentPaymentInputClassifierTests
{
    private static readonly byte[] s_key = Convert.FromHexString(
        "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798");

    [Fact]
    public void Given_KeyPathWithAnnex_When_Classified_Then_UsesEvenOutputKeyWithoutMutatingWitness()
    {
        // Arrange
        var script = new byte[] { 0x51, 0x20 }.Concat(s_key[1..]).ToArray();
        byte[][] witness = [new byte[64], [0x50, 1]];
        // Act
        var accepted = SilentPaymentInputClassifier.TryGetInputPublicKey(script, [], witness, out var key);
        // Assert
        Assert.True(accepted);
        Assert.Equal(new CompactPubKey(s_key), key);
        Assert.Equal(2, witness.Length);
    }

    [Fact]
    public void Given_NumsScriptPathWithAnnex_When_Classified_Then_Ignored()
    {
        // Arrange
        var script = new byte[] { 0x51, 0x20 }.Concat(s_key[1..]).ToArray();
        var control = new byte[] { 0xc0 }.Concat(Convert.FromHexString(
            "50929b74c1a04954b78b4b6035e97a5e078a5a0f28ec96d547bfee9ace803ac0")).ToArray();
        // Act
        var accepted = SilentPaymentInputClassifier.TryGetInputPublicKey(script, [], [[0x51], control, [0x50]], out _);
        // Assert
        Assert.False(accepted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_WitnessKey_When_Classified_Then_CompressedAndCurveValidityAreRequired(bool wrapped)
    {
        // Arrange
        var script = wrapped ? new byte[] { 0xa9, 0x14 }.Concat(new byte[20]).Append((byte)0x87).ToArray()
                             : new byte[] { 0, 0x14 }.Concat(new byte[20]).ToArray();
        var scriptSig = wrapped ? new byte[] { 0x16, 0, 0x14 }.Concat(new byte[20]).ToArray() : [];
        // Act / Assert
        Assert.True(SilentPaymentInputClassifier.TryGetInputPublicKey(script, scriptSig, [[], s_key], out _));
        Assert.False(SilentPaymentInputClassifier.TryGetInputPublicKey(script, scriptSig, [[], new byte[65]], out _));
        Assert.False(SilentPaymentInputClassifier.TryGetInputPublicKey(script, scriptSig, [[], s_key], out _,
                                                                      isValidPoint: _ => false));
        if (wrapped)
            Assert.False(SilentPaymentInputClassifier.TryGetInputPublicKey(script, scriptSig[1..], [[], s_key], out _));
    }

    [Fact]
    public void Given_MalleatedP2Pkh_When_Classified_Then_LastMatchingValidKeyIsExtracted()
    {
        // Arrange
        var hash = Enumerable.Repeat((byte)7, 20).ToArray();
        var script = new byte[] { 0x76, 0xa9, 0x14 }.Concat(hash).Concat(new byte[] { 0x88, 0xac }).ToArray();
        var scriptSig = new byte[] { 1, 2, 3 }.Concat(s_key).Concat(new byte[] { 0xff, 0 }).ToArray();
        // Act
        var accepted = SilentPaymentInputClassifier.TryGetInputPublicKey(script, scriptSig, [], out var key,
            bytes => bytes.AsSpan().SequenceEqual(s_key) ? hash : new byte[20]);
        // Assert
        Assert.True(accepted);
        Assert.Equal(new CompactPubKey(s_key), key);
        Assert.False(SilentPaymentInputClassifier.TryGetInputPublicKey(script, scriptSig, [], out _));
    }

    [Theory]
    [InlineData(0x52, true)]
    [InlineData(0x60, true)]
    [InlineData(0x51, false)]
    [InlineData(0x00, false)]
    public void Given_WitnessProgram_When_FutureVersionChecked_Then_TransactionRefusalIsSeparate(int version, bool future)
    {
        // Arrange
        var script = new byte[] { (byte)version, 0x20 }.Concat(new byte[32]).ToArray();
        // Act / Assert
        Assert.Equal(future, SilentPaymentInputClassifier.IsFutureWitnessVersion(script));
        Assert.False(SilentPaymentInputClassifier.IsFutureWitnessVersion(script[..^1]));
    }
}