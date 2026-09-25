using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Outputs;

using Domain.Money;

public class ChangeOutput : BaseOutput
{
    public ChangeOutput(Script scriptPubKey, LightningMoney? amountSats = null) : base(amountSats ?? 0UL, scriptPubKey, ScriptType.P2WPKH)
    { }
    public ChangeOutput(Script redeemScript, Script scriptPubKey, LightningMoney? amountSats = null)
        : base(amountSats ?? 0UL, redeemScript, scriptPubKey, ScriptType.P2WPKH)
    { }
}