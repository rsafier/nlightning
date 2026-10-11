using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NLightning.Domain.Signing;

namespace NLightning.LndGrpc.Macaroons;

/// <summary>
/// The root key and the default macaroons in the data directory: <c>macaroons.key</c> (32 random bytes, LND's root key
/// for storage id <c>0</c>) and <c>admin.macaroon</c>, <c>readonly.macaroon</c>, <c>invoice.macaroon</c> baked from it
/// with LND's operation sets. Every file is created 0600 and exclusively; an existing file is never overwritten except
/// the macaroons after the root key was replaced (deleting <c>macaroons.key</c> rotates every macaroon).
/// </summary>
public static class LndMacaroonFiles
{
    public const string RootKeyFileName = "macaroons.key";
    public const string AdminFileName = "admin.macaroon";
    public const string ReadOnlyFileName = "readonly.macaroon";
    public const string InvoiceFileName = "invoice.macaroon";

    /// <summary>
    /// Loads the root key, creating it (and re-baking the three macaroons) when missing; bakes any missing macaroon.
    /// Returns the root key.
    /// </summary>
    /// <param name="allowGenerate">False (<c>Node:Startup:RequireProvisionedSecrets</c>, NL-1352): a missing root key
    /// is refused and missing macaroons are not baked, so no credential file is written.</param>
    /// <param name="allowGenerateRootKey">False (<c>LndGrpc:RequireExistingRootKey</c>, NL-1389): a missing root key is
    /// refused; missing macaroons are still baked from an existing one.</param>
    public static byte[] EnsureCreated(string directory, ILogger? logger = null, NodeSigningContext? context = null,
                                       bool allowGenerate = true, bool allowGenerateRootKey = true)
    {
        if (context is not null) LndCredentialEnrollment.Bind(directory, context);
        var rootKeyPath = Path.Combine(directory, RootKeyFileName);
        var rotated = false;
        byte[] rootKey;
        if (File.Exists(rootKeyPath))
        {
            rootKey = File.ReadAllBytes(rootKeyPath);
            if (rootKey.Length != 32)
                throw new InvalidOperationException($"{rootKeyPath} is not a 32-byte macaroon root key; delete it to "
                                                  + "make a new one (every macaroon baked before stops working).");
        }
        else if (!allowGenerate)
        {
            throw new InvalidOperationException(
                $"The LND gRPC macaroon root key {rootKeyPath} is missing and Node:Startup:RequireProvisionedSecrets "
              + "forbids generating it: deliver lndGrpcMacaroonRootKey with the key.");
        }
        else if (!allowGenerateRootKey)
        {
            throw new InvalidOperationException(
                $"The LND gRPC macaroon root key {rootKeyPath} is missing and LndGrpc:RequireExistingRootKey forbids "
              + "generating it: restore it, or deliver lndGrpcMacaroonRootKey with the key.");
        }
        else
        {
            Directory.CreateDirectory(directory);
            rootKey = MacaroonVerifier.NewRootKey();
            WriteExclusive(rootKeyPath, rootKey);
            rotated = true;
            logger?.LogInformation("Created a new LND gRPC macaroon root key in {Path}", rootKeyPath);
        }

        rootKey = LndCredentialEnrollment.EffectiveRootKey(rootKey, context);
        if (!allowGenerate)
            return rootKey;

        BakeDefaultFiles(directory, rootKey, rotated, logger);
        return rootKey;
    }

    /// <summary>
    /// Bakes the three default macaroons that are missing in <paramref name="directory"/> under
    /// <paramref name="rootKey"/> (the effective root key 0, e.g. one kept in the database, NL-1389); with
    /// <paramref name="replace"/> (a new root key) every one is baked again.
    /// </summary>
    public static void BakeDefaultFiles(string directory, byte[] rootKey, bool replace, ILogger? logger = null)
    {
        Directory.CreateDirectory(directory);
        Bake(directory, AdminFileName, rootKey, LndPermissions.Admin, replace, logger);
        Bake(directory, ReadOnlyFileName, rootKey, LndPermissions.Read, replace, logger);
        Bake(directory, InvoiceFileName, rootKey, LndPermissions.Invoice, replace, logger);
    }

    /// <summary>
    /// The three default macaroons (<c>admin</c>, <c>readonly</c>, <c>invoice</c>) baked in memory under a root key
    /// delivered with the key of a locked start (NL-1352): handed back to the provisioner, never written by the node.
    /// </summary>
    /// <param name="storedRootKey">The delivered root key (the bytes <c>macaroons.key</c> would hold); not changed.</param>
    /// <param name="services">The node's services, for the credential context (<see cref="LndCredentialContext"/>).
    /// </param>
    public static IReadOnlyDictionary<string, byte[]> BakeDefaults(ReadOnlySpan<byte> storedRootKey,
                                                                   IServiceProvider services)
    {
        var context = LndCredentialContext.Resolve(services);
        var rootKey = LndCredentialEnrollment.EffectiveRootKey(storedRootKey.ToArray(), context);
        try
        {
            return new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["admin"] = NewMacaroon(rootKey, LndPermissions.Admin).Serialize(),
                ["readonly"] = NewMacaroon(rootKey, LndPermissions.Read).Serialize(),
                ["invoice"] = NewMacaroon(rootKey, LndPermissions.Invoice).Serialize()
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
        }
    }

    /// <summary>A new macaroon for <paramref name="ops"/> under <paramref name="rootKey"/> (LND's oven: a random 16-byte
    /// nonce, storage id <c>0</c>, location <c>lnd</c>).</summary>
    public static Macaroon NewMacaroon(ReadOnlySpan<byte> rootKey, IEnumerable<MacaroonOp> ops)
    {
        var id = new MacaroonId(RandomNumberGenerator.GetBytes(MacaroonId.NonceLength), LndPermissions.DefaultRootKeyId,
                                ops);
        return Macaroon.Create(rootKey, id.Encode(), LndPermissions.Location);
    }

    private static void Bake(string directory, string fileName, byte[] rootKey, IReadOnlyList<MacaroonOp> ops,
                             bool replace, ILogger? logger)
    {
        var path = Path.Combine(directory, fileName);
        if (File.Exists(path))
        {
            if (!replace)
                return;

            File.Delete(path);
        }

        WriteExclusive(path, NewMacaroon(rootKey, ops).Serialize());
        logger?.LogInformation("Baked {Path}", path);
    }

    /// <summary>Writes a new file readable by the owner only; fails when it exists.</summary>
    internal static void WriteExclusive(string path, ReadOnlySpan<byte> content)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var stream = new FileStream(path, options);
        stream.Write(content);
        stream.Flush(true);
    }
}