using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Managers;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Protocol.Interfaces;

public partial class SecureKeyManager
{
    public DepositAccountInfo? GetDepositAccount(AddressType addressType, uint accountIndex)
    {
        if (addressType is not (AddressType.P2Wpkh or AddressType.P2Tr)) return null;
        var path = GetDepositAccountPath(addressType, accountIndex);
        var master = GetMasterKey();
        ExtKey? account = null;
        try
        {
            account = master.Derive(path);
            return new DepositAccountInfo(account.Neuter().ToString(_network), $"m/{path}",
                master.GetPublicKey().GetHDFingerPrint().ToBytes());
        }
        finally
        {
            account?.PrivateKey.Dispose();
            master.PrivateKey.Dispose();
        }
    }

    public ExtPrivKey GetDepositKeyAtIndex(AddressType addressType, uint accountIndex, uint index, bool isChange)
    {
        if (index >= 0x80000000) throw new ArgumentOutOfRangeException(nameof(index));
        var path = GetDepositAccountPath(addressType, accountIndex);
        var master = GetMasterKey();
        ExtKey? account = null;
        ExtKey? branch = null;
        ExtKey? child = null;
        try
        {
            account = master.Derive(path);
            branch = account.Derive(isChange ? 1u : 0u);
            child = branch.Derive(index);
            return child.ToBytes();
        }
        finally
        {
            child?.PrivateKey.Dispose();
            branch?.PrivateKey.Dispose();
            account?.PrivateKey.Dispose();
            master.PrivateKey.Dispose();
        }
    }

    private KeyPath GetDepositAccountPath(AddressType addressType, uint accountIndex)
    {
        if (accountIndex >= 0x80000000) throw new ArgumentOutOfRangeException(nameof(accountIndex));
        // Existing deposit scopes use coin type zero on every network. Account zero must remain byte-for-byte stable.
        return addressType switch
        {
            AddressType.P2Wpkh => accountIndex == 0 ? _depositP2WpkhKeyPath : new KeyPath($"84'/0'/{accountIndex}'"),
            AddressType.P2Tr => accountIndex == 0 ? _depositP2TrKeyPath : new KeyPath($"86'/0'/{accountIndex}'"),
            _ => throw new ArgumentOutOfRangeException(nameof(addressType))
        };
    }
}