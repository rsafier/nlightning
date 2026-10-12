using NBitcoin;
using NLightning.Domain.Bitcoin.Enums;
using NLightning.Domain.Protocol.Interfaces;

namespace NLightning.Infrastructure.RemoteSigning;

public sealed record NativeWalletKeyLocator(uint Index, bool IsChange, AddressType AddressType);

/// <summary>Installed signer ownership, derived from keys rather than wallet snapshots or caller labels.</summary>
public interface INativeSignerWalletScriptRegistry
{
    bool IsOwned(NativeSignerBinding binding, byte[] scriptPubKey);
}

public interface INativeSignerWalletDerivationRegistry : INativeSignerWalletScriptRegistry
{
    byte[] GetScript(NativeSignerBinding binding, NativeWalletKeyLocator derivation);
}

/// <summary>Immutable set of administrator-installed account-zero derivations belonging to one signer enrollment.</summary>
public sealed class NativeSignerWalletScriptRegistry : INativeSignerWalletDerivationRegistry
{
    private readonly NativeSignerBinding _binding;
    private readonly HashSet<string> _scripts = new(StringComparer.Ordinal);
    private readonly Dictionary<NativeWalletKeyLocator, byte[]> _derivations = [];

    public NativeSignerWalletScriptRegistry(NativeSignerBinding binding, ISecureKeyManager keys,
                                            IEnumerable<NativeWalletKeyLocator> derivations)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(derivations);
        if (keys.GetNodePubKey().ToString() != binding.PublicKey)
            throw new UnauthorizedAccessException("Wallet registry keys do not match signer enrollment.");
        _binding = binding;
        var installed = new HashSet<NativeWalletKeyLocator>();
        foreach (var locator in derivations)
        {
            if (!installed.Add(locator)) throw new ArgumentException("Duplicate wallet derivation.");
            if (locator.AddressType is not (AddressType.P2Wpkh or AddressType.P2Tr))
                throw new NotSupportedException("Wallet registry requires a supported account-zero derivation.");
            var pubkey = new PubKey((byte[])keys.GetWalletPublicKey(locator.Index, locator.IsChange, locator.AddressType));
            var script = locator.AddressType == AddressType.P2Wpkh
                ? pubkey.WitHash.ScriptPubKey : pubkey.GetTaprootFullPubKey().ScriptPubKey;
            if (!_scripts.Add(Convert.ToHexString(script.ToBytes())))
                throw new ArgumentException("Wallet derivations produced duplicate scripts.");
            _derivations.Add(locator, script.ToBytes());
        }
    }

    public bool IsOwned(NativeSignerBinding binding, byte[] scriptPubKey) =>
        binding == _binding && _scripts.Contains(Convert.ToHexString(scriptPubKey));

    public byte[] GetScript(NativeSignerBinding binding, NativeWalletKeyLocator derivation)
    {
        if (binding != _binding || !_derivations.TryGetValue(derivation, out var script))
            throw new UnauthorizedAccessException("Wallet derivation is not installed for this signer enrollment.");
        return script.ToArray();
    }
}