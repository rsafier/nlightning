using System.Net.Sockets;
using System.Text;

namespace NLightning.Client.Handlers;

using Daemon.Contracts.Constants;
using Daemon.Contracts.Provisioning;
using Daemon.Contracts.Utilities;

/// <summary>
/// The <c>unlock</c> command (NL-1349): delivers an encrypted key file and its password to a node started locked,
/// over its provisioning socket (<c>&lt;configPath&gt;/provisioning/key.sock</c>), or asks for its state. It does not
/// go through the IPC pipe, which a locked node does not open. <c>--frame</c> writes the request frame to stdout
/// instead, for a node whose provisioner is its stdin. The password is never printed.
/// </summary>
internal static class UnlockCommands
{
    internal const string Usage = "--key-file <path> [--password-file <path> | --password-stdin] "
                                + "[--secrets-file <path>] [--macaroon-dir <path>] [--socket <path>] [--frame] "
                                + "| unlock --status [--socket <path>]";

    /// <summary>The longest wait for the node's answer (the key file's Argon2id and the database checks).</summary>
    internal static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(5);

    internal sealed record Arguments(bool Status, string? KeyFile, string? PasswordFile, bool PasswordStdin,
                                     string? SecretsFile, string? Socket, bool Frame, string? MacaroonDirectory = null);

    internal static string? Validate(string cmd, string[] commandArgs) =>
        Parse(commandArgs, out var error) is null ? $"{error} Usage: {cmd} {Usage}" : null;

    internal static Arguments? Parse(string[] commandArgs, out string? error)
    {
        error = null;
        bool status = false, passwordStdin = false, frame = false;
        string? keyFile = null, passwordFile = null, secretsFile = null, socket = null, macaroonDirectory = null;
        for (var i = 0; i < commandArgs.Length; i++)
        {
            var name = commandArgs[i];
            switch (name)
            {
                case "--status":
                    status = true;
                    continue;
                case "--password-stdin":
                    passwordStdin = true;
                    continue;
                case "--frame":
                    frame = true;
                    continue;
                case "--key-file" or "--password-file" or "--secrets-file" or "--socket" or "--macaroon-dir":
                    if (i + 1 >= commandArgs.Length)
                    {
                        error = $"{name} needs a value.";
                        return null;
                    }

                    var value = commandArgs[++i];
                    if (name == "--key-file") keyFile = value;
                    else if (name == "--password-file") passwordFile = value;
                    else if (name == "--secrets-file") secretsFile = value;
                    else if (name == "--macaroon-dir") macaroonDirectory = value;
                    else socket = value;
                    continue;
                default:
                    error = $"Unknown argument '{name}'.";
                    return null;
            }
        }

        if (status)
        {
            if (keyFile is not null || passwordFile is not null || passwordStdin || secretsFile is not null || frame
             || macaroonDirectory is not null)
            {
                error = "--status takes only --socket.";
                return null;
            }
        }
        else if (keyFile is null)
        {
            error = "--key-file is required.";
            return null;
        }
        else if (passwordFile is not null && passwordStdin)
        {
            error = "Give either --password-file or --password-stdin.";
            return null;
        }
        else if (frame && socket is not null)
        {
            error = "--frame writes to stdout; it takes no --socket.";
            return null;
        }
        else if (frame && passwordFile is null)
        {
            error = "--frame needs --password-file (stdout carries the frame).";
            return null;
        }
        else if (frame && macaroonDirectory is not null)
        {
            error = "--macaroon-dir needs the node's answer, which --frame does not read.";
            return null;
        }
        else if (macaroonDirectory is not null && secretsFile is null)
        {
            error = "--macaroon-dir needs --secrets-file with lndGrpcMacaroonRootKey.";
            return null;
        }

        return new Arguments(status, keyFile, passwordFile, passwordStdin, secretsFile, socket, frame,
                             macaroonDirectory);
    }

