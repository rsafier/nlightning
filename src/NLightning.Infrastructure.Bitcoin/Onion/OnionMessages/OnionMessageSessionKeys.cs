using System.Security.Cryptography;

namespace NLightning.Infrastructure.Bitcoin.Onion.OnionMessages;

using Crypto.Contexts;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Fresh session keys for onion messages: a Sphinx session key or the first ephemeral key of a blinded path.
/// </summary>
internal static class OnionMessageSessionKeys
{
    /// <summary>
    /// A uniformly random valid secp256k1 scalar from the CSPRNG.
    /// </summary>
    public static PrivKey Create()
    {
        var key = new byte[CryptoConstants.PrivkeyLen];
        while (true)
        {
            RandomNumberGenerator.Fill(key);
            if (!NLightningCryptoContext.Instance.TryCreateECPrivKey(key, out var ecPrivKey) || ecPrivKey is null)
                continue;

            ecPrivKey.Dispose();
            return new PrivKey(key);
        }
    }
}