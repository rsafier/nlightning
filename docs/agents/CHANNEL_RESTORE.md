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
   `open_channel2`, liquidity sales) and ours (`openchannel`, LND gRPC `OpenChannel`), until a restore completes or you
   lift the hold. Without it, a peer may open a channel to the new node before the restore, and with a key file older
   than the lost database that channel could take a key index a backed-up channel uses (same funding key, basepoints
   and per-commitment secrets for two channels). A start whose database already has channels ignores the setting, so
   it is safe to leave it in the file; remove it once the restore is done.
4. Start the daemon. `nltg openhold` shows the hold.

## 2. Restore

```bash
nltg verifychanbackup channel.backup     # this node's key and chain, every channel's keys
nltg restorechanbackup channel.backup
```

While it runs no open of any kind gets a channel key index (an inbound open gets an `error` "Not accepting channels
right now, try again later", ours `invalid_operation` with `[NL-1392] ... a channel restore (restorechanbackup) is in
progress`); retry the open after it. Before anything is written, the restore raises the database's channel key index
mark and the key file's last used index past the backup's highest index. When that raise fails (a database or key
file error, a node write fence) the restore stops with `invalid_operation` and **nothing is restored**: fix the cause
and run it again.

Each channel's outcome:

| Outcome | Meaning | Do |
|---|---|---|
| `Restore` | a recovery channel; its peer is asked to force close and our output is swept | wait for the peer's close; `pendingsweeps` shows the sweep |
| `AlreadyExists` | the channel is in the database | nothing (a recovery channel still waiting is checked again) |
| `KeysMismatch` | its key index does not derive the backed-up keys | the backup belongs to another key file: restore with that one |
| `KeyIndexInUse` | another live channel of this database (any state but Closed and Stale), or an open of this node not stored yet, uses the same key index | close the channel the detail names (`closechannel`, or `forceclosechannel` when its peer is away), wait until it is `Closed`, then run `restorechanbackup` again (for an open not stored yet: wait for it to finish, close it if it opened, restart, run the restore again). Two channels must never sign with the same keys |
| `Failed` | the recovery channel could not be stored or registered | see the detail; restart and run the restore again |

Only the conflicting channels are refused: the others share no keys and are restored at once. Running the restore
again is safe; finished channels report `AlreadyExists`.

## 3. Open channels again

A restore that completed lifts the start hold (the log says "Channel opens are allowed again"). Without a backup to
restore, lift it by hand: `nltg openhold --release`. A restore in progress keeps refusing opens until it ends either
way.
