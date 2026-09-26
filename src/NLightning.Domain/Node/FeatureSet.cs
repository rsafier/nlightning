using System.Collections;
using System.Runtime.Serialization;
using System.Text;

namespace NLightning.Domain.Node;

using Domain.Utils.Interfaces;
using Enums;

/// <summary>
/// Represents the features supported by a node. <see href="https://github.com/lightning/bolts/blob/master/09-features.md">BOLT-9</see>
/// </summary>
public class FeatureSet
{
    /// <summary>
    /// Some features are dependent on other features. This dictionary contains the dependencies, as listed in the
    /// Dependencies column of BOLT 9.
    /// </summary>
    /// <remarks>
    /// Dependencies that BOLT 9 no longer lists because the dependency became ASSUMED (e.g. anchors ->
    /// static_remotekey, payment_secret -> var_onion_optin) are intentionally not listed: peers may omit ASSUMED bits,
    /// so requiring them would disconnect spec-compliant peers. The one exception is zero_fee_commitments ->
    /// option_channel_type, which the current BOLT 9 table still lists even though option_channel_type is ASSUMED.
    /// gossip_queries_ex no longer depends on gossip_queries.
    /// </remarks>
    private static readonly Dictionary<Feature, Feature[]> s_featureDependencies = new()
    {
        // This \/ --- Depends on this \/
        { Feature.BasicMpp, [Feature.PaymentSecret] },
        { Feature.ZeroFeeCommitments, [Feature.OptionChannelType] },
        { Feature.OptionZeroconf, [Feature.OptionScidAlias] },
        { Feature.OptionSimpleClose, [Feature.OptionShutdownAnySegwit] },
        { Feature.OptionOnionMessagesOnlyChannels, [Feature.OptionOnionMessages] },
    };

    /// <summary>
    /// Features BOLT 9 marks ASSUMED: every node is assumed to support them, so a peer that omits them is treated as
    /// supporting them (optional) during negotiation.
    /// </summary>
    private static readonly HashSet<Feature> s_assumedFeatures =
    [
        Feature.OptionDataLossProtect,
        Feature.VarOnionOptin,
        Feature.OptionStaticRemoteKey,
        Feature.PaymentSecret,
        Feature.OptionChannelType
    ];

    private const FeatureContext InitAndNode = FeatureContext.Init | FeatureContext.NodeAnnouncement;

    /// <summary>
    /// The contexts each known feature may be presented in (the Context column of BOLT 9).
    /// </summary>
    /// <remarks>
    /// ASSUMED features keep the contexts of the last spec revision that defined them, because implementations still
    /// require some of them (e.g. data_loss_protect) in <c>init</c>.
    /// </remarks>
    private static readonly Dictionary<Feature, FeatureContext> s_featureContexts = new()
    {
        { Feature.OptionDataLossProtect, InitAndNode },
        { Feature.OptionUpfrontShutdownScript, InitAndNode },
        { Feature.GossipQueries, InitAndNode },
        { Feature.VarOnionOptin, InitAndNode | FeatureContext.Invoice },
        { Feature.GossipQueriesEx, InitAndNode },
        { Feature.OptionStaticRemoteKey, InitAndNode | FeatureContext.ChannelType },
        { Feature.PaymentSecret, InitAndNode | FeatureContext.Invoice },
        { Feature.BasicMpp, InitAndNode | FeatureContext.Invoice },
        { Feature.OptionSupportLargeChannel, InitAndNode },
        { Feature.OptionAnchors, InitAndNode | FeatureContext.ChannelType },
        { Feature.OptionRouteBlinding, InitAndNode | FeatureContext.Invoice },
        { Feature.OptionShutdownAnySegwit, InitAndNode },
        { Feature.OptionDualFund, InitAndNode },
        { Feature.OptionQuiesce, InitAndNode },
        { Feature.OptionAttributionData, InitAndNode | FeatureContext.Invoice },
        { Feature.OptionOnionMessages, InitAndNode },
        { Feature.ZeroFeeCommitments, InitAndNode },
        { Feature.OptionProvideStorage, InitAndNode },
        { Feature.OptionChannelType, InitAndNode },
        { Feature.OptionScidAlias, InitAndNode | FeatureContext.ChannelType },
        { Feature.OptionPaymentMetadata, FeatureContext.Invoice },
        { Feature.OptionZeroconf, InitAndNode | FeatureContext.ChannelType },
        { Feature.OptionSimpleClose, InitAndNode },
        { Feature.OptionSplice, InitAndNode },
        { Feature.OptionOnionMessagesOnlyChannels, InitAndNode },
    };

