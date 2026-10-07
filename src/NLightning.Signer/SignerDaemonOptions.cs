namespace NLightning.Signer;

using Domain.Protocol.ValueObjects;

internal sealed record SignerDaemonOptions(string SocketPath, string? KeyFilePath, string AuthTokenFilePath,
                                           string? PasswordFilePath,
                                           bool PasswordStdin, bool Create, BitcoinNetwork Network,
                                           bool SeedStdin, string StateFilePath)
{
    public const string Usage = "NLightning.Signer --socket <path> --key-file <path> "
                              + "--auth-token-file <path> (--password-file <path> | --password-stdin) "
                              + "[--create] [--network regtest]\n"
                              + "NLightning.Signer --socket <path> --seed-stdin --state-file <absolute-path> "
                              + "--auth-token-file <path> [--network regtest]";

    public static SignerDaemonOptions Parse(string[] args)
    {
        string? socket = null;
        string? key = null;
        string? passwordFile = null;
        string? authTokenFile = null;
        var stdin = false;
        var create = false;
        var seedStdin = false;
        string? stateFile = null;
        var network = BitcoinNetwork.Regtest;
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            switch (option)
            {
                case "--socket":
                    socket = Value(args, ref i);
                    break;
                case "--key-file":
                    key = Value(args, ref i);
                    break;
                case "--password-file":
                    passwordFile = Value(args, ref i);
                    break;
                case "--auth-token-file":
                    authTokenFile = Value(args, ref i);
                    break;
                case "--password-stdin":
                    stdin = true;
                    break;
                case "--create":
                    create = true;
                    break;
                case "--seed-stdin":
                    seedStdin = true;
                    break;
                case "--state-file":
                    stateFile = Value(args, ref i);
                    break;
                case "--network":
                    network = BitcoinNetwork.Resolve(Value(args, ref i));
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {option}");
            }
        }

        if (string.IsNullOrWhiteSpace(socket))
            throw new ArgumentException("--socket is required.");
        if (seedStdin)
        {
            if (key is not null || stdin || passwordFile is not null || create)
                throw new ArgumentException("--seed-stdin cannot be combined with key-file/password/create options.");
            if (stateFile is null || !Path.IsPathFullyQualified(stateFile))
                throw new ArgumentException("--seed-stdin requires an absolute --state-file path.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("--key-file is required unless --seed-stdin is specified.");
            if (stdin == (passwordFile is not null))
                throw new ArgumentException("Choose exactly one of --password-file and --password-stdin.");
        }
        if (string.IsNullOrWhiteSpace(authTokenFile))
            throw new ArgumentException("--auth-token-file is required.");

        var socketPath = Path.GetFullPath(socket);
        var keyPath = key is null ? null : Path.GetFullPath(key);
        var statePath = Path.GetFullPath(stateFile ?? key + ".signer-state");
        var paths = new[] { keyPath, statePath, statePath + ".key-index", (keyPath ?? statePath) + ".lock",
                            Path.GetFullPath(authTokenFile), passwordFile is null ? null : Path.GetFullPath(passwordFile) };
        if (paths.Any(path => string.Equals(path, socketPath, StringComparison.Ordinal)))
            throw new ArgumentException("The socket path must be separate from every signer storage/secret path.");
        var storagePaths = paths.Where(path => path is not null).ToArray();
        if (storagePaths.Distinct(StringComparer.Ordinal).Count() != storagePaths.Length)
            throw new ArgumentException("Signer storage, token and password files must use separate paths.");

        return new SignerDaemonOptions(socketPath, keyPath,
                                       Path.GetFullPath(authTokenFile),
                                       passwordFile is null ? null : Path.GetFullPath(passwordFile), stdin, create,
                                       network, seedStdin, statePath);
    }

    private static string Value(string[] args, ref int index)
    {
        if (++index == args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("An option is missing its value.");
        return args[index];
    }
}