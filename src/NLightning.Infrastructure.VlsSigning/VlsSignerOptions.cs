namespace NLightning.Infrastructure.VlsSigning;

public sealed class VlsSignerOptions
{
    public string SocketPath { get; set; } = "";
    public string? TokenFile { get; set; }
    public string? AuthToken { get; set; }
    public string Network { get; set; } = "regtest";
    public string? ExpectedNodePublicKey { get; set; }
    public int TimeoutSeconds { get; set; } = 15;
}