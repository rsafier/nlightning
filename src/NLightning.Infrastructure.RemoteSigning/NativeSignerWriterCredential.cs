using System.Security.Cryptography;
using System.Text;
using NLightning.Domain.Signing;

namespace NLightning.Infrastructure.RemoteSigning;

public interface INativeSignerWriterCredentialVerifier
{
    void Verify(NativeSignerBinding binding, string writerId, long epoch, string credential);
}

public sealed class NativeSignerWriterCredential : INativeSignerWriterCredentialVerifier
{
    private readonly NativeSignerBinding _binding;
    private readonly string _writerId;
    private readonly long _epoch;
    private readonly byte[] _credentialDigest;

    public NativeSignerWriterCredential(NativeSignerBinding binding, NativeSignerExecution execution, string credential)
    {
        NodeSigningContext.ValidateIdentifier(execution.WriterId, nameof(execution.WriterId));
        if (execution.Epoch <= 0) throw new ArgumentOutOfRangeException(nameof(execution));
        if (credential.Length < 32 || credential.Any(c => c is < '!' or > '~'))
            throw new ArgumentException("Writer credentials require at least 32 printable ASCII characters without spaces.");
        _binding = binding;
        _writerId = execution.WriterId;
        _epoch = execution.Epoch;
        _credentialDigest = SHA256.HashData(Encoding.UTF8.GetBytes(credential));
    }

    public void Verify(NativeSignerBinding binding, string writerId, long epoch, string credential)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(credential));
        if (!CryptographicOperations.FixedTimeEquals(digest, _credentialDigest)
         || binding != _binding || writerId != _writerId || epoch != _epoch)
            throw new UnauthorizedAccessException("Writer credential does not authenticate this execution context.");
    }
}