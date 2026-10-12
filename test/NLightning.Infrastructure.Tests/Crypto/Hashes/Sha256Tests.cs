namespace NLightning.Infrastructure.Tests.Crypto.Hashes;

using Infrastructure.Crypto.Hashes;

/// <summary>
/// Test our SHA256 implementation against the test vector available at
/// <see href="https://csrc.nist.gov/projects/cryptographic-algorithm-validation-program/secure-hashing">NIST</see>.
/// </summary>
public class Sha256Tests
{
    [Fact]
    public void Given_NistVectorInputs_When_DataIsHashed_Then_ResultIsKnown()
    {
        var testVectors = ReadTestVectors("Crypto/Vectors/SHA256LongMsg.rsp");
        using var sha256 = new Sha256();
        Span<byte> result = new byte[32];

        foreach (var vector in testVectors)
        {
            sha256.AppendData(vector.Msg);
            sha256.GetHashAndReset(result);

            Assert.Equal(vector.Md, result.ToArray());
        }
    }

    [Fact]
    public void Given_ADisposedSha256_When_DisposedAgainFromManyThreads_Then_TheStateIsFreedOnce()
    {
        // Arrange: a second free of the libsodium state crashed the process (NL-560)
        var sha256 = new Sha256();
        sha256.AppendData("nltg"u8);
        sha256.Dispose();

        // Act
        Parallel.For(0, 16, _ => sha256.Dispose());

        // Assert: reaching this line is the proof; a new instance still hashes
        using var fresh = new Sha256();
        Span<byte> result = stackalloc byte[32];
        fresh.GetHashAndReset(result);
        Assert.Equal(Convert.FromHexString("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"),
                     result.ToArray());
    }

    private class TestVector(int len)
    {
        public int Len { get; } = len;
        public byte[]? Msg { get; set; }
        public byte[]? Md { get; set; }
    }

    private static List<TestVector> ReadTestVectors(string filePath)
    {
        var testVectors = new List<TestVector>();
        TestVector? currentVector = null;

        foreach (var line in File.ReadLines(filePath))
        {
            if (line.StartsWith("Len = "))
            {
                currentVector = new TestVector(int.Parse(line[6..]));
            }
            else if (line.StartsWith("Msg = "))
            {
                if (currentVector == null)
                {
                    throw new InvalidOperationException("Msg line without Len line");
                }

                currentVector.Msg = Convert.FromHexString(line[6..]);

                if (currentVector.Msg.Length != currentVector.Len / 8)
                {
                    throw new InvalidOperationException("Msg length does not match Len");
                }
            }
            else if (line.StartsWith("MD = "))
            {
                if (currentVector == null)
                {
                    throw new InvalidOperationException("MD line without Len line");
                }

                if (currentVector.Msg == null)
                {
                    throw new InvalidOperationException("MD line without Msg line");
                }

                currentVector.Md = Convert.FromHexString(line[5..]);
                testVectors.Add(currentVector);
            }
        }

        return testVectors;
    }
}