using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NLightning.Domain.Node.Provisioning;
using NLightning.Domain.Signing;

namespace NLightning.LndGrpc.Macaroons;

/// <summary>
/// The macaroon root keys by LND root key id (NL-1169): id 0 is <c>macaroons.key</c> (the default macaroons'),
/// every other id a 32-byte file <c>macaroon-root-keys/&lt;id&gt;.key</c> made 0600 the first time <c>BakeMacaroon</c>
/// asks for it. The storage id inside a macaroon is the decimal id as text, as LND writes it (<c>"0"</c>, <c>"7"</c>).
/// Deleting an id deletes its key and so invalidates every macaroon baked with it; id 0 cannot be deleted (LND's
/// <c>ErrDeletionForbidden</c>).
/// <para>With a root key delivered with the key of a locked start (NL-1352) the store is memory-only: id 0 is that key,
/// no file is read or written, and other ids are refused (they would need a file to survive a restart). With
/// <c>Node:Startup:RequireProvisionedSecrets</c> new ids are refused too.</para>
/// <para>With a <see cref="LndRootKeyDatabase"/> (the node's registration, NL-1389) the keys live in the database,
/// encrypted with the node data key, instead of files: a deleted id is a deleted row, so a later process over the same
/// database (a replacement host, a standby) never resurrects it from an older copy of the data directory, and one
/// without the files never makes a new root key 0 beside the stored one. The first start over a database without root
/// key 0 imports <c>macaroons.key</c> and <c>macaroon-root-keys/*.key</c> once (<see cref="Initialize"/>); after that
/// the files are never read again. A key manager that cannot encrypt node data (VLS) keeps the files.</para>
/// </summary>
public sealed class LndRootKeyStore
{
    private const string DirectoryName = "macaroon-root-keys";

    private readonly ConcurrentDictionary<ulong, byte[]> _keys = new();
    private readonly Lock _lock = new();
    private readonly string _directory;

    private readonly byte[]? _provisionedRootKey;
    private readonly bool _allowGenerate;
    private readonly ILogger? _logger;

    // The database the keys live in (NL-1389); null: files. Cleared when the key manager cannot encrypt node data
    private LndRootKeyDatabase? _database;
    private bool _initialized;

    public LndRootKeyStore(string dataDirectory, NodeSigningContext? context = null)
        : this(dataDirectory, context, null)
    {
    }

    /// <param name="dataDirectory">The data directory.</param>
    /// <param name="context">The credential context, or null.</param>
    /// <param name="secrets">The secrets a locked start received (NL-1352), or null.</param>
    /// <param name="database">The node's database for the keys (NL-1389), or null for files.</param>
    /// <param name="logger">A logger, or null.</param>
    public LndRootKeyStore(string dataDirectory, NodeSigningContext? context, ProvisionedSecrets? secrets,
                           LndRootKeyDatabase? database = null, ILogger? logger = null)
    {
        DataDirectory = dataDirectory;
        Context = context;
        _provisionedRootKey = secrets?.LndGrpcMacaroonRootKey;
        _allowGenerate = secrets is not { RefuseGeneration: true };
        _database = _provisionedRootKey is null ? database : null;
        _logger = logger;
        if (context is not null && _provisionedRootKey is null) LndCredentialEnrollment.Bind(dataDirectory, context);
        _directory = Path.Combine(dataDirectory, DirectoryName);
    }

    /// <summary>Whether the root key was delivered with the node key (memory only).</summary>
    public bool IsProvisioned => _provisionedRootKey is not null;

    /// <summary>Whether the keys live in the node's database (NL-1389); valid after <see cref="Initialize"/>.</summary>
    public bool UsesDatabase
    {
        get
        {
            lock (_lock)
                return _database is not null;
        }
    }

