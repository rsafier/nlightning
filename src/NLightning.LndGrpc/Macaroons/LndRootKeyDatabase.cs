using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.LndGrpc.Macaroons;

using Domain.Node.Credentials;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Enums;
using Domain.Protocol.Interfaces;

/// <summary>
/// The macaroon root keys in the node's database (NL-1389, <see cref="IMacaroonRootKeyDbRepository"/>): each key
/// encrypted with the node data key (<see cref="NodeDataPurpose.MacaroonRootKey"/>, XChaCha20-Poly1305 bound to its root
/// key id), so the database never holds one in the clear and a row moved to another id or another node fails to
/// decrypt. A deletion is a row delete: a later process over the same database never sees the id again.
/// </summary>
/// <remarks>
/// Every call opens a scope of its own and commits before it returns; the API is synchronous like the root key store's
/// (the database work runs on the thread pool and is waited for).
/// </remarks>
public sealed class LndRootKeyDatabase
{
    private const int NonceLength = 24;
    private const int KeyLength = 32;

    private static readonly byte[] s_associatedDataLabel = "nltg-macaroon-root-key/v1"u8.ToArray();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISecureKeyManager _keyManager;
    private readonly TimeProvider _timeProvider;

    public LndRootKeyDatabase(IServiceScopeFactory scopeFactory, ISecureKeyManager keyManager,
                              TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _keyManager = keyManager ?? throw new ArgumentNullException(nameof(keyManager));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Whether the key manager can encrypt node data. False when it says it cannot (a VLS signer) or when the probe
    /// fails otherwise (a remote signer too old for <see cref="NodeDataPurpose.MacaroonRootKey"/>, or one that cannot be
    /// reached): <paramref name="failure"/> then says why.
    /// </summary>
    public bool IsSupported(out Exception? failure)
    {
        failure = null;
        try
        {
            _ = _keyManager.EncryptNodeData(NodeDataPurpose.MacaroonRootKey, new byte[NonceLength],
                                            AssociatedData(0), []);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            failure = e;
            return false;
        }
    }

    /// <summary>How many root keys are stored (nothing is decrypted).</summary>
    public int Count() =>
        Run(async unitOfWork => (await unitOfWork.MacaroonRootKeyDbRepository.GetAllAsync()).Count);

    /// <summary>Every stored root key, decrypted (the bytes <c>macaroons.key</c> would hold), by id.</summary>
    /// <exception cref="CryptographicException">A row that does not decrypt (another node's, or tampered).</exception>
    public Dictionary<ulong, byte[]> LoadAll()
    {
        var rows = Run(unitOfWork => unitOfWork.MacaroonRootKeyDbRepository.GetAllAsync());
        var keys = new Dictionary<ulong, byte[]>();
        foreach (var row in rows)
            keys[row.RootKeyId] = Decrypt(row);

        return keys;
    }

    /// <summary>The stored key of <paramref name="rootKeyId"/>, decrypted, or null when it has none.</summary>
    /// <exception cref="CryptographicException">The row does not decrypt.</exception>
    public byte[]? Load(ulong rootKeyId)
    {
        var row = Run(unitOfWork => unitOfWork.MacaroonRootKeyDbRepository.GetAsync(rootKeyId));
        return row is null ? null : Decrypt(row);
    }

    /// <summary>Replaces the stored key of <paramref name="rootKeyId"/> and commits; false when it had none.</summary>
    public bool Replace(ulong rootKeyId, byte[] storedKey) =>
        Run(unitOfWork => unitOfWork.MacaroonRootKeyDbRepository.ReplaceAsync(Encrypt(rootKeyId, storedKey)));

    /// <summary>Stores <paramref name="storedKey"/> under <paramref name="rootKeyId"/> and commits; an existing id
    /// fails.</summary>
    public void Add(ulong rootKeyId, byte[] storedKey)
    {
        var row = Encrypt(rootKeyId, storedKey);
        Run(async unitOfWork =>
        {
            unitOfWork.MacaroonRootKeyDbRepository.Add(row);
            await unitOfWork.SaveChangesAsync();
            return true;
        });
    }

    /// <summary>Deletes the key of <paramref name="rootKeyId"/> and commits; false when it had none.</summary>
    public bool Delete(ulong rootKeyId) =>
        Run(unitOfWork => unitOfWork.MacaroonRootKeyDbRepository.DeleteAsync(rootKeyId));

    private StoredMacaroonRootKey Encrypt(ulong rootKeyId, byte[] storedKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = _keyManager.EncryptNodeData(NodeDataPurpose.MacaroonRootKey, nonce,
                                                     AssociatedData(rootKeyId), storedKey);
        return new StoredMacaroonRootKey(rootKeyId, nonce, ciphertext, _timeProvider.GetUtcNow());
    }

    private byte[] Decrypt(StoredMacaroonRootKey row)
    {
        var key = _keyManager.DecryptNodeData(NodeDataPurpose.MacaroonRootKey, row.Nonce,
                                              AssociatedData(row.RootKeyId), row.Ciphertext);
        if (key.Length == KeyLength)
            return key;

        CryptographicOperations.ZeroMemory(key);
        throw new CryptographicException($"Macaroon root key {row.RootKeyId} is not 32 bytes");
    }

    private static byte[] AssociatedData(ulong rootKeyId)
    {
        var data = new byte[s_associatedDataLabel.Length + sizeof(ulong)];
        s_associatedDataLabel.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(s_associatedDataLabel.Length), rootKeyId);
        return data;
    }

    private T Run<T>(Func<IUnitOfWork, Task<T>> action) =>
        Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
        }).GetAwaiter().GetResult();
}