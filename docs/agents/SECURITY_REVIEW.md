# Security review: key material and local attack surface

Wave rf1, lane r3-security-review (2026-09-26), on `wip/fafo` at `8b974626`. Scope: the node key file and
`SecureKeyManager`, the daemon's IPC pipe and cookie, password handling, secret files written by the daemon, and
secrets in logs. The peer-to-peer protocol (BOLT 1-11), channel code, payments and persistence migrations are out of
scope; seams into them are listed under "What remains".

Finding IDs are `SR-##` (this document). Ledger IDs (`NL-###`) are given where the issue already had one; the
integrator assigns ledger IDs to the new ones.

## Threat model

| Actor | Can | Wants | Main defences |
| --- | --- | --- | --- |
| Other local user (multi-user host, shared CI box) | Read world/group-readable files, connect to world-accessible sockets, list processes (`ps`) | Node key, wallet, RPC password, preimages, control of the node over IPC | Owner-only config dir (0700), files (0600) and socket, cookie auth, no secrets on the command line |
| Offline attacker with a copy of the config dir (stolen disk, backup, support bundle) | Unlimited offline guessing against the key file | The master key | Argon2id (64 MiB, 3 passes), random per-file salt and nonce, XChaCha20-Poly1305 |
| Log reader (log shipping, bug reports) | Read logs | Keys, preimages, cookie, passwords | Nothing secret is logged (see SR-16) |
| Same-user malware / root | Everything the node can do | - | Out of scope: it can read process memory and the unlocked key |
| Remote peer | Send Lightning messages | - | Out of scope for this lane |

Assets: the master key (BIP32 root: every wallet and channel key), the node key (identity, BOLT 8, onion ECDH, gossip
and invoice signatures), channel key indexes (reuse = two channels share keys), the key-file password, the IPC cookie
(full node control), the bitcoind RPC password, the database (preimages, channel state, per-commitment secrets).

## Findings

