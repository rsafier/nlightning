namespace NLightning.Infrastructure.Bitcoin.KeyRing;

/// <summary>Swap signing policy, bound from LndGrpc:Signer. No reserved Lightning key family is ever permitted.</summary>
public sealed class KeyRingOptions
{
    public List<int> AllowedKeyFamilies { get; set; } = [21, 99, 42060, 42068, 42069];
    public int MaxSessions { get; set; } = 1000;
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(1);
}