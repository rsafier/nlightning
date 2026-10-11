using System.Text.Json;
using NLightning.Domain.Bitcoin.SilentPayments.Interfaces;

namespace NLightning.Infrastructure.RemoteSigning;

/// <summary>Typed receiver operations; none returns a scan or spend private key.</summary>
public static class NativeSilentPaymentOperations
{
    public const uint Metadata = 111;
    public const uint ScanSharedPoint = 112;
    public const uint LabelPoint = 113;
    public const uint LabelTweak = 114;

    public static object?[] Execute(ISilentPaymentKeySource keys, uint operation, JsonElement[] arguments) =>
        operation switch
        {
            Metadata => [new SilentPaymentReceiverMetadata(keys.ScanPubKey, keys.SpendPubKey,
                                                          keys.RecoverableElsewhere)],
            ScanSharedPoint => [SharedPoint(keys, SignerWire.Read<byte[]>(arguments[0]))],
            LabelPoint => [keys.GetLabelPoint(SignerWire.Read<uint>(arguments[0]))],
            LabelTweak => [Tweak(keys, SignerWire.Read<uint>(arguments[0]))],
            _ => throw new ArgumentException("Unknown silent payment receiver operation.", nameof(operation))
        };

    private static byte[] SharedPoint(ISilentPaymentKeySource keys, byte[] input)
    {
        var result = new byte[33];
        keys.ComputeScanSharedSecret(input, result);
        return result;
    }

    private static byte[] Tweak(ISilentPaymentKeySource keys, uint label)
    {
        var result = new byte[32];
        keys.GetLabelTweak(label, result);
        return result;
    }
}