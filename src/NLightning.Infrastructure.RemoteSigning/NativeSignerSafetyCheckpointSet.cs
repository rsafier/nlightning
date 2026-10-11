using System.Security.Cryptography;
using System.Text.Json;
using NLightning.Domain.Signing;

namespace NLightning.Infrastructure.RemoteSigning;

/// <summary>A signer-installed source of committed safety history, never a node-supplied checkpoint.</summary>
public sealed record NativeSignerCheckpointSource(string Name, Func<byte[]> ReadCommittedDigest);

/// <summary>Authenticates all configured safety stores together with their immutable enrollment and source names.</summary>
public sealed class NativeSignerSafetyCheckpointSet
{
    private readonly NativeSignerBinding _binding;
    private readonly NativeSignerCheckpointSource[] _sources;

    public NativeSignerSafetyCheckpointSet(NativeSignerBinding binding, DurableSignerState journal,
                                           params NativeSignerCheckpointSource[] additionalSources)
    {
        _binding = binding;
        _sources = additionalSources.Append(new NativeSignerCheckpointSource("main-journal", journal.GetCheckpointDigest))
            .OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        foreach (var source in _sources)
        {
            NodeSigningContext.ValidateIdentifier(source.Name, nameof(source.Name));
            ArgumentNullException.ThrowIfNull(source.ReadCommittedDigest);
        }
        if (_sources.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != _sources.Length)
            throw new ArgumentException("Safety checkpoint names must be unique.");
    }

    public void ValidateEnrollment(NativeSignerBinding binding)
    {
        if (binding != _binding) throw new UnauthorizedAccessException("Checkpoint sources belong to another signer enrollment.");
    }

    public string GetCheckpoint()
    {
        var histories = _sources.Select(source =>
        {
            var digest = source.ReadCommittedDigest();
            if (digest.Length != 32) throw new InvalidDataException("Safety stores must supply a committed SHA256 digest.");
            return new { source.Name, Digest = Convert.ToHexString(digest) };
        }).ToArray();
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { _binding, histories })));
    }
}