    internal BitArray FeatureFlags;

    /// <summary>
    /// Initializes a new instance of the <see cref="FeatureSet"/> class.
    /// </summary>
    /// <remarks>
    /// Always set the bit of <see cref="Feature.VarOnionOptin"/> as Optional.
    /// </remarks>
    public FeatureSet()
    {
        FeatureFlags = new BitArray(128);
        // Always set the compulsory bit of option_data_loss_protect
        SetFeature(Feature.OptionDataLossProtect, true);
        // Always set the compulsory bit of var_onion_optin
        SetFeature(Feature.VarOnionOptin, true);
        // Always set the compulsory bit of option_static_remote_key
        SetFeature(Feature.OptionStaticRemoteKey, true);
        // Always set the compulsory bit of payment_secret
        SetFeature(Feature.PaymentSecret, true);
        // Always set the compulsory bit for option_channel_type
        SetFeature(Feature.OptionChannelType, true);
    }

    public static FeatureSet NewBasicChannelType()
    {
        // Initialize a new FeatureSet with only OptionStaticRemoteKey set as compulsory
        var featureFlagsForChannelType = DeserializeFromBytes([0b0001_0000, 0b0000_0000]);
        return featureFlagsForChannelType;
    }

    public event EventHandler? Changed;

    /// <summary>
    /// Gets the last index-of-one in the BitArray and add 1 because arrays starts at 0.
    /// </summary>
    public int SizeInBits => GetLastIndexOfOne(FeatureFlags);

    /// <summary>
    /// Sets a feature.
    /// </summary>
    /// <param name="feature">The feature to set.</param>
    /// <param name="isCompulsory">If the feature is compulsory.</param>
    /// <param name="isSet">true to set the feature, false to unset it</param>
    /// <remarks>
    /// If the feature has dependencies, they will be set first.
    /// A dependency that is not set yet is set with the same isCompulsory value as the feature being set; one that is
    /// already set is only ever upgraded to compulsory, never downgraded to optional.
    /// </remarks>
    public void SetFeature(Feature feature, bool isCompulsory, bool isSet = true)
    {
        // If we're setting the feature, and it has dependencies, set them first
        if (isSet)
        {
            if (s_featureDependencies.TryGetValue(feature, out var dependencies))
            {
                foreach (var dependency in dependencies)
                {
                    if (!HasFeature(dependency) || (isCompulsory && !IsFeatureSet(dependency, true)))
                        SetFeature(dependency, isCompulsory, isSet);
                }
            }
        }
        else // If we're unsetting the feature, and it has dependents, unset them first
        {
            foreach (var dependent in s_featureDependencies.Where(x => x.Value.Contains(feature)).Select(x => x.Key))
                SetFeature(dependent, isCompulsory, isSet);
        }

        var bitPosition = (int)feature;

        if (isCompulsory)
        {
            // Unset the non-compulsory bit
            SetFeature(bitPosition, false);
            --bitPosition;
        }
        else
        {
            // Unset the compulsory bit
            SetFeature(bitPosition - 1, false);
        }

        // Then set the feature itself
        SetFeature(bitPosition, isSet);
    }

    /// <summary>
    /// Sets a feature.
    /// </summary>
    /// <param name="bitPosition">The bit position of the feature to set.</param>
    /// <param name="isSet">true to set the feature, false to unset it</param>
    public void SetFeature(int bitPosition, bool isSet)
    {
        if (bitPosition >= FeatureFlags.Length)
            FeatureFlags.Length = bitPosition + 1;

        FeatureFlags.Set(bitPosition, isSet);

        OnChanged();
    }

    /// <summary>
    /// Checks if a feature is set either as compulsory or optional.
    /// </summary>
    /// <param name="feature">Feature to check.</param>
    /// <returns>true if the feature is set, false otherwise.</returns>
    public bool IsFeatureSet(Feature feature)
    {
        var bitPosition = (int)feature;

        return IsFeatureSet(bitPosition) || IsFeatureSet(bitPosition - 1);
    }

    /// <summary>
    /// Checks if a feature is set.
    /// </summary>
    /// <param name="feature">Feature to check.</param>
    /// <param name="isCompulsory">If the feature is compulsory.</param>
    /// <returns>true if the feature is set, false otherwise.</returns>
    public bool IsFeatureSet(Feature feature, bool isCompulsory)
    {
        var bitPosition = (int)feature;

        // If the feature is compulsory, adjust the bit position to be even
        if (isCompulsory)
            bitPosition--;

        return IsFeatureSet(bitPosition);
    }

