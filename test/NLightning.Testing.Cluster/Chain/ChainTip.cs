namespace NLightning.Testing.Cluster.Chain;

/// <summary>A chain tip: height and block hash (display hex).</summary>
public sealed record ChainTip(long Height, string Hash)
{
    public override string ToString() => $"{Height} ({Hash})";
}