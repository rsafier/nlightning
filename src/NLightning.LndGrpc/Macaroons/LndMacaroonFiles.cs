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
    public static byte[] EnsureCreated(string directory, ILogger? logger = null, NodeSigningContext? context = null)
    {
        if (context is not null) LndCredentialEnrollment.Bind(directory, context);
        Directory.CreateDirectory(directory);
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
        else
        {
            rootKey = MacaroonVerifier.NewRootKey();
            WriteExclusive(rootKeyPath, rootKey);
            rotated = true;
            logger?.LogInformation("Created a new LND gRPC macaroon root key in {Path}", rootKeyPath);
        }

        rootKey = LndCredentialEnrollment.EffectiveRootKey(rootKey, context);
        Bake(directory, AdminFileName, rootKey, LndPermissions.Admin, rotated, logger);
        Bake(directory, ReadOnlyFileName, rootKey, LndPermissions.Read, rotated, logger);
        Bake(directory, InvoiceFileName, rootKey, LndPermissions.Invoice, rotated, logger);
        return rootKey;
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