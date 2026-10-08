using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Signer;

using Domain.Bitcoin.Interfaces;
using Domain.Crypto.Interfaces;
using Domain.Crypto.KeyRing;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Signing;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.KeyRing;
using Infrastructure.Bitcoin.Managers;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Bitcoin.Signers;
using Infrastructure.RemoteSigning;
using Infrastructure.Repositories.Memory;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine(SignerDaemonOptions.Usage);
            return 0;
        }

        try
        {
            if (OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("The prototype signer daemon requires Unix domain sockets.");

            var options = SignerDaemonOptions.Parse(args);
            SignerFiles.PrepareDirectoryFor(options.KeyFilePath ?? options.StateFilePath);
            SignerFiles.PrepareDirectoryFor(options.StateFilePath);
            SignerFiles.PrepareDirectoryFor(options.SocketPath);
            if (Path.Exists(options.StateFilePath) || new FileInfo(options.StateFilePath).LinkTarget is not null)
                SignerFiles.RequirePrivate(options.StateFilePath);
            // Never unlink an arbitrary existing path or steal an active listener. After a crash the operator
            // explicitly removes the stale socket; normal shutdown removes the socket we created.
            if (Path.Exists(options.SocketPath) || new FileInfo(options.SocketPath).LinkTarget is not null)
                throw new IOException("Socket path already exists; verify that its signer is stopped before removing it.");

            using var keyLock = SignerFiles.LockKeyFile(options.KeyFilePath ?? options.StateFilePath);
            SignerFiles.RequirePrivate(options.AuthTokenFilePath);
            var token = (await File.ReadAllTextAsync(options.AuthTokenFilePath)).TrimEnd('\r', '\n');
            if (token.Length < 32 || token.Any(character => character is < '!' or > '~'))
                throw new ArgumentException("The authentication token must contain at least 32 printable ASCII characters.");

            using var provisioned = await ProvisionKeysAsync(options);
            var keys = provisioned.Keys;
            var enrolledContext = new NodeSigningContext(options.NodeId, options.OwnerId, options.SignerId,
                                                        options.Network.Name, keys.GetNodePubKey());
            SignerEnrollment.Bind(options.StateFilePath, enrolledContext);

            var builder = WebApplication.CreateSlimBuilder();
            // This executable has one explicitly configured IPC endpoint. Ambient appsettings/environment
            // configuration must never add a TCP listener beside it.
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.ConfigureKestrel(server =>
                server.ListenUnixSocket(options.SocketPath, endpoint => endpoint.Protocols = HttpProtocols.Http2));
            builder.Services.AddBitcoinInfrastructure();
            var wallet = new UtxoMemoryRepository();
            builder.Services.AddSingleton<IUtxoMemoryRepository>(wallet);
            builder.Services.AddSingleton<ISecureKeyManager>(keys);
            builder.Services.AddSingleton(enrolledContext);
            var swapStatePath = options.StateFilePath + ".swap-sessions";
            if (File.Exists(options.StateFilePath) && !File.Exists(swapStatePath))
                throw new IOException("Signer swap history is missing beside existing safety state.");
            builder.Services.AddSingleton<ISwapSigner>(services =>
                new SwapSigner(keys, services.GetRequiredService<IMusig2Service>(),
                    services.GetRequiredService<ISecp256K1Math>(), services.GetRequiredService<IOptions<KeyRingOptions>>(),
                    sessionStatePath: swapStatePath));
            var nonceStatePath = options.StateFilePath + ".nonces";
            if (File.Exists(options.StateFilePath) && !File.Exists(nonceStatePath))
                throw new IOException("Signer nonce history is missing beside existing safety state.");
            builder.Services.AddSingleton(_ => NativeNonceStateStore.ForSigner(nonceStatePath, keys));
            builder.Services.AddSingleton<ILightningSigner>(services =>
            {
                var signer = new LocalLightningSigner(new FundingOutputBuilder(), new KeyDerivationService(),
                    services.GetRequiredService<ILogger<LocalLightningSigner>>(),
                    new NodeOptions { BitcoinNetwork = options.Network }, keys, wallet);
                signer.AttachNonceStateStore(services.GetRequiredService<NativeNonceStateStore>());
                return signer;
            });
            builder.Services.AddSingleton(services =>
                new DurableSignerState(services.GetRequiredService<ILightningSigner>(),
                                       options.StateFilePath, options.Network.ToString()));
            builder.Services.AddSingleton(new RemoteSignerOptions
            {
                NodeId = options.NodeId,
                OwnerId = options.OwnerId,
                SignerId = options.SignerId,
                SocketPath = options.SocketPath,
                AuthToken = token,
                Network = options.Network.ToString()
            });
            builder.Services.AddGrpc(grpc =>
            {
                grpc.MaxReceiveMessageSize = RemoteSignerOptions.MaxMessageBytes;
                grpc.MaxSendMessageSize = RemoteSignerOptions.MaxMessageBytes;
            });
            // One service instance owns serialization and the bounded retry cache for the shared signer.
            builder.Services.AddSingleton<SignerRpcService>();

            await using var app = builder.Build();
            // Replay and validate the durable signer journal before accepting requests or reporting readiness.
            _ = app.Services.GetRequiredService<ISwapSigner>();
            _ = app.Services.GetRequiredService<DurableSignerState>();
            SignerFiles.SyncParentDirectory(options.StateFilePath);
            provisioned.MarkStateInitialized();
            app.MapGrpcService<SignerRpcService>();
            var started = false;
            try
            {
                await app.StartAsync();
                started = true;
                File.SetUnixFileMode(options.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Console.WriteLine($"SIGNER_READY network={options.Network} socket={options.SocketPath} "
                                + $"node={Convert.ToHexString((byte[])keys.GetNodePubKey()).ToLowerInvariant()}");
                await app.WaitForShutdownAsync();
            }
            finally
            {
                if (started)
                {
                    await app.StopAsync();
                    File.Delete(options.SocketPath);
                }
            }

            return 0;
        }
        catch (Exception exception)
        {
            // Never log request bodies, decrypted keys, password/token values or a stack containing them.
            Console.Error.WriteLine($"Signer startup failed ({exception.GetType().Name}). "
                                  + "Check paths, owner-only permissions, password, network and exclusive key ownership.");
            return 1;
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task<ProvisionedKeys> ProvisionKeysAsync(SignerDaemonOptions options)
    {
        if (!options.SeedStdin)
        {
            var password = await SignerFiles.ReadPasswordAsync(options);
            return new ProvisionedKeys(LoadKeys(options, password), null);
        }

        var seed = await SignerFiles.ReadSeedAsync();
        InjectedKeyIndexJournal? journal = null;
        SecureKeyManager? keys = null;
        try
        {
            var indexPath = options.StateFilePath + ".key-index";
            if (File.Exists(options.StateFilePath) && !File.Exists(indexPath))
                throw new IOException("Injected allocation journal is missing beside existing signer state.");
            keys = SecureKeyManager.FromSeed(seed, options.Network, index => journal!.Persist(index));
            journal = new InjectedKeyIndexJournal(indexPath, options.Network.ToString(),
                                                  (byte[])keys.GetNodePubKey());
            if (journal.StateInitialized && !File.Exists(options.StateFilePath))
                throw new IOException("Signer state is missing beside an allocated injected seed.");
            keys.EnsureLastUsedChannelIndexAtLeast(journal.LastIndex);
            return new ProvisionedKeys(keys, journal);
        }
        catch
        {
            keys?.Dispose();
            journal?.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static SecureKeyManager LoadKeys(SignerDaemonOptions options, string password)
    {
        var keyFilePath = options.KeyFilePath!;
        if (new FileInfo(keyFilePath).LinkTarget is not null)
            throw new IOException("Key file cannot be a symbolic link.");
        if (File.Exists(keyFilePath))
        {
            if (options.Create)
                throw new IOException("--create refuses to replace an existing key file.");
            SignerFiles.RequirePrivate(keyFilePath);
            return SecureKeyManager.FromFilePath(keyFilePath, options.Network, password);
        }

        if (!options.Create)
            throw new FileNotFoundException("Key file does not exist; use --create for a new identity.");
        var keys = SecureKeyManager.CreateNew(options.Network, keyFilePath, 0);
        try
        {
            keys.SaveToFile(password);
            return keys;
        }
        catch
        {
            keys.Dispose();
            throw;
        }
    }

    [UnsupportedOSPlatform("windows")]
    private sealed class ProvisionedKeys(SecureKeyManager keys, InjectedKeyIndexJournal? journal) : IDisposable
    {
        public SecureKeyManager Keys { get; } = keys;

        public void MarkStateInitialized() => journal?.MarkStateInitialized();

        public void Dispose()
        {
            Keys.Dispose();
            journal?.Dispose();
        }
    }
}