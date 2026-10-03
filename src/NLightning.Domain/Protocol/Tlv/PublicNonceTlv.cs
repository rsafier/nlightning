namespace NLightning.Domain.Protocol.Tlv;

using Crypto.ValueObjects;
using ValueObjects;

/// <summary>
/// A TLV whose value is one 66-byte MuSig2 public nonce (simple taproot channels): the base of
/// <see cref="NextLocalNonceTlv"/>, <see cref="ShutdownNonceTlv"/>, <see cref="NextCloseeNonceTlv"/>,
/// <see cref="FundingNonceTlv"/> and <see cref="CurrentCommitNonceTlv"/>, which only differ by their type.
/// </summary>
public abstract class PublicNonceTlv : BaseTlv
{
    /// <summary>The public nonce.</summary>
    public MusigPublicNonce Nonce { get; }

    protected PublicNonceTlv(BigSize type, MusigPublicNonce nonce) : base(type)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if ((byte[])nonce is null)
            throw new ArgumentException("The public nonce is empty.", nameof(nonce));

        Nonce = nonce;

        Value = [.. (byte[])nonce];
        Length = Value.Length;
    }
}