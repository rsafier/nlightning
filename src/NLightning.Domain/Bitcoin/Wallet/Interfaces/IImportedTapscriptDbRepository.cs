namespace NLightning.Domain.Bitcoin.Wallet.Interfaces;

/// <summary>Watch-only scripts. These records never create wallet UTXOs, reserves or accounting income.</summary>
public sealed record ImportedTapscript(byte[] Script, byte[] InternalKey, byte[] Definition, uint CreatedHeight);

public interface IImportedTapscriptDbRepository
{
    Task<IReadOnlyList<ImportedTapscript>> ListAsync();
    Task<ImportedTapscript?> GetAsync(byte[] script);
    void Add(ImportedTapscript script);
    Task<ImportedWatchIndex?> GetIndexAsync();
    Task SetIndexAsync(ImportedWatchIndex index);
}
/// <summary>Atomic checkpoint and raw relevant transactions of the watch-only imported history.</summary>
public sealed record ImportedWatchIndex(uint Height, byte[] BlockHash, string ScriptSet, byte[] History);