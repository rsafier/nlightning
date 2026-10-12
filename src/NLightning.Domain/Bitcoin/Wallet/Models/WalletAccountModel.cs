namespace NLightning.Domain.Bitcoin.Wallet.Models;

using Enums;

/// <summary>Durable account discovery metadata. Watch-only accounts never acquire spendable custody.</summary>
public sealed record WalletAccountModel(string Name, AddressType AddressType, uint AccountIndex,
                                       string ExtendedPublicKey, byte[] MasterFingerprint,
                                       string DerivationPath, bool WatchOnly, uint BirthdayHeight,
                                       uint ExternalKeyCount = 0, uint InternalKeyCount = 0);