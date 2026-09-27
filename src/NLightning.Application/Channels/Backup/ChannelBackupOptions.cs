namespace NLightning.Application.Channels.Backup;

/// <summary>
/// The static channel backup file (section <c>Node:Backup</c>).
/// </summary>
public sealed class ChannelBackupOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Node:Backup";

    /// <summary>The default file name, next to the key file (<c>~/.nltg/&lt;network&gt;/channel.backup</c>).</summary>
    public const string DefaultFileName = "channel.backup";

    /// <summary>
    /// The backup file. Null or empty: the node writes no file (the IPC export still works); the daemon sets it to
    /// <see cref="DefaultFileName"/> in its configuration directory unless configured.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>Whether the node keeps the file up to date (default true).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long a change waits for more before the file is written (default 1 s).</summary>
    public TimeSpan WriteDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How often the file is compared with the database even without a channel event (default 10 min; zero turns
    /// it off). Catches a channel that left memory without an event (closed on chain).
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(10);
}