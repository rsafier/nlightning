using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NLightning.Domain.Node.Provisioning;
using NLightning.Domain.Signing;

namespace NLightning.LndGrpc.Macaroons;

/// <summary>
/// The macaroon root keys by LND root key id (NL-1169): id 0 is the default macaroons' key, every other id a key made
/// the first time <c>BakeMacaroon</c> asks for it. The storage id inside a macaroon is the decimal id as text, as LND
/// writes it (<c>"0"</c>, <c>"7"</c>). Deleting an id deletes its key and so invalidates every macaroon baked with it;
/// id 0 cannot be deleted (LND's <c>ErrDeletionForbidden</c>) but can be rotated (<see cref="RotateDefault"/>).
/// <para>With a <see cref="LndRootKeyDatabase"/> (the node's registration, NL-1389) the keys live in the database,
/// encrypted with the node data key: a deleted id is a deleted row, so a later process over the same database (a
/// replacement host, a standby) never resurrects it from an older copy of the data directory, and one without the files
/// never makes a new root key 0 beside the stored one. The first start over a database without root key 0 imports
/// <c>macaroons.key</c> and <c>macaroon-root-keys/*.key</c> once (<see cref="Initialize"/>); after that the files are
/// never read again, except that a <c>macaroons.key</c> that differs from the stored key 0 refuses the start (a stale or
/// foreign directory). Keys are re-read from the database when their cached copy is older than
/// <see cref="RefreshInterval"/>, so an id another process deleted or rotated stops verifying here too. A key manager
/// that cannot encrypt node data (VLS, a remote signer too old for it) keeps the files while the database holds no
/// root key.</para>
/// <para>Without a database the keys are files: id 0 is <c>macaroons.key</c>, every other id a 32-byte file
/// <c>macaroon-root-keys/&lt;id&gt;.key</c> made 0600.</para>
/// <para>With a root key delivered with the key of a locked start (NL-1352) id 0 is that key (memory only, never
/// written); it must equal a stored root key 0. Other ids live in the database when there is one and are refused
/// otherwise (they would need a file). With <c>Node:Startup:RequireProvisionedSecrets</c> new ids are refused too.</para>
/// </summary>
public sealed class LndRootKeyStore
{
    /// <summary>How long a key read from the database is used before it is read again.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private const string DirectoryName = "macaroon-root-keys";

    private readonly ConcurrentDictionary<ulong, CachedKey> _keys = new();
    private readonly Lock _lock = new();
    private readonly string _directory;

    private readonly byte[]? _provisionedRootKey;
    private readonly bool _allowGenerate;
    private readonly ILogger? _logger;
    private readonly TimeProvider _timeProvider;