    /// <summary>
    /// Loads the keys from the database (NL-1389). The first start over a database without root key 0 imports the
    /// files (<c>macaroons.key</c> as id 0 and every <c>macaroon-root-keys/&lt;id&gt;.key</c>) and from then on never
    /// reads them; without a <c>macaroons.key</c> to import a new root key 0 is made and stored, unless
    /// <paramref name="allowGenerateDefault"/> is false (then the start fails: fail closed). Idempotent; nothing for a
    /// provisioned or file store.
    /// </summary>
    /// <returns>True when this call made a new root key 0 (the default macaroons must be baked again).</returns>
    /// <exception cref="InvalidOperationException">Root key 0 is missing and may not be made.</exception>
    public bool Initialize(bool allowGenerateDefault = true)
    {
        lock (_lock)
        {
            if (_initialized)
                return false;
            if (_database is not null && !_database.IsSupported())
            {
                _logger?.LogWarning("The key manager cannot encrypt node data: the LND gRPC macaroon root keys stay "
                                  + "files in {Directory}", DataDirectory);
                _database = null;
            }

            if (_database is null)
            {
                _initialized = true;
                return false;
            }

            var created = false;
            var stored = _database.LoadAll();
            if (!stored.ContainsKey(0))
            {
                created = ImportFiles(stored, allowGenerateDefault && _allowGenerate);
                foreach (var key in stored.Values)
                    CryptographicOperations.ZeroMemory(key);
                stored = _database.LoadAll();
            }

            foreach (var (id, key) in stored)
                _keys[id] = LndCredentialEnrollment.EffectiveRootKey(key, Context);
            _initialized = true;
            return created;
        }
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

        EnsureInitialized();
        if (UsesDatabase)
            return _keys.TryGetValue(id, out var stored) ? stored : null;

        if (_provisionedRootKey is not null)
            return id == 0
                       ? _keys.GetOrAdd(0, _ => LndCredentialEnrollment.EffectiveRootKey(_provisionedRootKey.ToArray(),
                                                                                           Context))
                       : null;

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
            if (_provisionedRootKey is not null)
                throw new InvalidOperationException("only root key id 0 is available: the root key was provisioned "
                                                  + "in memory and new root keys would need a file");
            if (!_allowGenerate)
                throw new InvalidOperationException("new root keys are refused: Node:Startup:RequireProvisionedSecrets "
                                                  + "forbids writing them to disk");

            key = MacaroonVerifier.NewRootKey();
            if (_database is not null)
            {
                _database.Add(id, key);
                return _keys.GetOrAdd(id, LndCredentialEnrollment.EffectiveRootKey(key, Context));
            }

            Directory.CreateDirectory(_directory);
            LndMacaroonFiles.WriteExclusive(PathOf(id), key);
            return _keys.GetOrAdd(id, LndCredentialEnrollment.EffectiveRootKey(key, Context));
        }
    }

    /// <summary>The ids that have a key, ascending (0 first).</summary>
    public IReadOnlyList<ulong> ListIds()
    {
        EnsureInitialized();
        if (UsesDatabase)
            return [.. _keys.Keys.Order()];

        var ids = new SortedSet<ulong>();
        if (TryGet(0) is not null)
            ids.Add(0);
        if (_provisionedRootKey is null && Directory.Exists(_directory))
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

        EnsureInitialized();
        lock (_lock)
        {
            _keys.TryRemove(id, out _);
            if (_provisionedRootKey is not null)
                return false;

            if (_database is not null)
            {
                // The row is the key; a file left from before the import goes too (it is never read again)
                var deleted = _database.Delete(id);
                var legacy = PathOf(id);
                if (File.Exists(legacy))
                    File.Delete(legacy);
                return deleted;
            }

            var path = PathOf(id);
            if (!File.Exists(path))
                return false;

            File.Delete(path);
            return true;
        }
    }

    private void EnsureInitialized()
    {
        bool initialized;
        lock (_lock)
            initialized = _initialized;
        if (!initialized)
            Initialize(_allowGenerate);
    }

    /// <summary>
    /// Stores the files' keys in the database once (the caller holds <see cref="_lock"/> and found no root key 0 there),
    /// or a new root key 0 when there is no <c>macaroons.key</c>. A concurrent first start that stored root key 0 first
    /// wins: its key is the one loaded.
    /// </summary>
    /// <returns>True when a new root key 0 was made.</returns>
    private bool ImportFiles(Dictionary<ulong, byte[]> stored, bool allowGenerateDefault)
    {
        var database = _database!;
        var rootKeyPath = PathOf(0);
        byte[] defaultKey;
        var created = false;
        if (File.Exists(rootKeyPath))
        {
            defaultKey = File.ReadAllBytes(rootKeyPath);
            if (defaultKey.Length != 32)
                throw new InvalidOperationException($"{rootKeyPath} is not a 32-byte macaroon root key; delete it to "
                                                  + "make a new one (every macaroon baked before stops working).");
        }
        else if (!allowGenerateDefault)
        {
            throw new InvalidOperationException(
                "The LND gRPC macaroon root key 0 is neither in the database nor in " + rootKeyPath + ", and making a "
              + "new one is refused (LndGrpc:RequireExistingRootKey or Node:Startup:RequireProvisionedSecrets): "
              + "restore the database or deliver lndGrpcMacaroonRootKey with the key.");
        }
        else
        {
            defaultKey = MacaroonVerifier.NewRootKey();
            created = true;
        }

        try
        {
            if (Directory.Exists(_directory))
            {
                foreach (var file in Directory.EnumerateFiles(_directory, "*.key"))
                {
                    if (!ulong.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.None,
                                        CultureInfo.InvariantCulture, out var id)
                     || id == 0 || stored.ContainsKey(id))
                        continue;

                    var key = File.ReadAllBytes(file);
                    try
                    {
                        if (key.Length == 32)
                            database.Add(id, key);
                    }
                    catch (Exception e)
                    {
                        // Another process over this database imported it first, or the database refused it: the
                        // import of root key 0 below decides whether the start goes on
                        _logger?.LogWarning(e, "Could not move LND gRPC macaroon root key {Id} into the database", id);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(key);
                    }
                }
            }

            // Root key 0 last: its row marks the import done
            try
            {
                database.Add(0, defaultKey);
            }
            catch
            {
                // Another process over this database stored root key 0 first: its key is the node's
                if (!database.LoadAll().ContainsKey(0))
                    throw;
                return false;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(defaultKey);
        }

        if (created)
            _logger?.LogInformation("Created a new LND gRPC macaroon root key in the node's database");
        else
            _logger?.LogInformation("Moved the LND gRPC macaroon root keys of {Directory} into the node's database; "
                                  + "the files are no longer read", DataDirectory);
        return created;
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