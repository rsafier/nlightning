namespace NLightning.LndGrpc.Mapping;

using Domain.Node;
using Lnrpc;

/// <summary>
/// Feature bits as LND reports them (<c>map&lt;uint32, Feature&gt;</c>: name, <c>is_required</c> for an even bit,
/// <c>is_known</c>): the names of LND's <c>lnwire.Features</c> at v0.21.4, then the BOLT names of bits LND does not
/// name. An unnamed bit is reported with an empty name and <c>is_known</c> false, as LND does.
/// </summary>
public static class LndFeatures
{
    private static readonly Dictionary<int, string> s_names = new()
    {
        [0] = "data-loss-protect",
        [3] = "initial-routing-sync",
        [4] = "upfront-shutdown-script",
        [6] = "gossip-queries",
        [8] = "tlv-onion",
        [10] = "gossip-queries-ex",
        [12] = "static-remote-key",
        [14] = "payment-addr",
        [16] = "multi-path-payments",
        [18] = "wumbo-channels",
        [20] = "anchor-commitments",
        [22] = "anchors-zero-fee-htlc-tx",
        [24] = "route-blinding",
        [26] = "shutdown-any-segwit",
        [28] = "dual-fund",
        [30] = "amp",
        [34] = "quiescence",
        [36] = "attribution-data",
        [38] = "onion-messages",
        [42] = "provide-storage",
        [44] = "explicit-commitment-type",
        [46] = "scid-alias",
        [48] = "payment-metadata",
        [50] = "zero-conf",
        [54] = "keysend",
        [56] = "trampoline-routing",
        [60] = "rbf-coop-close",
        [62] = "splice",
        [70] = "gossip-v2",
        [80] = "simple-taproot-chans",
        [180] = "simple-taproot-chans-x",
        [262] = "bolt-11-blinded-paths",
        [2022] = "script-enforced-lease"
    };

    /// <summary>The name of <paramref name="bit"/> (either bit of the pair), or empty.</summary>
    public static string NameOf(int bit) =>
        s_names.TryGetValue(bit & ~1, out var name) ? name
        : bit == 3 ? s_names[3]
        : string.Empty;

    /// <summary>The LND feature map of <paramref name="features"/>.</summary>
    public static IEnumerable<KeyValuePair<uint, Feature>> ToMap(FeatureSet? features)
    {
        if (features is null)
            yield break;

        foreach (var bit in features.GetSetBits())
        {
            var name = NameOf(bit);
            yield return new KeyValuePair<uint, Feature>((uint)bit, new Feature
            {
                Name = name,
                IsRequired = bit % 2 == 0,
                IsKnown = name.Length > 0
            });
        }
    }

    /// <summary>The LND feature map of raw wire feature bytes (big-endian, as in gossip).</summary>
    public static IEnumerable<KeyValuePair<uint, Feature>> ToMap(ReadOnlySpan<byte> wireFeatures)
    {
        var bits = new List<int>();
        for (var i = 0; i < wireFeatures.Length; i++)
        {
            var value = wireFeatures[wireFeatures.Length - 1 - i];
            for (var b = 0; b < 8; b++)
            {
                if ((value & (1 << b)) != 0)
                    bits.Add(i * 8 + b);
            }
        }

        return bits.Select(bit =>
        {
            var name = NameOf(bit);
            return new KeyValuePair<uint, Feature>((uint)bit, new Feature
            {
                Name = name,
                IsRequired = bit % 2 == 0,
                IsKnown = name.Length > 0
            });
        });
    }
}