    // The database the keys live in (NL-1389); null: files. Cleared when the key manager cannot encrypt node data and
    // the database holds no root key
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
    /// <param name="timeProvider">The clock of <see cref="RefreshInterval"/>; the system's when null.</param>
    public LndRootKeyStore(string dataDirectory, NodeSigningContext? context, ProvisionedSecrets? secrets,
                           LndRootKeyDatabase? database = null, ILogger? logger = null,
                           TimeProvider? timeProvider = null)
    {
        DataDirectory = dataDirectory;
        Context = context;
        _provisionedRootKey = secrets?.LndGrpcMacaroonRootKey;
        _allowGenerate = secrets is not { RefuseGeneration: true };
        _database = database;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

    /// <summary>The data directory (<c>LndGrpc:DataDirectory</c>).</summary>
    public string DataDirectory { get; }

    public NodeSigningContext? Context { get; }

    /// <summary>
    /// Loads the keys from the database (NL-1389). The first start over a database without root key 0 imports the
    /// files (<c>macaroons.key</c> as id 0 and every <c>macaroon-root-keys/&lt;id&gt;.key</c>) and from then on never
    /// reads them; without a <c>macaroons.key</c> to import a new root key 0 is made and stored, unless
    /// <paramref name="allowGenerateDefault"/> is false, or <paramref name="refuseAfterEarlierUse"/> is true and the
    /// database already holds other root keys (an earlier use whose root key 0 is gone): then the start fails (fail
    /// closed). A <c>macaroons.key</c> (or a delivered root key) that differs from the stored root key 0 fails the start
    /// too. Idempotent; nothing for a file store.
    /// </summary>
    /// <returns>True when this call made a new root key 0 (the default macaroons must be baked again).</returns>
    /// <exception cref="InvalidOperationException">Root key 0 is missing and may not be made, or it conflicts.
    /// </exception>
    public bool Initialize(bool allowGenerateDefault = true, bool refuseAfterEarlierUse = true)
    {
        lock (_lock)
        {
            if (_initialized)
                return false;

            if (_database is not null && !_database.IsSupported(out var failure))
            {
                // A database that already holds root keys must not be bypassed by files an older copy may hold
                if (_database.Count() > 0)
                    throw new InvalidOperationException(
                        "The LND gRPC macaroon root keys are in the node's database but the key manager cannot "
                      + "decrypt them; the server does not start", failure);

                _logger?.LogWarning(failure, "The key manager cannot encrypt node data: the LND gRPC macaroon root "
                                           + "keys stay files in {Directory}", DataDirectory);
                _database = null;
            }

            var created = false;
            if (_database is not null)
            {
                var stored = _database.LoadAll();
                try
                {
                    if (_provisionedRootKey is not null)
                        CheckProvisionedMatches(stored);
                    else if (stored.TryGetValue(0, out var rootKey))
                        CheckLegacyFileMatches(rootKey);
                    else
                        created = ImportFiles(stored, allowGenerateDefault && _allowGenerate, refuseAfterEarlierUse);
                }
                finally
                {
                    foreach (var key in stored.Values)
                        CryptographicOperations.ZeroMemory(key);
                }

                foreach (var (id, key) in _database.LoadAll())
                {
                    if (id != 0 || _provisionedRootKey is null)
                        Cache(id, key);
                }
            }

            _initialized = true;
            return created;
        }
    }

    /// <summary>The key of a macaroon's storage id, or null when the id is not one of ours or has no key.</summary>
    public byte[]? TryGet(ReadOnlySpan<byte> storageId) =>
        TryParseStorageId(storageId, out var id) ? TryGet(id) : null;

    /// <summary>The key of <paramref name="id"/>, or null when it has none.</summary>
    public byte[]? TryGet(ulong id)
    {
        if (id == 0 && _provisionedRootKey is not null)
            return GetCached(0)
                ?? Cache(0, _provisionedRootKey.ToArray());

        EnsureInitialized();
        if (UsesDatabase)
            return TryGetStored(id);

        if (GetCached(id) is { } cached)
            return cached;
        if (_provisionedRootKey is not null)
            return null;

        var path = PathOf(id);
        if (!File.Exists(path))
            return null;

        var key = File.ReadAllBytes(path);
        return key.Length == 32 ? Cache(id, key) : null;
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
            if (_provisionedRootKey is not null && !UsesDatabase)
                throw new InvalidOperationException("only root key id 0 is available: the root key was provisioned "
                                                  + "in memory and new root keys would need a file");
            if (!_allowGenerate)
                throw new InvalidOperationException("new root keys are refused: Node:Startup:RequireProvisionedSecrets "
                                                  + "forbids writing them to disk");

            var created = MacaroonVerifier.NewRootKey();
            if (_database is not null)
            {
                try
                {
                    _database.Add(id, created);
                }
                catch (Exception e)
                {
                    // Another process over this database made the id first: its key is the one
                    CryptographicOperations.ZeroMemory(created);
                    if (_database.Load(id) is not { } stored)
                        throw new InvalidOperationException($"Could not store macaroon root key {id}", e);
                    return Cache(id, stored);
                }

                return Cache(id, created);
            }

            Directory.CreateDirectory(_directory);
            LndMacaroonFiles.WriteExclusive(PathOf(id), created);
            return Cache(id, created);
        }
    }

