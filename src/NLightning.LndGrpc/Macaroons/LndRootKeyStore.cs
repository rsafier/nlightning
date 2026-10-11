using System.Collections.Concurrent;
using System.Globalization;
using NLightning.Domain.Signing;

namespace NLightning.LndGrpc.Macaroons;

/// <summary>
/// The macaroon root keys by LND root key id (NL-1169): id 0 is <c>macaroons.key</c> (the default macaroons'),
/// every other id a 32-byte file <c>macaroon-root-keys/&lt;id&gt;.key</c> made 0600 the first time <c>BakeMacaroon</c>
/// asks for it. The storage id inside a macaroon is the decimal id as text, as LND writes it (<c>"0"</c>, <c>"7"</c>).
/// Deleting an id deletes its key and so invalidates every macaroon baked with it; id 0 cannot be deleted (LND's
/// <c>ErrDeletionForbidden</c>).
/// </summary>
public sealed class LndRootKeyStore
{
    private const string DirectoryName = "macaroon-root-keys";

    private readonly ConcurrentDictionary<ulong, byte[]> _keys = new();
    private readonly Lock _lock = new();
    private readonly string _directory;

    public LndRootKeyStore(string dataDirectory, NodeSigningContext? context = null)
    {
        DataDirectory = dataDirectory;
        Context = context;
        if (context is not null) LndCredentialEnrollment.Bind(dataDirectory, context);
        _directory = Path.Combine(dataDirectory, DirectoryName);
    }

    /// <summary>The data directory (<c>LndGrpc:DataDirectory</c>).</summary>
    public string DataDirectory { get; }
    public NodeSigningContext? Context { get; }

    /// <summary>The key of a macaroon's storage id, or null when the id is not one of ours or has no key.</summary>
    public byte[]? TryGet(ReadOnlySpan<byte> storageId) =>
        TryParseStorageId(storageId, out var id) ? TryGet(id) : null;

    /// <summary>The key of <paramref name="id"/>, or null when it has none.</summary>
    public byte[]? TryGet(ulong id)
    {
        if (_keys.TryGetValue(id, out var cached))
            return cached;

        var path = PathOf(id);
        if (!File.Exists(path))
            return null;

        var key = File.ReadAllBytes(path);
        return key.Length == 32 ? _keys.GetOrAdd(id, LndCredentialEnrollment.EffectiveRootKey(key, Context)) : null;
    }

    /// <summary>The key of <paramref name="id"/>, made now when it has none (id 0 must exist already).</summary>
    public byte[] GetOrCreate(ulong id)
    {
        lock (_lock)
        {
            if (TryGet(id) is { } key)
                return key;
            if (id == 0)
                throw new InvalidOperationException("The default macaroon root key is missing");

            Directory.CreateDirectory(_directory);
            key = MacaroonVerifier.NewRootKey();
            LndMacaroonFiles.WriteExclusive(PathOf(id), key);
            return _keys.GetOrAdd(id, LndCredentialEnrollment.EffectiveRootKey(key, Context));
        }
    }

    /// <summary>The ids that have a key, ascending (0 first).</summary>
    public IReadOnlyList<ulong> ListIds()
    {
        var ids = new SortedSet<ulong>();
        if (TryGet(0) is not null)
            ids.Add(0);
        if (Directory.Exists(_directory))
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*.key"))
            {
                if (ulong.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.None,
                                   CultureInfo.InvariantCulture, out var id) && id != 0)
                    ids.Add(id);
            }
        }

        return [.. ids];
    }

    /// <summary>Deletes the key of <paramref name="id"/>; false when it had none.</summary>
    /// <exception cref="InvalidOperationException">Id 0 (the default macaroons' key).</exception>
    public bool Delete(ulong id)
    {
        if (id == 0)
            throw new InvalidOperationException("the specified ID cannot be deleted");

        lock (_lock)
        {
            _keys.TryRemove(id, out _);
            var path = PathOf(id);
            if (!File.Exists(path))
                return false;

            File.Delete(path);
            return true;
        }
    }

    /// <summary>LND's storage id of a root key id: its decimal text.</summary>
    public static byte[] StorageIdOf(ulong id) =>
        System.Text.Encoding.ASCII.GetBytes(id.ToString(CultureInfo.InvariantCulture));

    private static bool TryParseStorageId(ReadOnlySpan<byte> storageId, out ulong id)
    {
        id = 0;
        if (storageId.IsEmpty || storageId.Length > 20 || (storageId.Length > 1 && storageId[0] == (byte)'0'))
            return false;

        foreach (var b in storageId)
        {
            if (b is < (byte)'0' or > (byte)'9')
                return false;
        }

        return ulong.TryParse(storageId, NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    private string PathOf(ulong id) =>
        id == 0
            ? Path.Combine(DataDirectory, LndMacaroonFiles.RootKeyFileName)
            : Path.Combine(_directory, id.ToString(CultureInfo.InvariantCulture) + ".key");
}