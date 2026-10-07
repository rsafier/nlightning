namespace NLightning.Domain.Bitcoin.Wallet.Models;

using Enums;

public sealed class WalletAddressModel
{
    public AddressType AddressType { get; }
    public uint Index { get; }
    public bool IsChange { get; }
    public string Address { get; }
    public uint AccountIndex { get; init; }
    public uint? DerivationIndex { get; init; }
    public string AccountName { get; init; } = "default";

    /// <summary>
    /// Reserved for a use that owns it (a channel's <c>upfront_shutdown_script</c>, NL-045): the wallet never hands it
    /// out as an unused address again.
    /// </summary>
    public bool IsReserved { get; init; }

    public WalletAddressModel(AddressType addressType, uint index, bool isChange, string address)
    {
        AddressType = addressType;
        Index = index;
        IsChange = isChange;
        Address = address;
    }

    public override string ToString()
    {
        return Address;
    }
}