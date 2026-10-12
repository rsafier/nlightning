using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Outputs;

using Domain.Money;
using Taproot;

/// <summary>
/// The simple taproot funding output <c>OP_1 &lt;funding_key&gt;</c>: <c>funding_key</c> is the BIP 86 tweak of the
/// MuSig2 aggregate of both funding keys (<c>KeyAgg(KeySort(pubkey1, pubkey2))</c>), spent by key path only (the
/// commitment and the cooperative close, both MuSig2-signed). The aggregation itself is the MuSig2 module's; this output
/// takes its result.
/// </summary>
public sealed class TaprootFundingOutput : BaseOutput
{
    /// <summary>The MuSig2 aggregate of the funding keys (the untweaked internal key).</summary>
    public TaprootInternalPubKey CombinedFundingKey { get; }

    /// <summary>The BIP 86 output key, with its parity.</summary>
    public TaprootFullPubKey FundingKey { get; }

    public TaprootFundingOutput(LightningMoney amount, TaprootInternalPubKey combinedFundingKey)
        : this(amount, SimpleTaprootScripts.CreateFundingOutputKey(combinedFundingKey))
    {
    }

    private TaprootFundingOutput(LightningMoney amount, TaprootFullPubKey fundingKey)
        : base(amount, Script.Empty, fundingKey.ScriptPubKey, ScriptType.Taproot)
    {
        if (amount.IsZero)
            throw new ArgumentOutOfRangeException(nameof(amount), "Funding amount must be greater than zero.");

        CombinedFundingKey = fundingKey.InternalKey;
        FundingKey = fundingKey;
    }

    /// <inheritdoc />
    /// <remarks>The funding output is spent by key path only.</remarks>
    public override ScriptCoin ToCoin() =>
        throw new NotSupportedException("The taproot funding output is spent by MuSig2 key path only");
}