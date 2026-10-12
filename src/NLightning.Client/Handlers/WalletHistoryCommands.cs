using System.Globalization;

namespace NLightning.Client.Handlers;

using Ipc;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal static class WalletHistoryCommands
{
    internal const string Usage = "wallethistory [--from-height <height> [--to-height <height>] [--allow-partial] [--address-count <count>] | --cancel]";
    internal static string? Validate(string[] args) => Parse(args, out var error) is null ? error : null;
    internal static WalletHistoryIpcRequest? Parse(string[] args, out string? error)
    {
        uint? from = null, to = null;
        uint count = 30;
        bool partial = false, cancel = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (!seen.Add(option)) return Fail(out error);
            if (option == "--allow-partial") partial = true;
            else if (option == "--cancel") cancel = true;
            else if (option is "--from-height" or "--to-height" or "--address-count" && ++i < args.Length &&
                uint.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                if (option == "--from-height") from = value;
                else if (option == "--to-height") to = value;
                else count = value;
            }
            else return Fail(out error);
        }
        if (count is 0 or > 100_000 || (cancel && args.Length != 1) ||
            (from is null && (to is not null || partial || seen.Contains("--address-count"))) ||
            (from is { } lower && to is { } upper && lower > upper)) return Fail(out error);
        error = null;
        return new() { FromHeight = from, ToHeight = to, AddressCount = count, AllowPartial = partial, Cancel = cancel };
    }
    private static WalletHistoryIpcRequest? Fail(out string? error) { error = $"Invalid arguments. Usage: {Usage}"; return null; }
    internal static async Task RunAsync(string[] args, NamedPipeIpcClient client, CancellationToken ct)
    {
        var result = await client.WalletHistoryAsync(Parse(args, out _)!, ct);
        Print(result);
    }
    private static void Print(WalletHistoryIpcResponse result)
    {
        if (!result.HasJob) { Console.WriteLine("No wallet history rescan requested."); return; }
        Console.WriteLine($"History rescan {(result.IsActive ? "active" : "stopped")}: requested {result.RequestedFromHeight}, available {result.AvailableFromHeight}..{result.TargetHeight}, cursor {result.CursorHeight?.ToString(CultureInfo.InvariantCulture) ?? "none"}.");
        if (result.IsPartial) Console.WriteLine("Partial history: earlier requested blocks are pruned.");
        if (result.Error is { } error) Console.WriteLine($"Paused: {error}");
    }
}