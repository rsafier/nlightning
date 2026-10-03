namespace NLightning.Domain.Protocol.Onion.Models;

using Constants;
using Enums;

/// <summary>
/// How a trampoline onion's <c>hop_payloads</c> length is chosen when it is built.
/// </summary>
/// <remarks>
/// BOLT 4 (PR 836) lets the sender pick the length ("its own trade-off between flexibility and privacy") and recommends
/// trailing filler when there are few hops. The filler is the payment onion's: the <c>pad</c> stream of the session
/// key, then the per-hop layers, so a padded packet peels exactly like an exact one.
/// </remarks>
public readonly record struct TrampolineOnionSizePolicy
{
    /// <summary>
    /// The mode.
    /// </summary>
    public TrampolineOnionSizeMode Mode { get; }

    /// <summary>
    /// The length for <see cref="TrampolineOnionSizeMode.Fixed"/>, the maximum for
    /// <see cref="TrampolineOnionSizeMode.Auto"/>, 0 for <see cref="TrampolineOnionSizeMode.Exact"/>.
    /// </summary>
    public int Length { get; }

    private TrampolineOnionSizePolicy(TrampolineOnionSizeMode mode, int length)
    {
        Mode = mode;
        Length = length;
    }

    /// <summary>
    /// Exactly the framed hop payloads, no filler (how the PR 836 vectors are built).
    /// </summary>
    public static TrampolineOnionSizePolicy Exact => new(TrampolineOnionSizeMode.Exact, 0);

    /// <summary>
    /// A fixed <c>hop_payloads</c> length; building fails when the framed payloads do not fit.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="hopPayloadsLength"/> is not positive.</exception>
    public static TrampolineOnionSizePolicy Fixed(int hopPayloadsLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hopPayloadsLength);
        return new TrampolineOnionSizePolicy(TrampolineOnionSizeMode.Fixed, hopPayloadsLength);
    }

    /// <summary>
    /// <see cref="TrampolineOnionConstants.RecommendedHopPayloadsLength"/> when the framed payloads fit in it and
    /// it is at most <paramref name="maxHopPayloadsLength"/>; otherwise the exact length. Building fails when the exact
    /// length exceeds <paramref name="maxHopPayloadsLength"/>.
    /// </summary>
    /// <param name="maxHopPayloadsLength">
    /// The largest length the caller can carry, typically
    /// <see cref="Interfaces.ITrampolineOnionService.GetMaxHopPayloadsLength"/> for its outer route.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="maxHopPayloadsLength"/> is not positive.</exception>
    public static TrampolineOnionSizePolicy Auto(int maxHopPayloadsLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHopPayloadsLength);
        return new TrampolineOnionSizePolicy(TrampolineOnionSizeMode.Auto, maxHopPayloadsLength);
    }

    /// <summary>
    /// The <c>hop_payloads</c> length this policy gives for payloads whose framed length is
    /// <paramref name="framedPayloadsLength"/>.
    /// </summary>
    /// <param name="framedPayloadsLength">The sum of <c>bigsize(len) + len + 32</c> over the hops.</param>
    /// <exception cref="ArgumentException">If the payloads do not fit under this policy.</exception>
    public int ResolveHopPayloadsLength(int framedPayloadsLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framedPayloadsLength);

        switch (Mode)
        {
            case TrampolineOnionSizeMode.Exact:
                return framedPayloadsLength;
            case TrampolineOnionSizeMode.Fixed:
                if (framedPayloadsLength > Length)
                    throw new ArgumentException(
                        $"The framed trampoline payloads ({framedPayloadsLength} bytes) do not fit in {Length} bytes.",
                        nameof(framedPayloadsLength));

                return Length;
            case TrampolineOnionSizeMode.Auto:
                if (framedPayloadsLength > Length)
                    throw new ArgumentException(
                        $"The framed trampoline payloads ({framedPayloadsLength} bytes) exceed the maximum of {Length}"
                      + " bytes.", nameof(framedPayloadsLength));

                return framedPayloadsLength <= TrampolineOnionConstants.RecommendedHopPayloadsLength
                    && TrampolineOnionConstants.RecommendedHopPayloadsLength <= Length
                           ? TrampolineOnionConstants.RecommendedHopPayloadsLength
                           : framedPayloadsLength;
            default:
                throw new InvalidOperationException($"Unknown trampoline onion size mode {Mode}.");
        }
    }
}