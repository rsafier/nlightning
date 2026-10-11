using System.Security.Cryptography;
using NLightning.Domain.Crypto.Hashes;
using NLightning.Tests.Utils.Mocks.Interfaces;

namespace NLightning.Tests.Utils.Mocks;

/// <summary>
/// Test double for <see cref="ISha256"/>. By default it computes a real SHA-256 over the appended data so that a bare
/// instance never produces a silently wrong (e.g. all-zero) hash. Override the virtual
/// <see cref="GetHashAndReset()"/> via Moq to force a fixed digest.
/// </summary>
public class FakeSha256 : ISha256, ITestSha256
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public void AppendData(ReadOnlySpan<byte> data)
    {
        _hash.AppendData(data);
    }

    public void GetHashAndReset(Span<byte> hash)
    {
        var result = GetHashAndReset();
        result.CopyTo(hash);
    }

    public virtual byte[] GetHashAndReset()
    {
        return _hash.GetHashAndReset();
    }

    public void Dispose()
    {
        _hash.Dispose();
        GC.SuppressFinalize(this);
    }
}