namespace NLightning.Domain.Node.Credentials;

/// <summary>
/// The LND gRPC macaroon root keys of this database (NL-1389, table <c>MacaroonRootKeys</c>), reached through
/// <c>IUnitOfWork.MacaroonRootKeyDbRepository</c>. A deleted id is a deleted row, so a later process over the same
/// database (a replacement host) never resurrects it from an older copy of the data directory.
/// </summary>
public interface IMacaroonRootKeyDbRepository
{
    /// <summary>Every stored root key.</summary>
    Task<IReadOnlyList<StoredMacaroonRootKey>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Stages a new root key (written by the unit of work's save; an existing id fails that save).</summary>
    void Add(StoredMacaroonRootKey key);

    /// <summary>Deletes the root key of <paramref name="rootKeyId"/> and commits; false when it had none.</summary>
    Task<bool> DeleteAsync(ulong rootKeyId, CancellationToken cancellationToken = default);
}