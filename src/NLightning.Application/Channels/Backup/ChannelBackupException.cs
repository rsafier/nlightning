namespace NLightning.Application.Channels.Backup;

/// <summary>
/// A static channel backup that cannot be used: not a backup, a version we do not know, truncated or malformed
/// (<see cref="ChannelBackupFormatException"/>), or not encrypted to this node's key or tampered with
/// (<see cref="ChannelBackupAuthenticationException"/>).
/// </summary>
public class ChannelBackupException : Exception
{
    public ChannelBackupException(string message) : base(message)
    {
    }

    public ChannelBackupException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// The backup is not a static channel backup, has a version we do not know, or is truncated or malformed.
/// </summary>
public sealed class ChannelBackupFormatException : ChannelBackupException
{
    public ChannelBackupFormatException(string message) : base(message)
    {
    }
}

/// <summary>
/// The backup does not decrypt with this node's key: another node's backup, or tampered with.
/// </summary>
public sealed class ChannelBackupAuthenticationException : ChannelBackupException
{
    public ChannelBackupAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}