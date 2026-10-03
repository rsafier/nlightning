using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Outputs;

using Domain.Money;

public class HtlcResolutionOutput : BaseOutput
{
    public PubKey RevocationPubKey { get; }
    public PubKey LocalDelayedPubKey { get; }
    public ulong ToSelfDelay { get; }

    public HtlcResolutionOutput(LightningMoney amount, PubKey localDelayedPubKey, PubKey revocationPubKey,
                                ulong toSelfDelay)
        : base(amount, GenerateHtlcOutputScript(localDelayedPubKey, revocationPubKey, toSelfDelay),
               ScriptType.P2WSH)
    {
        RevocationPubKey = revocationPubKey;
        LocalDelayedPubKey = localDelayedPubKey;
        ToSelfDelay = toSelfDelay;
    }

    private static Script GenerateHtlcOutputScript(PubKey localDelayedPubKey, PubKey revocationPubKey,
                                                   ulong toSelfDelay)
    {
        return new Script(
            OpcodeType.OP_IF,
            Op.GetPushOp(revocationPubKey.ToBytes()),
            OpcodeType.OP_ELSE,
            Op.GetPushOp((long)toSelfDelay),
            OpcodeType.OP_CHECKSEQUENCEVERIFY,
            OpcodeType.OP_DROP,
            Op.GetPushOp(localDelayedPubKey.ToBytes()),
            OpcodeType.OP_ENDIF,
            OpcodeType.OP_CHECKSIG
        );
    }
}