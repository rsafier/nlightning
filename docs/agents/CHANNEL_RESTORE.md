# Restoring channels from a static channel backup

How to get the funds of a node's channels back after its database is lost, with `restorechanbackup` (wave RF1,
NL-426; key index safety NL-1392). The backup holds no private keys: the restore asks each channel's peer to force
close and sweeps our output of its commitment. It is never a way back to an older channel state.

## 1. Set the node up again

1. Install the same build, the **same key file** (or the same seed) and its password. Never start a second node with
   that key while the old one may still run.
2. Write the configuration as before, with a **fresh** database (`Database:RunMigrations=true`).
3. Add `"HoldOpensUntilRestore": true` to the `Node:Backup` section of `appsettings.json` before the first start:

   ```json
   "Node": { "Backup": { "HoldOpensUntilRestore": true } }
   ```

   A start whose database holds no channel row then refuses every channel open, inbound (`open_channel`,
   `open_channel2`, liquidity sales; the peer gets an `error` for that open and stays connected) and ours
   (`openchannel`, LND gRPC `OpenChannel`), until a restore raised the backup's key indexes or you lift the hold. The
   hold is applied again at every start while the database still holds no channel row; a start whose database has
   channels ignores the setting, so it is safe to leave it in the file; remove it once the restore is done.

   Without it the node still protects itself: at every start, before any peer connects, it raises the channel key
   index past every channel of its own `channel.backup` that the database lacks (the file is never replaced while the
   database is empty). The opt-in also covers a backup that is not at `Node:Backup:FilePath` (a copy kept elsewhere).
4. Start the daemon. `nltg openhold` shows the hold.

## 2. Restore

```bash
nltg verifychanbackup channel.backup     # this node's key and chain, every channel's keys
nltg restorechanbackup channel.backup
```

While the restore raises the backup's key indexes and reads the indexes in use (a short moment, before any channel
is stored or any peer dialed), no open of any kind gets a channel key index: an inbound open gets an `error` "Not
accepting channels right now, try again later" (the peer stays connected), ours `invalid_operation` with `[NL-1392] ...
a channel restore (restorechanbackup) is in progress`; retry the open. Before anything is written, the restore raises
the database's channel key index mark and the key file's last used index past the backup's highest index. When that
raise fails (a database or key file error, a node write fence) the restore stops with `invalid_operation` and
**nothing is restored**: fix the cause and run it again. A VLS signer is not raised (its keys come from the peer and
the channel's VLS id, not from our index).

Each channel's outcome:

| Outcome | Meaning | Do |
|---|---|---|
| `Restore` | a recovery channel; its peer is asked to force close and our output is swept | wait for the peer's close; `pendingsweeps` shows the sweep |
| `Restore` with `KEYS SHARED` in the detail | restored, but another live channel (or an open in progress) already uses the same channel key index | see below |
| `AlreadyExists` | the channel is in the database | nothing (a recovery channel still waiting is checked again) |
| `KeysMismatch` | its key index does not derive the backed-up keys | the backup belongs to another key file: restore with that one |
| `Failed` | the recovery channel could not be stored or registered | see the detail; restart and run the restore again |

**`KEYS SHARED`** (exit code 1). Two channels at one key index share their funding key, basepoints and
per-commitment secrets. The recovery channel is restored anyway: it signs nothing and releases no secret, so refusing
it would remove no risk and only delay its funds. The **other** channel (named in the detail, and marked `KEYS
SHARED with ...` in `listchannels` and in a critical log at every start) is key-compromised: its peer, often the
backed-up channel's peer too, may hold our per-commitment secrets of the old channel, so a commitment of ours at a
number it saw revoked there could be swept by it.

- Close that channel **cooperatively** (`closechannel`) as soon as possible.
- **Never force close it** (`forceclosechannel`): our commitment could be taken as revoked.
- If its peer does not cooperate, keep the node running and contact the peer; do not broadcast.

An open in progress at that index (named "handed out to an open not stored yet") is the same case once it completes.
## 3. Open channels again

A restore whose key index raise ran lifts the start hold (the log says "Channel opens are allowed again"); a restore
that had nothing to restore, or ran without a reserver, does not. Without a backup to restore, lift it by hand:
`nltg openhold --release`. A restore in progress keeps refusing opens until its raise is done either way.