    /// <returns>The process exit code: 0 when the node is (now) unlocked or answered a status, 1 otherwise.</returns>
    internal static async Task<int> RunAsync(string[] commandArgs, string configPath, TextReader stdin,
                                             TextWriter output, TextWriter errorOutput, CancellationToken ct)
    {
        var arguments = Parse(commandArgs, out _)!;
        var request = new KeyProvisioningRequest { Kind = KeyProvisioningProtocol.StatusKind };
        if (!arguments.Status)
        {
            try
            {
                request = new KeyProvisioningRequest
                {
                    Kind = KeyProvisioningProtocol.UnlockKind,
                    Material = KeyProvisioningProtocol.EncryptedKeyFileMaterial,
                    KeyFile = Convert.ToBase64String(await File.ReadAllBytesAsync(arguments.KeyFile!, ct)),
                    Password = await ReadPasswordAsync(arguments, stdin, errorOutput, ct),
                    Secrets = arguments.SecretsFile is null
                                  ? null
                                  : KeyProvisioningProtocol.ParseSecrets(
                                      await File.ReadAllTextAsync(arguments.SecretsFile, ct)),
                    ReturnLndGrpcMacaroons = arguments.MacaroonDirectory is null ? null : true
                };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                await errorOutput.WriteLineAsync(e.Message);
                return 1;
            }

            if (string.IsNullOrEmpty(request.Password))
            {
                await errorOutput.WriteLineAsync("The password is empty.");
                return 1;
            }
        }

        if (arguments.Frame)
        {
            await using var stdout = Console.OpenStandardOutput();
            await KeyProvisioningProtocol.WriteRequestAsync(stdout, request, ct);
            return 0;
        }

        var socketPath = arguments.Socket
                      ?? Path.Combine(configPath, NodeConstants.ProvisioningSocketDirectory,
                                      NodeConstants.ProvisioningSocketFile);
        KeyProvisioningResponse response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AnswerTimeout);
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            await KeyProvisioningProtocol.WriteRequestAsync(stream, request, timeout.Token);
            response = await KeyProvisioningProtocol.ReadResponseAsync(stream, timeout.Token);
        }
        catch (SocketException)
        {
            await errorOutput.WriteLineAsync($"No locked node answers on {socketPath} (is it started with --locked, "
                                           + "or already unlocked?).");
            return 1;
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            await errorOutput.WriteLineAsync($"The provisioning exchange failed: {e.Message}");
            return 1;
        }

        await output.WriteLineAsync($"State: {response.State}");
        if (response.Network is not null)
            await output.WriteLineAsync($"Network: {response.Network}");
        if (response.NodeId is not null)
            await output.WriteLineAsync($"Node id: {response.NodeId}");
        if (!response.Ok)
        {
            await errorOutput.WriteLineAsync($"Refused: {response.Error}");
            return 1;
        }

        if (arguments.MacaroonDirectory is not null)
            return await WriteMacaroonsAsync(arguments.MacaroonDirectory, response, output, errorOutput, ct);

        return 0;
    }

    /// <summary>
    /// Writes the macaroons a node baked under the delivered root key (NL-1352) on the operator's side: the node keeps
    /// none on disk. Each file is new and owner-only; an existing one is never replaced.
    /// </summary>
    private static async Task<int> WriteMacaroonsAsync(string directory, KeyProvisioningResponse response,
                                                       TextWriter output, TextWriter errorOutput, CancellationToken ct)
    {
        if (response.LndGrpcMacaroons is not { Count: > 0 } macaroons)
        {
            await errorOutput.WriteLineAsync("The node unlocked but returned no macaroons.");
            return 1;
        }

        try
        {
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(directory);
            else
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite
                                                                           | UnixFileMode.UserExecute);
            foreach (var (name, value) in macaroons.OrderBy(m => m.Key, StringComparer.Ordinal))
            {
                if (name is not ("admin" or "readonly" or "invoice"))
                    continue;

                var path = Path.Combine(directory, $"{name}.macaroon");
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var stream = new FileStream(path, options))
                    await stream.WriteAsync(Convert.FromBase64String(value), ct);
                await output.WriteLineAsync($"Wrote {path}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            await errorOutput.WriteLineAsync($"The macaroons could not be written: {e.Message}");
            return 1;
        }

        return 0;
    }

    private static async Task<string> ReadPasswordAsync(Arguments arguments, TextReader stdin, TextWriter errorOutput,
                                                        CancellationToken ct)
    {
        if (arguments.PasswordFile is not null)
        {
            // One line, like the daemon's --password-file: a trailing line break is not part of the password
            var text = await File.ReadAllTextAsync(arguments.PasswordFile, Encoding.UTF8, ct);
            return text.TrimEnd('\r', '\n');
        }

        if (arguments.PasswordStdin)
            return (await stdin.ReadLineAsync(ct))?.TrimEnd('\r') ?? string.Empty;

        await errorOutput.FlushAsync(ct);
        return ConsoleUtils.ReadPassword("Key file password: ");
    }
}