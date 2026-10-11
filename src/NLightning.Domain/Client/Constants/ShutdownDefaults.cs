namespace NLightning.Domain.Client.Constants;

/// <summary>
/// The bounds of <c>shutdown --wait</c> (NL-592), shared by the client's argument checks and the daemon's.
/// </summary>
public static class ShutdownDefaults
{
    /// <summary>The drain wait when <c>--wait</c> is given without <c>--timeout</c>.</summary>
    public const int DefaultWaitTimeoutSeconds = 300;

    /// <summary>The largest <c>--timeout</c>: a wait is always bounded (a day).</summary>
    public const int MaxWaitTimeoutSeconds = 86_400;
}