    /// <summary>
    /// Checks if a feature is set.
    /// </summary>
    /// <param name="bitPosition">The bit position of the feature to check.</param>
    /// <param name="isCompulsory">If the feature is compulsory.</param>
    /// <returns>true if the feature is set, false otherwise.</returns>
    public bool IsFeatureSet(int bitPosition, bool isCompulsory)
    {
        // If the feature is compulsory, adjust the bit position to be even
        if (isCompulsory)
            bitPosition--;

        return IsFeatureSet(bitPosition);
    }

    /// <summary>
    /// Checks if a feature is set.
    /// </summary>
    /// <param name="bitPosition">The bit position of the feature to check.</param>
    /// <returns>true if the feature is set, false otherwise.</returns>
    private bool IsFeatureSet(int bitPosition)
    {
        return bitPosition < FeatureFlags.Length && FeatureFlags.Get(bitPosition);
    }

    /// <summary>
    /// Gets the positions of every set bit, lowest first.
    /// </summary>
    public IReadOnlyList<int> GetSetBits()
    {
        var bits = new List<int>();
        for (var i = 0; i < FeatureFlags.Length; i++)
            if (FeatureFlags.Get(i))
                bits.Add(i);

        return bits;
    }

    /// <summary>
    /// Checks if both sets have exactly the same bits set, whatever the length of their bitmaps.
    /// </summary>
    public bool HasSameBits(FeatureSet other) => GetSetBits().SequenceEqual(other.GetSetBits());

    /// <summary>
    /// Checks if the option_anchors feature is set.
    /// </summary>
    /// <returns>true if one of the features is set, false otherwise.</returns>
    public bool IsOptionAnchorsSet()
    {
        return IsFeatureSet(Feature.OptionAnchors, false) || IsFeatureSet(Feature.OptionAnchors, true);
    }

    /// <summary>
    /// Check if this feature set is compatible with the other provided feature set.
    /// </summary>
    /// <param name="other">The other feature set to check compatibility with.</param>
    /// <param name="negotiatedFeatureSet">The resulting negotiated feature set.</param>
    /// <returns>true if the feature sets are compatible, false otherwise.</returns>
    /// <remarks>
    /// Both this and the other feature set must have all the dependencies set.
    /// ASSUMED features (BOLT 9) that the other set omits are treated as set optional by it, so a peer that leaves
    /// them out is not rejected even when we set them as compulsory.
    /// </remarks>
    public bool IsCompatible(FeatureSet other, out FeatureSet? negotiatedFeatureSet)
    {
        // Our own feature set must be well-formed (BOLT 9: MUST set all transitive feature dependencies)
        if (!AreDependenciesSet())
        {
            negotiatedFeatureSet = null;
            return false;
        }

        // Check which one is bigger and iterate on it
        var maxLength = Math.Max(FeatureFlags.Length, other.FeatureFlags.Length);

        // Create an empty feature set to store the negotiated features
        negotiatedFeatureSet = CreateEmpty(maxLength);
        for (var i = 1; i < maxLength; i += 2)
        {
            var isLocalOptionalSet = IsFeatureSet(i, false);
            var isLocalCompulsorySet = IsFeatureSet(i, true);
            var isOtherOptionalSet = other.IsFeatureSet(i, false);
            var isOtherCompulsorySet = other.IsFeatureSet(i, true);

            // If the feature is unknown
            if (!Enum.IsDefined(typeof(Feature), i))
            {
                // If the feature is unknown and even, close the connection
                if (isOtherCompulsorySet)
                {
                    negotiatedFeatureSet = null;
                    return false;
                }

                if (isOtherOptionalSet)
                    negotiatedFeatureSet.SetFeature(i, false);
            }
            else
            {
                // ASSUMED features can be safely ignored by the peer: treat an omitted one as supported (optional)
                if (!isOtherOptionalSet && !isOtherCompulsorySet && s_assumedFeatures.Contains((Feature)i))
                    isOtherOptionalSet = true;

                // If the local feature is compulsory, the other feature should also be set (either optional or compulsory)
                if (isLocalCompulsorySet && !(isOtherOptionalSet || isOtherCompulsorySet))
                {
                    negotiatedFeatureSet = null;
                    return false;
                }

                // If the other feature is compulsory, the local feature should also be set (either optional or compulsory)
                if (isOtherCompulsorySet && !(isLocalOptionalSet || isLocalCompulsorySet))
                {
                    negotiatedFeatureSet = null;
                    return false;
                }

                // Record the negotiated feature: compulsory (even bit) if either side requires it, optional (odd bit)
                // if both sides support it
                if (isOtherCompulsorySet || isLocalCompulsorySet)
                {
                    negotiatedFeatureSet.SetFeature(i - 1, true);
                }
                else if (isLocalOptionalSet && isOtherOptionalSet)
                {
                    negotiatedFeatureSet.SetFeature(i, true);
                }
            }
        }

        // Check if all the other node's dependencies are set
        if (other.AreDependenciesSet())
            return true;

        negotiatedFeatureSet = null;
        return false;
    }