Severity is for this node's goal (real funds on mainnet). "Fixed" SHAs are this lane's commits on
`wip/fafo-rf1-r3-security-review-s1` (the integrator's merge may change them).

| ID | Sev. | Finding | Status |
| --- | --- | --- | --- |
| SR-01 | Medium | `SecureKeyManager`'s constructor zeroed the caller's key array through `Marshal.UnsafeAddrOfPinnedArrayElement` on an **unpinned** array: if the GC moved it in between, it wrote zeros into unrelated heap memory and left the key in place | fixed (974d046): `CryptographicOperations.ZeroMemory` on the arrays |
| SR-02 | Medium | Key-file rewrites (v1 upgrade, index update) and the `.v1.bak` copied the existing file's mode. Pre-v2 builds wrote the file with the umask's mode (typically 0644), so an upgraded key file stayed world-readable | fixed (974d046): owner bits kept, group/other bits always dropped, warning on stderr |
| SR-03 | Medium | `~/.nltg/<network>` was created 0755 and `appsettings.json` 0644: the bitcoind RPC password was world-readable, and so were the database (default `nltg.db` there) and the logs | fixed (7794595): new directories (with `~/.nltg`) 0700, the template 0600; a warning at every start when the config directory or file has group/other bits (never chmods an existing path) |
| SR-04 | Medium | `GetNextChannelKey` persisted the new channel key index fire-and-forget: a crash before the write, or two concurrent opens whose writes landed out of order, left a lower index on file, and after a restart a new channel got the keys of an existing one | fixed (9809e4a): written atomically and flushed under the index lock before the key is returned, never lowered; a failed write throws |
| SR-05 | Low | IPC: the Unix socket had the umask's mode and no `CurrentUserOnly`; a client that sent nothing held one of the 64 pipe instances forever; exception messages went to unauthenticated clients; the pre-auth envelope was parsed with MessagePack's `Standard` (trusted) security | fixed (058f297): `PipeOptions.CurrentUserOnly` on both ends plus chmod 0600 of the socket, 30 s request read timeout, generic error before authentication, `MessagePackSecurity.UntrustedData`; an empty cookie never authenticates |
| SR-06 | Low | `--password-file` was read whatever its permissions | fixed (7794595): warning when the file has group/other bits |
| SR-07 | Medium | NL-212: a v1 key file written on Windows with a non-ASCII password (the old libsodium P/Invoke marshalled it as LPStr in the ANSI code page) no longer opened | fixed (974d046): v1-only retry with `Marshal.StringToHGlobalAnsi` (the exact LPStr marshalling) cut to `password.Length`; known-answer test with a code-page-1252-style encoding |
| SR-08 | Medium | NL-159: the node key **is** the master private key, whose chain code is replaced by the genesis hash; a mnemonic's real chain code was dropped (`FromMnemonic`), so seeds did not restore in BIP32 wallets, and any leak of the node key (used online for every BOLT 8 handshake and onion peel) is a leak of every wallet and channel key | fixed for new nodes (974d046): see "Key derivation" below; existing key files keep their derivation and node id by design |
| SR-09 | Low | Plaintext key copies in managed memory: NBitcoin `Key`/`ExtKey` objects built for every derivation, the xprv string in `SaveToFile`/`FromFilePath`, the password string, and `GetNodeKeyPair()` copies handed to callers | partial (974d046): the temporary arrays in `GetMasterKey` and the constructor are zeroed and the node key has its own locked buffer; strings and NBitcoin objects cannot be wiped |
| SR-10 | Info | Key-file crypto: Argon2id m = 64 MiB, t = 3, p = 1, 16-byte random salt and 24-byte random XChaCha20 nonce per save (a new salt and nonce on every write), parameters bounded on load (`MaxMemLimit`/`MaxOpsLimit`, so a crafted file cannot exhaust memory); v2/v3 files are tried with the full UTF-8 password only (the legacy retries now run for v1 files only, so a wrong password costs one Argon2id run) | ok |
| SR-11 | Low | NL-148 remainder: `--daemon` hands the password to the child in `NLTG_PASSWORD`. The child clears it with `Environment.SetEnvironmentVariable`, but on Linux `/proc/<pid>/environ` shows the initial environment block for the process's life (readable by the same user and root only) | open: hand it over a pipe (stdin of the child) instead |
| SR-12 | Info | `Database:EnableSensitiveQueryLogging=true` (opt-in) makes EF log parameter values: preimages, per-commitment secrets | open, persistence seam: warn at start when it is set |
| SR-13 | Low | The output descriptors built from the master xpub (`tr([fp/86'/0'/0']<master xpub>/0/*)`) name a key origin the xpub is not at, so they do not describe our addresses if exported; they are stored in plaintext in the key file | open (functional; the master xpub is not secret-critical because every path from it is hardened) |
| SR-14 | Low | NL-224: atomic key-file writes create the temp file as the running user (owner/group change if another user, e.g. root, rewrites it) and do not copy a Windows ACL (the new file inherits the directory's) | open (NL-224) |
| SR-15 | Info | Cookie: 32 random bytes (hex), written 0600 with `CreateNew` after deleting the old one (no symlink follow), rotated on every start, deleted on stop, re-read per request, compared with `CryptographicOperations.FixedTimeEquals` (only the non-secret length returns early) | ok |
| SR-16 | Info | Logs: no log call passes a key, preimage, secret, password, cookie or connection string as an argument (reviewed every `Log*`/Serilog call whose arguments mention them); messages only say that a preimage or secret exists | ok |
| SR-17 | Low | `PeerServiceFactory` takes a copy of the node private key per connection (`GetNodeKeyPair()`) for the BOLT 8 handshake and never wipes it; the handshake should do its ECDH through `ISecureKeyManager` like the onion peel | open, transport seam |
| SR-18 | Low | The `.v1.bak` kept after a v1 upgrade (NL-211, for downgrades) is the same key under the weak v1 KDF (fixed salt, 64 KiB): whoever gets the directory attacks that copy, not the v2 file | open: advise deleting it once the upgrade is trusted (stderr notice or a CLI command) |

## Key derivation (NL-159)

| Key file version | Written by | Encryption | Master key | Node key |
| --- | --- | --- | --- | --- |
| 1 | builds before NL-158 | Argon2id 64 KiB, fixed salt, zero nonce | private key + genesis hash as chain code | the master private key |
| 2 | v1 upgrade; legacy `new SecureKeyManager(privateKey, ...)` | Argon2id 64 MiB, random salt and nonce | same as v1 | same as v1 |
| 3 | `SecureKeyManager.CreateNew` (daemon, new nodes), `FromMnemonic` | same as v2 | standard BIP32 master (from 32 random bytes of seed, or BIP39) | `m/1017'/0'/6'/0/0` (`NodeKeyPathString`, stored as `nodeKeyPath` and checked on load) |

The file version decides the node id, so a file never moves between derivations: a v1 file is upgraded to v2, never
to v3, and a v2 file is never rewritten as v3 (tests: `Given_Version1KeyFile_When_Upgraded_Then_KeepsTheNodeIdAndTheLegacyDerivation`,
`Given_Version3KeyFile_When_FromFilePath_Then_KeepsTheNodeIdAndTheWalletKeys`). The node key path follows LND's layout
(BIP43 purpose 1017, key family 6), but the seed is not an aezeed, so an LND wallet does not restore it. The wallet and
channel paths are unchanged (`m/84'/0'/0'`, `m/86'/0'/0'`, `m/6425'/0'/0'/0`), so `FromMnemonic` with the BIP84 test
mnemonic gives the BIP84 vector's first address (test `Given_TheBip84TestMnemonic_When_FromMnemonic_Then_DerivesTheStandardBip32Keys`).
Builds older than this one cannot read a v3 file. Moving an existing node to v3 would change its node id and every
wallet key: that needs a new node (close channels, sweep, start fresh), not a file migration.

## Tests added

- `Infrastructure.Bitcoin.Tests/Managers/SecureKeyManagerTests`: input array zeroed; legacy node key = master key;
  v1 upgrade keeps node id, channel keys and v2; v3 creation (version, path, node key derived, differs from master),
  v3 reload (node id, wallet key, descriptor, index); wrong `nodeKeyPath` refused; BIP84 mnemonic vector; NL-212 ANSI
  known-answer (opens and upgrades with the encoder, refused without); `GetSystemAnsiPasswordBytes`; world-readable
  v1 file and its backup end 0600; group bit dropped on an index update; index on file before `GetNextChannelKey`
  returns; 16 concurrent opens give unique indexes and the highest on file; the stored index is never lowered.
- `Daemon.Tests/Services/Ipc/CookieFileAuthenticatorTests`: the cookie (with a trailing newline) is accepted;
  null, empty, last character changed, prefix and longer tokens are refused; missing and empty cookie files refuse.
- `Daemon.Tests/Services/Ipc/NamedPipeIpcServiceTests`: a silent client is disconnected after the read timeout (with a
  `CurrentUserOnly` client, as the CLI connects); an unreadable request's error does not carry the exception message;
  the Unix socket has no group/other bits; the framing round-trips under `UntrustedData`.
- `Daemon.Tests/Utilities/FilePermissionUtilsTests`, `PasswordUtilsTests`, `DaemonArgsTests`: 0700 directories
  (parents too), 0600 new files, `CreateNew` never overwrites, warnings only for group/other bits, password-file
  warning, and a fresh `~/.nltg/<network>` is 0700 with a 0600 `appsettings.json`.

## What remains

1. SR-11: pass the password to the daemon child over its stdin (`--password-stdin`) instead of the environment.
2. SR-17: BOLT 8 handshake ECDH through `ISecureKeyManager` (transport lane), then drop `GetNodeKeyPair()`'s private
   key from every caller except the signer.
3. SR-18: a way to delete the weak `.v1.bak` (and say so in the upgrade notice).
4. SR-12: warn at startup when `Database:EnableSensitiveQueryLogging` is on (persistence lane).
5. SR-13: fix the descriptors (derive the account xpub for each path) before anything exports them.
6. SR-14 / NL-224: Windows ACL on rewrites; on Unix, keep the file's owner when root rewrites it.
7. Not reviewed here: Windows named-pipe ACL behaviour end to end (only `CurrentUserOnly` is set), the database file's
   own mode when its path is outside the config directory (`Database:ConnectionString` can point anywhere), and
   Serilog sinks configured by the operator outside the config directory.