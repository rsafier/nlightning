# Changelog

All notable changes to this project will be documented in this file.

## Unreleased

### Changed

- `SecureKeyManager` writes version 2 key files (random per-file salt and nonce, Argon2id 64 MiB). Version 1 files
  are still read and are upgraded on first load; the original is kept as `<key file>.v1.bak`. **Builds older than
  this one cannot read a version 2 key file**, so to roll back restore the `.v1.bak` copy;
- Key file writes are atomic and keep the existing file's permissions (new files are created `0600` on Unix),
  follow symlinks to replace the real file, and leave no temp file behind when they fail;
- Key file passwords are hashed as their full UTF-8 encoding on every crypto backend. Files written by the libsodium
  backend with a non-ASCII password (which hashed only the first `password.Length` UTF-8 bytes) still open, and are
  rewritten with the full encoding;
- New nodes (`SecureKeyManager.CreateNew`, and `FromMnemonic`) get **version 3** key files: a standard BIP32 master key
  with the node key derived at `m/1017'/0'/6'/0/0` (`SecureKeyManager.NodeKeyPathString`, NL-159), so the node key is
  no longer the wallet's root key and a mnemonic restores in other BIP32 wallets. Version 1 and 2 files keep the old
  derivation (node key = master key, genesis hash as chain code) and their node id; a version 1 file is still upgraded
  to version 2, never to 3. Builds older than this one cannot read a version 3 key file;
- Key file rewrites (upgrade, index update, backup) never keep group or other permission bits (a key file written by
  an old build with mode 0644 becomes 0600, with a warning on stderr);
- Key file rewrites also keep the file's Unix owner and group where the process may set them (a rewrite by another
  user, e.g. root, hands the file back to its owner), and copy the file's Windows ACL instead of the new file
  inheriting the directory's (NL-224);
- Version 1 key files written on Windows with a non-ASCII password (marshalled in the ANSI code page by the old
  libsodium P/Invoke) open again (NL-212);

## v1.0.0

Major release adding wallet address management, funding transaction building, and comprehensive key management improvements.

### Added

- Added `FundingTransactionBuilder` and `IFundingTransactionBuilder` for constructing and signing funding transactions from UTXO sets;
- Added `BitcoinChainService` and `IBitcoinChainService` for chain-level Bitcoin operations;
- Added `IBitcoinWalletService` with `GetUnusedAddressAsync`;
- Added `IBlockchainMonitor.WatchBitcoinAddress` for watching wallet addresses;
- Added `IBlockchainMonitor.PublishAndWatchTransactionAsync` for broadcasting and tracking transactions;
- Added `IBlockchainMonitor.LastProcessedBlockHeight` property;
- Added `IBlockchainMonitor.OnWalletMovementDetected` event;
- Added `SecureKeyManager.ChannelKeyPath`, `DepositP2TrKeyPath`, `DepositP2WpkhKeyPath` key paths;
- Added `SecureKeyManager.OutputDepositP2TrDescriptor`, `OutputDepositP2WshDescriptor`, `OutputChangeP2TrDescriptor`, `OutputChangeP2WshDescriptor` output descriptors;
- Added `LocalLightningSigner` dependency on `IUtxoMemoryRepository` for UTXO selection;

### Changed

- `BitcoinWalletService` rewritten to use `IBlockchainMonitor` and `ISecureKeyManager` instead of direct RPC; no longer implements `IBitcoinWallet`;
- `SecureKeyManager.KeyPath` renamed to `ChannelKeyPath`; `OutputDescriptor` renamed to `OutputChannelDescriptor`;
- `CommitmentKeyDerivationService.DeriveRemoteCommitmentKeys` signature simplified: removed `localChannelKeyIndex` and `commitmentNumber` parameters;
- `DustService` now references `Domain.Protocol.Interfaces` instead of `Domain.Protocol.Services`;
- Updated `PackageProjectUrl` to `https://docs.nlightn.ing`;
- Updated `MessagePack` to `v3.1.4`, `NBitcoin` to `v9.0.5`, `NBitcoin.Secp256k1` to `v3.2.0`, `NetMQ` to `v4.0.2.2`;

### Removed

- Removed `IBitcoinWallet` interface (replaced by `IBitcoinWalletService`);

### Breaking Changes

- Dropped `net8.0` and `net9.0` targets; the library now requires **.NET 10.0** or later;
- `IBitcoinWallet` removed; use `IBitcoinWalletService` instead;
- `SecureKeyManager.KeyPath` → `ChannelKeyPath`; `OutputDescriptor` → `OutputChannelDescriptor`;
- `CommitmentKeyDerivationService.DeriveRemoteCommitmentKeys` signature changed: `localChannelKeyIndex` and `commitmentNumber` parameters removed;
- `LocalLightningSigner` constructor now requires `IUtxoMemoryRepository`;

## v0.0.4

Bump version to use `NLightning.Infrastructure@v1.0.3`.

## v0.0.3

Bump version to use `NLightning.Infrastructure@v1.0.2`.

## v0.0.2

Bump version to use `NLightning.Infrastructure@v1.0.1`.

## v0.0.1

Initial release