    /// <summary>
    /// Serializes the features to a byte array.
    /// </summary>
    public void WriteToBitWriter(IBitWriter bitWriter, int length, bool shouldPad)
    {
        // Check if _featureFlags is as long as the length
        var extraLength = length - FeatureFlags.Length;
        if (extraLength > 0)
            FeatureFlags.Length += extraLength;

        for (var i = 0; i < length && bitWriter.HasMoreBits(1); i++)
            bitWriter.WriteBit(FeatureFlags[length - i - (shouldPad ? 0 : 1)]);
    }

    /// <summary>
    /// Checks if a feature is set.
    /// </summary>
    /// <param name="feature">The feature to check.</param>
    /// <returns>true if the feature is set, false otherwise.</returns>
    /// <remarks>
    /// We don't care if the feature is compulsory or optional.
    /// </remarks>
    public bool HasFeature(Feature feature) => IsFeatureSet(feature, false) || IsFeatureSet(feature, true);

    public byte[]? GetBytes(bool asGlobal = false)
    {
        // Get the last valid bit
        var lastIndexOfOne = GetLastIndexOfOne(FeatureFlags, asGlobal);
        if (lastIndexOfOne == -1)
            return null;

        // Calculate total bytes needed
        var totalBytes = (FeatureFlags.Length + 7) / 8;
        var bytes = new byte[totalBytes];

        // Copy bits as bytes
        FeatureFlags.CopyTo(bytes, 0);

        // Number of bytes up to and including the one holding the last set bit ((i + 7) / 8 dropped a last bit at a
        // multiple of 8, e.g. zero_fee_commitments' bit 40)
        var lastValidByte = lastIndexOfOne / 8 + 1;

        return bytes[..lastValidByte];
    }

    /// <summary>
    /// Gets the feature bits as a big-endian byte array, the wire encoding of BOLT 9 feature fields such as
    /// <c>channel_type</c> (the last byte holds bits 0-7).
    /// </summary>
    /// <returns>The big-endian bytes, or null if no bit is set.</returns>
    /// <remarks>
    /// <see cref="GetBytes"/> is little-endian (the first byte holds bits 0-7); this is its reverse and the inverse of
    /// <see cref="DeserializeFromBytes"/>.
    /// </remarks>
    public byte[]? GetWireBytes()
    {
        var bytes = GetBytes();
        if (bytes is null)
            return null;

        Array.Reverse(bytes);
        return bytes;
    }

