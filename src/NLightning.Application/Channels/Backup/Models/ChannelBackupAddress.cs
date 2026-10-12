namespace NLightning.Application.Channels.Backup.Models;

/// <summary>
/// A last known address of a channel peer, as the peer table stores it.
/// </summary>
/// <param name="Type">The address type as the peer table names it (for example <c>IPv4</c>).</param>
/// <param name="Host">The host (an IP address, a DNS name or an onion address).</param>
/// <param name="Port">The TCP port.</param>
public sealed record ChannelBackupAddress(string Type, string Host, ushort Port);