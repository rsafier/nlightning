namespace NLightning.Daemon.Contracts.Constants;

public static class NodeConstants
{
    public const string DaemonFolder = "nltg";
    public const string KeyFile = "nltg.key.json";
    public const string PidFile = "nltg.pid";
    public const string NamedPipeFile = "nltg.ipc";
    public const string CookieFile = "nltg.cookie";

    /// <summary>The locked start's provisioning socket, <c>&lt;configPath&gt;/provisioning/key.sock</c> (NL-1349).</summary>
    public const string ProvisioningSocketDirectory = "provisioning";

    public const string ProvisioningSocketFile = "key.sock";
}