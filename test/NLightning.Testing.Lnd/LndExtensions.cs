// Ported from LNUnit.LND (https://github.com/nbd-wtf/LNUnit, LNDExtentionMethods.cs), Copyright (c) 2024-2025 nbd,
// MIT License; the full text is in LICENSE-LNUnit.txt next to this file. Not ported: the AES helpers (unrelated to
// LND and unused here).

namespace NLightning.Testing.Lnd;

using Lnrpc;

/// <summary>LNUnit.LND's small helpers around <see cref="LndNodeConnection"/>.</summary>
public static class LndExtensions
{
    /// <summary>Opens a connection with these settings (blocking <c>GetInfo</c>).</summary>
    public static LndNodeConnection GetClient(this LndSettings settings) => new(settings);

    /// <summary>The bits of an unsigned 64-bit value as a signed one (e.g. a short channel id in a signed field).</summary>
    public static long PackUnsignedToInt64(this ulong i) => unchecked((long)i);

    /// <summary>The inverse of <see cref="PackUnsignedToInt64"/>.</summary>
    public static ulong UnpackSignedToUInt64(this long i) => unchecked((ulong)i);

    /// <summary>The node as an <c>lnrpc.LightningNode</c> (alias and identity key only).</summary>
    public static LightningNode ToLightningNode(this LndNodeConnection node) => new()
    {
        Alias = node.LocalAlias,
        PubKey = node.LocalNodePubKey
    };

    /// <summary><see cref="ToLightningNode"/> for each node.</summary>
    public static List<LightningNode> ToLightningNodes(this IEnumerable<LndNodeConnection> nodes) =>
        nodes.Select(x => x.ToLightningNode()).ToList();
}