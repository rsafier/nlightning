namespace NLightning.Application.Channels.Splicing.Exceptions;

/// <summary>
/// The peer's <c>commitment_signed</c> for a splice's new funding does not verify (SP-CS-02). Before our
/// <c>tx_signatures</c> the negotiation ends with our <c>tx_abort</c>; the channel itself is untouched.
/// </summary>
public sealed class SpliceCommitmentException : Exception
{
    public SpliceCommitmentException(string message) : base(message)
    {
    }

    public SpliceCommitmentException(string message, Exception innerException) : base(message, innerException)
    {
    }
}