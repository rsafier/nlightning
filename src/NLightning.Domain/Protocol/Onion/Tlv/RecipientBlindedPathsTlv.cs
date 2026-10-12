namespace NLightning.Domain.Protocol.Onion.Tlv;

using Codecs;
using Constants;
using Models;
using Protocol.Tlv;

/// <summary>
/// Onion hop payload TLV 22 (<c>recipient_blinded_paths</c>, <c>...*payment_blinded_path</c>): the blinded paths of a
/// recipient that does not support trampoline, which the last trampoline node pays (BOLTs PR 836).
/// </summary>
/// <remarks>
/// Each element is a BOLT 4 <c>blinded_path</c> followed by its BOLT 12 <c>blinded_payinfo</c>
/// (<see cref="PaymentBlindedPathCodec"/>). <see cref="BaseTlv.Value"/> holds the wire bytes. An empty list encodes
/// and parses; the trampoline payload validator refuses it.
/// </remarks>
public class RecipientBlindedPathsTlv : BaseTlv
{
    /// <summary>
    /// The recipient's blinded paths, in wire order.
    /// </summary>
    public IReadOnlyList<WireBlindedPaymentPath> Paths { get; }

    /// <summary>
    /// Creates the TLV from <paramref name="paths"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A path cannot be encoded (see <see cref="PaymentBlindedPathCodec"/>).
    /// </exception>
    public RecipientBlindedPathsTlv(IReadOnlyList<WireBlindedPaymentPath> paths)
        : base(OnionPayloadTlvTypes.RecipientBlindedPaths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var value = PaymentBlindedPathCodec.EncodeList(paths);

        Value = value;
        Length = value.Length;
        Paths = paths.ToArray();
    }
}