    /// <summary>The ids that have a key, ascending (0 first).</summary>
    public IReadOnlyList<ulong> ListIds()
    {
        EnsureInitialized();
        if (UsesDatabase)
        {
            var stored = _database!.LoadAll();
            foreach (var cachedId in _keys.Keys.Where(k => !stored.ContainsKey(k) && !IsProvisionedDefault(k)))
                _keys.TryRemove(cachedId, out _);
            foreach (var (id, key) in stored)
            {
                if (IsProvisionedDefault(id))
                    CryptographicOperations.ZeroMemory(key);
                else
                    Cache(id, key);
            }

            if (_provisionedRootKey is not null)
                stored[0] = [];
            return [.. stored.Keys.Order()];
        }

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
            if (_database is not null)
            {
                // The row is the key: it goes first, and only a committed delete leaves the cache (a refused or failed
                // one keeps the id verifying as it still does in the database). A file left from before the import
                // goes too (it is never read again)
                var deleted = _database.Delete(id);
                _keys.TryRemove(id, out _);
                var legacy = PathOf(id);
                if (File.Exists(legacy))
                    File.Delete(legacy);
                return deleted;
            }

            if (_provisionedRootKey is not null)
                return false;

            var path = PathOf(id);
            if (!File.Exists(path))
                return false;

            File.Delete(path);
            _keys.TryRemove(id, out _);
            return true;
        }
    }

    /// <summary>
    /// Replaces root key 0 with a new random key (NL-1389, <c>nltg rotatemacaroonrootkey</c>): every macaroon baked
    /// with the old one stops verifying at once. In the database the row is replaced under the node write fence and a
    /// <c>macaroons.key</c> left from before the import is deleted; without a database <c>macaroons.key</c> is replaced.
    /// The caller bakes the default macaroons again with the returned key.
    /// </summary>
    /// <returns>The new effective root key 0.</returns>
    /// <exception cref="InvalidOperationException">The root key was delivered with the key (rotate it there), or
    /// generating keys is refused.</exception>
    public byte[] RotateDefault()
    {
        if (_provisionedRootKey is not null)
            throw new InvalidOperationException("the macaroon root key was delivered with the node key: rotate it at "
                                              + "the provisioner");
        if (!_allowGenerate)
            throw new InvalidOperationException("new root keys are refused: Node:Startup:RequireProvisionedSecrets "
                                              + "forbids writing them");

        EnsureInitialized();
        lock (_lock)
        {
            var key = MacaroonVerifier.NewRootKey();
            var rootKeyPath = PathOf(0);
            if (_database is not null)
            {
                if (!_database.Replace(0, key))
                    _database.Add(0, key);
                if (File.Exists(rootKeyPath))
                    File.Delete(rootKeyPath);
            }
            else
            {
                if (File.Exists(rootKeyPath))
                    File.Delete(rootKeyPath);
                LndMacaroonFiles.WriteExclusive(rootKeyPath, key);
            }

            _logger?.LogWarning("Rotated the LND gRPC macaroon root key: every macaroon baked before stops working");
            return Cache(0, key);
        }
    }

    /// <summary>LND's storage id of a root key id: its decimal text.</summary>
    public static byte[] StorageIdOf(ulong id) =>
        System.Text.Encoding.ASCII.GetBytes(id.ToString(CultureInfo.InvariantCulture));

    private bool IsProvisionedDefault(ulong id) => id == 0 && _provisionedRootKey is not null;

    /// <summary>A database key from the cache while it is fresh, else read again (a deleted id leaves the cache).
    /// </summary>
    private byte[]? TryGetStored(ulong id)
    {
        if (_keys.TryGetValue(id, out var cached) && _timeProvider.GetUtcNow() - cached.LoadedAt < RefreshInterval)
            return cached.Key;

        byte[]? stored;
        try
        {
            stored = _database!.Load(id);
        }
        catch (Exception e) when (e is not CryptographicException)
        {
            // The database cannot be read now: a key read before keeps working until it can
            _logger?.LogDebug(e, "Could not read LND gRPC macaroon root key {Id} again", id);
            return cached?.Key;
        }

        if (stored is null)
        {
            _keys.TryRemove(id, out _);
            return null;
        }

        return Cache(id, stored);
    }

    private byte[]? GetCached(ulong id) => _keys.TryGetValue(id, out var cached) ? cached.Key : null;

    /// <summary>Caches the effective key of a stored key (the stored bytes are consumed).</summary>
    private byte[] Cache(ulong id, byte[] storedKey)
    {
        var entry = new CachedKey(LndCredentialEnrollment.EffectiveRootKey(storedKey, Context),
                                  _timeProvider.GetUtcNow());
        _keys[id] = entry;
        return entry.Key;
    }

    private void EnsureInitialized()
    {
        bool initialized;
        lock (_lock)
            initialized = _initialized;
        if (!initialized)
            Initialize(_allowGenerate);
    }

    /// <summary>A delivered root key must be the stored root key 0 when there is one.</summary>
    private void CheckProvisionedMatches(Dictionary<ulong, byte[]> stored)
    {
        if (stored.TryGetValue(0, out var rootKey)
         && !CryptographicOperations.FixedTimeEquals(rootKey, _provisionedRootKey))
            throw new InvalidOperationException(
                "The delivered lndGrpcMacaroonRootKey differs from the root key 0 in the node's database: deliver the "
              + "matching key, or rotate it on the node that holds the database");
    }

    /// <summary>A <c>macaroons.key</c> left beside a stored root key 0 must be the same key.</summary>
    private void CheckLegacyFileMatches(byte[] rootKey)
    {
        var rootKeyPath = PathOf(0);
        if (!File.Exists(rootKeyPath))
            return;

        var file = File.ReadAllBytes(rootKeyPath);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(file, rootKey))
                throw new InvalidOperationException(
                    $"{rootKeyPath} differs from the LND gRPC macaroon root key 0 in the node's database (a data "
                  + "directory from another node, or from before a rotation): the database's key is the node's. Delete "
                  + "the file (it is no longer read) or start against the matching database.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(file);
        }
    }

    /// <summary>
    /// Stores the files' keys in the database once (the caller holds <see cref="_lock"/> and found no root key 0 there),
    /// or a new root key 0 when there is no <c>macaroons.key</c>. Every id is stored before root key 0, whose row marks
    /// the import done: an id that cannot be stored fails the start before it (a row another process stored first is
    /// accepted only when it holds the same key). A concurrent first start that stored root key 0 first wins.
    /// </summary>
    /// <returns>True when a new root key 0 was made.</returns>
    private bool ImportFiles(Dictionary<ulong, byte[]> stored, bool allowGenerateDefault, bool refuseAfterEarlierUse)
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
        else if (refuseAfterEarlierUse && stored.Count > 0)
        {
            throw new InvalidOperationException(
                "The node's database holds LND gRPC macaroon root keys but no root key 0, and " + rootKeyPath
              + " is missing: making a new one is refused. Restore the matching database or macaroons.key, or set "
              + "LndGrpc:RequireExistingRootKey=false to make a new root key 0 (every macaroon baked before stops "
              + "working).");
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
                                        CultureInfo.InvariantCulture, out var id) || id == 0)
                        continue;

                    var key = File.ReadAllBytes(file);
                    try
                    {
                        if (key.Length == 32)
                            ImportOne(id, key, stored);
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
            catch (Exception e)
            {
                // Another process over this database stored root key 0 first: its key is the node's when it is the
                // same file's (or this start made one); a different key from the same file is a conflict
                var winner = database.Load(0) ?? throw new InvalidOperationException(
                                 "Could not store the LND gRPC macaroon root key 0", e);
                try
                {
                    if (!created && !CryptographicOperations.FixedTimeEquals(winner, defaultKey))
                        throw new InvalidOperationException(
                            $"Another process stored a different LND gRPC macaroon root key 0 than {rootKeyPath}", e);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(winner);
                }

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

    /// <summary>Stores one id of the files; a row already there must hold the same key, else the start fails.</summary>
    private void ImportOne(ulong id, byte[] key, Dictionary<ulong, byte[]> stored)
    {
        if (stored.TryGetValue(id, out var existing))
        {
            if (!CryptographicOperations.FixedTimeEquals(existing, key))
                throw new InvalidOperationException(
                    $"macaroon-root-keys/{id}.key differs from root key {id} in the node's database");
            return;
        }

        try
        {
            _database!.Add(id, key);
        }
        catch (Exception e)
        {
            // Accepted only when another process stored the same key first
            var winner = _database!.Load(id) ?? throw new InvalidOperationException(
                             $"Could not move LND gRPC macaroon root key {id} into the node's database", e);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(winner, key))
                    throw new InvalidOperationException(
                        $"Another process stored a different LND gRPC macaroon root key {id}", e);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(winner);
            }
        }
    }

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

    private sealed record CachedKey(byte[] Key, DateTimeOffset LoadedAt);
}