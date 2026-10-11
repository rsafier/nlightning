namespace NLightning.Domain.Protocol.Onion.Models;

using Enums;

/// <summary>
/// A trampoline payment's failure decrypted at the origin: the layer whose key authenticated it, the erring hop's
/// position in that layer and its message.
/// </summary>
public sealed class TrampolineDecryptedFailure
{
    /// <summary>
    /// The layer whose <c>um</c> key matched: the outer route or the trampoline route.
    /// </summary>
    public TrampolineFailureLayer Layer { get; }

    /// <summary>
    /// The decrypted failure; its <see cref="DecryptedFailure.ErringHopIndex"/> is the hop's index in
    /// <see cref="Layer"/> (0 = the first outer hop, or the first trampoline node).
    /// </summary>
    public DecryptedFailure Failure { get; }

    /// <summary>
    /// The erring hop's index in <see cref="Layer"/>.
    /// </summary>
    public int ErringHopIndex => Failure.ErringHopIndex;

    /// <summary>
    /// The parsed failure message, or <c>null</c> when it could not be parsed.
    /// </summary>
    public FailureMessage? Message => Failure.Message;

    /// <summary>
    /// The failure code (see <see cref="DecryptedFailure.Code"/>).
    /// </summary>
    public FailureCode? Code => Failure.Code;

    public TrampolineDecryptedFailure(TrampolineFailureLayer layer, DecryptedFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        Layer = layer;
        Failure = failure;
    }
}