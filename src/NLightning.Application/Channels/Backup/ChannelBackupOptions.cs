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

    /// <summary>
    /// How many blocks below the tip <c>restorechanbackup</c> searches for the spend of a funding output that is
    /// already spent (default 4032, about four weeks; never below the funding block). A spend older than that is
    /// searched in the
    /// background down to the funding block, and handed to the on-chain resolution once found (NL-430).
    /// </summary>
    public uint RestoreSpendSearchDepth { get; set; } = 4032;

    /// <summary>
    /// How many blocks the funding spend search reads at once (default 8, at least 1). A spend older than
    /// <see cref="RestoreSpendSearchDepth"/> is searched in the background, batch by batch, down to the funding block
    /// (NL-430).
    /// </summary>
    public uint RestoreSpendSearchBatchSize { get; set; } = 8;
}