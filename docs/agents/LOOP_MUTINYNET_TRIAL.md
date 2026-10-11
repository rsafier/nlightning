# One-node Mutinynet static Loop In trial (NL-1190 / NL-1196)

Status: prepared, pending node access, server-network confirmation and the owner's explicit approval.
No live FAFO configuration has been changed. Regtest evidence is recorded in LOOP_GRPC_PLAN.md;
a successful regtest swap does not prove a Mutinynet trial.

## Proposed scope

Use one FAFO node; FAFO2 is the proposed canary (`~/.nltg/mutinynet-fafo2`) if the owner selects it.
Keep the listener on `127.0.0.1:10019` with TLS and macaroons. Enable only the existing isolated swap
signer families 21, 99, 42060, 42068 and 42069; node/channel families stay excluded. Both mainnet flags
stay false. Run pinned real loopd with `--network=signet --experimental` on that machine or through
an SSH loopback tunnel. The selected Loop server must explicitly support **Mutinynet's chain**, not
merely another signet. Loop's default signet endpoint is `signet.swap.lightning.today:11010`; do not
fund an address until its compatibility with Mutinynet has been confirmed.

One static address, one confirmed deposit, one static Loop In. Request the server's terms/quote first;
use its minimum accepted amount, capped at 500,000 test satoshis. Cap the quoted swap fee at 5,000 sat,
and record any L402 charge and mining fees separately before funding. A larger minimum or quote
requires a revised approval. Use no automatic swap policy. Keep enough on-chain funds for unilateral
CSV recovery if the cooperative path fails.

## Preparation before approval

1. Obtain the node alias/config directory, access host, current build, node ID, balances, usable incoming
   liquidity, chain height/hash and confirmation that its unpruned Core backend follows Mutinynet.
   Inspect through its existing `~/day0/nodectl` and CLI; do not start a second daemon on the key/database.
2. Build/stage `wip/lnd-p2` beside the current binaries. Prepare an isolated candidate:

   ```bash
   scripts/mutinynet/prepare-loop-trial.py \
     --config-file "$HOME/.nltg/mutinynet-fafo2/appsettings.json" \
     --candidate-dir "$HOME/day0/loop-trial-candidate"
   /path/to/staged/nltg --network mutinynet --config "$HOME/day0/loop-trial-candidate" --check-config
   ```

   The candidate contains the original private configuration and is created mode 0600. Never commit or
   print its full contents. The preparer does not modify the running configuration or enable the listener.
3. Inspect the candidate's LndGrpc block, verify port availability, stage pinned loop/loopd, and confirm
   the intended server endpoint/network. Prepare a stopped-node backup of the database, encrypted key,
   configuration and current binary symlink. Get approval for the selected node, endpoint, amount/fee
   caps, backup/restart, local listener and signer **before applying anything or funding a deposit**.

## Execution after explicit approval

Use the existing node controller to stop the selected node, take the cold backup, atomically install
its reviewed candidate configuration, update its staged binary symlink, and start exactly one daemon.
Verify node identity, channel reestablishment, chain sync and the local gRPC listener. Keep credentials
in private files. Bake a Loop macaroon with only the necessary signer, address, onchain, offchain,
invoices and info RPC permissions; use the admin macaroon only for this provisioning step.

Start loopd in a dedicated private directory, separate from any existing Loop instance. Run it under
supervision with restart on failure: pinned Loop exits when the LND-compatible listener restarts and
its chain streams close. The recovery proof restarts Loop from its retained database after the node
listener is available again; it does not claim that those existing streams survive a process restart.


```bash
loopd --network=signet --experimental --loopdir="$HOME/day0/loop-trial" \
  --lnd.host=127.0.0.1:10019 \
  --lnd.tlspath="$HOME/.nltg/mutinynet-fafo2/lnd-grpc-loop-trial/tls.cert" \
  --lnd.macaroonpath=/private/path/loop.macaroon \
  --server.host=CONFIRMED_MUTINYNET_LOOP_SERVER
```

Use its local TLS/macaroon CLI for `getinfo`, server terms/quotes and `static new`. Record the address,
server key, CSV lifetime, quoted amount/fees and approved funding transaction. Wait for the deposit's
required confirmations in `static listdeposits`; verify the same outpoint in our WalletKit.ListUnspent
and imported transaction history, while ordinary spendable balance excludes it. Run `static in`
explicitly against that deposit. Record the swap hash, terminal SUCCEEDED state, on-chain spend and
lightning balance change. Verify the imported output is spent and ordinary wallet accounting has not
counted it as a spendable wallet coin. Persist a redacted evidence record with block/transaction hashes,
versions, fee totals, state changes and times; omit credentials and signing secrets.

## Failure and rollback

A failed trial must retain the encrypted key, allocation database, import records and Loop database:
these are required for recovery. Do not restore an old allocation database after issuing swap keys.
Do not disable the signer while a funded deposit or swap still needs Loop to recover it. First determine
whether cooperative withdrawal or the confirmed CSV timeout sweep can safely return the deposit to the
wallet; keep the listener available only as needed for that recovery and report the pending outpoint.

Once every deposit/swap is resolved and confirmed, stop the dedicated loopd, stop the selected node,
restore the pre-trial configuration and prior binary symlink (without rolling the live database/key
back), and restart with the node controller. Verify the same node ID, active channels and chain sync;
verify the trial gRPC port is closed. Preserve the evidence and recovery files privately.
