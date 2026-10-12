namespace NLightning.Client.Handlers;

using Domain.Crypto.ValueObjects;
using Ipc;
using Printers;

/// <summary>
/// The peer storage command of the CLI: <c>listpeerstorage|list-peer-storage [node_id] [--blob]</c> (ClientCommand 32,
/// NL-432).
/// </summary>
internal static class PeerStorageCommands
{
    /// <summary>The usage of listpeerstorage.</summary>
    internal const string ListPeerStorageUsage = "[node_id] [--blob]";

    /// <summary>
    /// Whether <paramref name="cmd"/> is the peer storage command.
    /// </summary>
    internal static bool IsPeerStorageCommand(string cmd) => cmd is "listpeerstorage" or "list-peer-storage";

    /// <summary>
    /// Checks the arguments of listpeerstorage.
    /// </summary>
    /// <returns>An error message with the usage, or null when they are valid.</returns>
    internal static string? Validate(string cmd, string[] commandArgs) =>
        ParseListPeerStorageOptions(commandArgs, out var error) is null
            ? $"{error} Usage: {cmd} {ListPeerStorageUsage}"
            : null;

    /// <summary>
    /// Runs a validated listpeerstorage and prints its result.
    /// </summary>
    internal static async Task RunAsync(string[] commandArgs, NamedPipeIpcClient client,
                                        CancellationToken cancellationToken)
    {
        var (nodeId, includeBlob) = ParseListPeerStorageOptions(commandArgs, out _)!.Value;
        new ListPeerStoragePrinter().Print(await client.ListPeerStorageAsync(nodeId, includeBlob, cancellationToken));
    }

    /// <summary>
    /// <c>[node_id] [--blob]</c>, in any order.
    /// </summary>
    /// <returns>The arguments, or null with <paramref name="error"/> set.</returns>
    internal static (CompactPubKey? NodeId, bool IncludeBlob)? ParseListPeerStorageOptions(
        string[] commandArgs, out string? error)
    {
        error = null;
        CompactPubKey? nodeId = null;
        var includeBlob = false;
        foreach (var argument in commandArgs)
        {
            if (argument == "--blob")
            {
                includeBlob = true;
                continue;
            }

            if (nodeId is not null)
            {
                error = $"Unexpected argument '{argument}'.";
                return null;
            }

            if (!ClientApp.TryParseNodeId(argument, out var parsed))
            {
                error = $"Invalid node id '{argument}': expected 66 hex characters.";
                return null;
            }

            nodeId = parsed;
        }

        return (nodeId, includeBlob);
    }
}