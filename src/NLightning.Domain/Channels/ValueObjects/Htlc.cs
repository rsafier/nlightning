namespace NLightning.Domain.Channels.ValueObjects;

using Crypto.ValueObjects;
using Enums;
using Money;
using Protocol.Messages;

public readonly record struct Htlc
{
    public ulong Id { get; }
    public LightningMoney Amount { get; }
    public Hash PaymentHash { get; }
    public Hash? PaymentPreimage { get; }
    public uint CltvExpiry { get; }
    public HtlcState State { get; }
    public HtlcDirection Direction { get; }
    /// <summary>
    /// The <c>update_add_htlc</c> that offered this HTLC, when known. Null for HTLCs built for the commitment
    /// transaction factory from a commitment state machine spec (<c>CommitmentTxSpec.FromCommitmentSpec</c>), which
    /// carries no wire message: the factory and builders never read it (NL-244).
    /// </summary>
    public UpdateAddHtlcMessage? AddMessage { get; }
    public ulong ObscuredCommitmentNumber { get; }
    public CompactSignature? Signature { get; }

    public Htlc(LightningMoney amount, UpdateAddHtlcMessage? addMessage, HtlcDirection direction, uint cltvExpiry,
                ulong id, ulong obscuredCommitmentNumber, Hash paymentHash, HtlcState state,
                Hash? paymentPreimage = null, CompactSignature? signature = null)
    {
        Id = id;
        Amount = amount;
        PaymentHash = paymentHash;
        PaymentPreimage = paymentPreimage;
        CltvExpiry = cltvExpiry;
        State = state;
        Direction = direction;
        AddMessage = addMessage;
        ObscuredCommitmentNumber = obscuredCommitmentNumber;
        Signature = signature;
    }
}