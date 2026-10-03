using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

/// <summary>
/// Converts a TLV whose value is one 66-byte MuSig2 public nonce (<see cref="PublicNonceTlv"/>); the subclasses only
/// name the type and build the TLV.
/// </summary>
public abstract class PublicNonceTlvConverter<TTlv> : ITlvConverter<TTlv> where TTlv : PublicNonceTlv
{
    /// <summary>The TLV type this converter reads.</summary>
    protected abstract BigSize TlvType { get; }

    /// <summary>Builds the TLV from its nonce.</summary>
    protected abstract TTlv Create(MusigPublicNonce nonce);

    public BaseTlv ConvertToBase(TTlv tlv)
    {
        return tlv;
    }

    public TTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != TlvType)
            throw new InvalidCastException("Invalid TLV type");

        if (baseTlv.Length != MusigConstants.PublicNonceLen || baseTlv.Value.Length != baseTlv.Length)
            throw new InvalidCastException(
                $"Invalid length: a public nonce TLV holds {MusigConstants.PublicNonceLen} bytes, not "
              + $"{baseTlv.Value.Length}");

        return Create(new MusigPublicNonce(baseTlv.Value.ToArray()));
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as TTlv
                          ?? throw new InvalidCastException($"Error converting BaseTlv to {typeof(TTlv).Name}"));
    }
}

/// <summary>Converts <c>next_local_nonce</c> (type 4 of open_channel, accept_channel and channel_ready).</summary>
public sealed class NextLocalNonceTlvConverter : PublicNonceTlvConverter<NextLocalNonceTlv>
{
    protected override BigSize TlvType => TaprootTlvConstants.NextLocalNonce;
    protected override NextLocalNonceTlv Create(MusigPublicNonce nonce) => new(nonce);
}

/// <summary>Converts <c>shutdown_nonce</c> (type 8 of shutdown).</summary>
public sealed class ShutdownNonceTlvConverter : PublicNonceTlvConverter<ShutdownNonceTlv>
{
    protected override BigSize TlvType => TaprootTlvConstants.ShutdownNonce;
    protected override ShutdownNonceTlv Create(MusigPublicNonce nonce) => new(nonce);
}

/// <summary>Converts <c>next_closee_nonce</c> (type 22 of closing_sig).</summary>
public sealed class NextCloseeNonceTlvConverter : PublicNonceTlvConverter<NextCloseeNonceTlv>
{
    protected override BigSize TlvType => TaprootTlvConstants.NextCloseeNonce;
    protected override NextCloseeNonceTlv Create(MusigPublicNonce nonce) => new(nonce);
}

/// <summary>Converts <c>funding_nonce</c> (type 6 of tx_complete, BOLTs PR #1324).</summary>
public sealed class FundingNonceTlvConverter : PublicNonceTlvConverter<FundingNonceTlv>
{
    protected override BigSize TlvType => TaprootTlvConstants.FundingNonce;
    protected override FundingNonceTlv Create(MusigPublicNonce nonce) => new(nonce);
}

/// <summary>Converts <c>current_commit_nonce</c> (type 24 of channel_reestablish, BOLTs PR #1324).</summary>
public sealed class CurrentCommitNonceTlvConverter : PublicNonceTlvConverter<CurrentCommitNonceTlv>
{
    protected override BigSize TlvType => TaprootTlvConstants.CurrentCommitNonce;
    protected override CurrentCommitNonceTlv Create(MusigPublicNonce nonce) => new(nonce);
}