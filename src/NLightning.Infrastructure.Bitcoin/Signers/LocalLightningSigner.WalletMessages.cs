using System.Security.Cryptography;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Signers;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Wallet.Models;
using Domain.Exceptions;

/// <summary>
/// LND's walletrpc <c>SignMessageWithAddr</c> (NL-1186): Bitcoin Core message signatures with a wallet address's key,
/// whose private key never leaves the signer.
/// </summary>
public partial class LocalLightningSigner
{
    /// <inheritdoc />
    public byte[] SignWalletMessage(WalletAddressModel address, byte[] message)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(message);
        if (address.AddressType is not (AddressType.P2Wpkh or AddressType.P2Tr))
            throw new SignerException($"Wallet address {address.Address} of type {address.AddressType} cannot sign "
                                    + "messages");

        Script expected;
        try
        {
            expected = BitcoinAddress.Create(address.Address, _network).ScriptPubKey;
        }
        catch (FormatException e)
        {
            throw new SignerException($"Wallet address {address.Address} is not an address of this network", e);
        }

        var child = address.DerivationIndex ?? address.Index;
        var extKeyBytes = address.AccountIndex != 0
            ? _secureKeyManager.GetDepositKeyAtIndex(address.AddressType, address.AccountIndex, child, address.IsChange)
            : address.AddressType == AddressType.P2Wpkh
                ? _secureKeyManager.GetDepositP2WpkhKeyAtIndex(child, address.IsChange)
                : _secureKeyManager.GetDepositP2TrKeyAtIndex(child, address.IsChange);
        Key key;
        try
        {
            key = ExtKey.CreateFromBytes(extKeyBytes).PrivateKey;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(extKeyBytes);
        }

        using (key)
        {
            // The derived key must own the address (LND signs with the address's own key; P2TR with its untweaked
            // internal key, so the verifier tweaks the recovered key)
            var derived = address.AddressType == AddressType.P2Wpkh
                              ? key.PubKey.WitHash.ScriptPubKey
                              : key.PubKey.GetTaprootFullPubKey().ScriptPubKey;
            if (derived != expected)
                throw new SignerException($"The key derived for wallet address {address.Address} does not match it");

            var privateKey = key.ToBytes();
            try
            {
                return LightningMessageSignature.Sign(privateKey, BitcoinMessageSignature.Digest(message));
            }
            catch (CryptographicException e)
            {
                throw new SignerException(e.Message, e, "Internal error");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
        }
    }
}