    /// <summary>
    /// Deserializes the features from a byte array.
    /// </summary>
    /// <param name="data">The byte array to deserialize from.</param>
    /// <remarks>
    /// The byte array can have a length less than or equal to 8 bytes.
    /// </remarks>
    /// <returns>The deserialized features.</returns>
    /// <exception cref="SerializationException">Error deserializing Features</exception>
    public static FeatureSet DeserializeFromBytes(byte[] data)
    {
        try
        {
            // Work on a copy so the caller's buffer is never mutated
            var bytes = (byte[])data.Clone();
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);

            var bitArray = new BitArray(bytes);
            return new FeatureSet { FeatureFlags = bitArray };
        }
        catch (Exception e)
        {
            throw new SerializationException("Error deserializing Features", e);
        }
    }

    /// <summary>
    /// Deserializes the features from a BitReader.
    /// </summary>
    /// <param name="bitReader">The bit reader to read from.</param>
    /// <param name="length">The number of bits to read.</param>
    /// <param name="shouldPad">If the bit array should be padded.</param>
    /// <returns>The deserialized features.</returns>
    /// <exception cref="SerializationException">Error deserializing Features</exception>
    public static FeatureSet DeserializeFromBitReader(IBitReader bitReader, int length, bool shouldPad)
    {
        try
        {
            // Create a new bit array
            var bitArray = new BitArray(length + (shouldPad ? 1 : 0));
            for (var i = 0; i < length; i++)
                bitArray.Set(length - i - (shouldPad ? 0 : 1), bitReader.ReadBit());

            return new FeatureSet { FeatureFlags = bitArray };
        }
        catch (Exception e)
        {
            throw new SerializationException("Error deserializing Features", e);
        }
    }

    /// <summary>
    /// Combines two feature sets.
    /// </summary>
    /// <param name="first">The first feature set.</param>
    /// <param name="second">The second feature set.</param>
    /// <returns>The combined feature set.</returns>
    /// <remarks>
    /// The combined feature set is the logical OR of the two feature sets.
    /// </remarks>
    public static FeatureSet Combine(FeatureSet first, FeatureSet second)
    {
        var combinedLength = Math.Max(first.FeatureFlags.Length, second.FeatureFlags.Length);
        var combinedFlags = new BitArray(combinedLength);

        for (var i = 0; i < combinedLength; i++)
            combinedFlags.Set(i, first.IsFeatureSet(i) || second.IsFeatureSet(i));

        return new FeatureSet { FeatureFlags = combinedFlags };
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        for (var i = 1; i < FeatureFlags.Length; i += 2)
        {
            if (IsFeatureSet(i))
                sb.Append($"{(Feature)i}, ");
            else if (IsFeatureSet(i - 1))
                sb.Append($"{(Feature)i}, ");
        }

        return sb.ToString().TrimEnd(' ', ',');
    }

    /// <summary>
    /// Checks if all dependencies are set.
    /// </summary>
    /// <returns>true if all dependencies are set, false otherwise.</returns>
    /// <remarks>
    /// Checking every set feature's direct dependencies also covers transitive dependencies.
    /// </remarks>
    public bool AreDependenciesSet() => GetMissingDependencies().Count == 0;

    /// <summary>
    /// Gets every (feature, dependency) pair where the feature is set but its dependency is not.
    /// </summary>
    public IReadOnlyList<(Feature Feature, Feature Dependency)> GetMissingDependencies()
    {
        var missing = new List<(Feature, Feature)>();
        foreach (var (feature, dependencies) in s_featureDependencies)
        {
            if (!HasFeature(feature))
                continue;

            missing.AddRange(dependencies.Where(dependency => !HasFeature(dependency))
                                         .Select(dependency => (feature, dependency)));
        }

        return missing;
    }

    /// <summary>
    /// Gets the contexts a known feature may be presented in.
    /// </summary>
    /// <returns>The contexts, or <see cref="FeatureContext.None"/> for unknown features.</returns>
    public static FeatureContext GetContexts(Feature feature) =>
        s_featureContexts.GetValueOrDefault(feature, FeatureContext.None);

    /// <summary>
    /// Gets the dependencies of a known feature.
    /// </summary>
    public static IReadOnlyList<Feature> GetDependencies(Feature feature) =>
        s_featureDependencies.TryGetValue(feature, out var dependencies) ? dependencies : [];

    /// <summary>
    /// Creates a copy of this feature set that only keeps the known features that may be presented in the given
    /// context.
    /// </summary>
    /// <param name="context">The context(s) the features will be presented in.</param>
    /// <returns>A new, filtered, feature set.</returns>
    /// <remarks>
    /// BOLT 9: the origin node MUST NOT set feature bits in fields not specified by the table, and MUST NOT set
    /// feature bits it does not support, so unknown bits are dropped too.
    /// </remarks>
    public FeatureSet FilterByContext(FeatureContext context)
    {
        var filtered = CreateEmpty(FeatureFlags.Length);
        foreach (var (feature, contexts) in s_featureContexts)
        {
            if ((contexts & context) == FeatureContext.None)
                continue;

            var optionalBit = (int)feature;
            if (IsFeatureSet(optionalBit))
                filtered.SetFeature(optionalBit, true);

            if (IsFeatureSet(optionalBit - 1))
                filtered.SetFeature(optionalBit - 1, true);
        }

        return filtered;
    }

    private void OnChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static FeatureSet CreateEmpty(int length) => new() { FeatureFlags = new BitArray(length) };

    private static int GetLastIndexOfOne(BitArray bitArray, bool asGlobal = false)
    {
        var maxLength = asGlobal ? 13 : bitArray.Length;
        for (var i = maxLength - 1; i >= 0; i--)
        {
            if (bitArray[i])
                return i;
        }

        return -1; // Return -1 if no number 1 is found
    }
}