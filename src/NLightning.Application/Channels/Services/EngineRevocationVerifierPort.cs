namespace NLightning.Application.Channels.Services;

using Domain.Channels.Commitments.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;

/// <summary>
/// The commitment state machine's <see cref="IRevocationVerifier"/> over <see cref="IPerCommitmentSecretVerifier"/>
/// (NL-230): <c>per_commitment_secret * G == per_commitment_point</c> (B2-RAA-R01). The shachain insert stays with the
/// caller (<see cref="IPerCommitmentSecretVerifier.VerifyAndStore"/> or the shachain store directly).
/// </summary>
public sealed class EngineRevocationVerifierPort : IRevocationVerifier
{
    private readonly IPerCommitmentSecretVerifier _perCommitmentSecretVerifier;

    public EngineRevocationVerifierPort(IPerCommitmentSecretVerifier perCommitmentSecretVerifier)
    {
        _perCommitmentSecretVerifier = perCommitmentSecretVerifier;
    }

    /// <inheritdoc />
    public bool IsValidSecret(Secret perCommitmentSecret, CompactPubKey expectedPerCommitmentPoint) =>
        _perCommitmentSecretVerifier.Verify(perCommitmentSecret, expectedPerCommitmentPoint);
}