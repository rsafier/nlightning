namespace NLightning.Infrastructure.Bitcoin.Signers;

public partial class LocalLightningSigner
{
    private NativeNonceStateStore? _nativeNonceStore;

    /// <summary>Attach the signer-owned journal before accepting requests or restoring durable workflows.</summary>
    public void AttachNonceStateStore(NativeNonceStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (Interlocked.CompareExchange(ref _nativeNonceStore, store, null) is { } existing && !ReferenceEquals(existing, store))
            throw new InvalidOperationException("A native nonce journal is already attached to this signer.");
    }
}