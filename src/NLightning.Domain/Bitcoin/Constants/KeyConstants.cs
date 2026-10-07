namespace NLightning.Domain.Bitcoin.Constants;

public static class KeyConstants
{
    public const uint SilentPaymentPurpose = 352;

    /// <summary>BIP 352 account 0; coin type 0 on mainnet and 1 on test networks.</summary>
    public static string GetSilentPaymentKeyPath(bool isMainnet, bool isScan) =>
        $"m/{SilentPaymentPurpose}'/{(isMainnet ? 0 : 1)}'/0'/{(isScan ? 1 : 0)}'/0";

    public const string ChannelKeyPathString = "m/6425'/0'/0'/0";
    public const string P2TrKeyPathString = "m/86'/0'/0'";
    public const string P2WpkhKeyPathString = "m/84'/0'/0'";
}