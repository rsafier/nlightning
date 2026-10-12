# Trusted VLS payment approval

Run this helper as the trusted operator, separately from the node. The node gets
only `node-token` and `node.sock`; **never put `approval-token` in the node's
configuration, service environment, or service provider**. For a meaningful
security boundary, the operator and node need distinct OS identities and access
controls; separate sockets under one UID do not isolate a compromised process.

The gateway's approval socket verifies the approval credential. BOLT 11 approval
uses VLS's signed-invoice validation, expiry checks, payment amount, and velocity
policy. Amountless invoices are outside this initial slice. Keysend approval binds
the selected destination public key, payment hash, and amount; VLS does not
authenticate ownership of that destination. Routing fees are governed separately
by the gateway's fee policy and may increase the total spend above the approved
invoice/keysend amount. Independently verify the payment details before running
the helper. Do not approve data supplied by an untrusted
node without checking it against the intended payment.

## Invoice payment

Save the exact independently checked invoice in `/secure/payment.bolt11`. Generate
and save a request UUID, then run:

```bash
python3 tools/vls-approve/approve.py \
  --socket /private/vls-gateway/approval.sock \
  --token-file /private/vls-gateway/approval-token \
  --request-id YOUR_SAVED_UUID \
  invoice --invoice-file /secure/payment.bolt11
```

Only after `Payment admitted by VLS` should you submit that same invoice through
the node's ordinary `payinvoice` command. A transport success containing
`added:false` is a policy refusal; the helper exits unsuccessfully. If the reply
is lost, repeat the same UUID and exact arguments to recover the existing receipt.
Changing the arguments while reusing the UUID is rejected. An uncertain result
does not grant permission to pay.

## Keysend payment

Create an operator-managed preimage file with 32 random raw bytes and owner-only
permissions. Record its SHA256 payment hash, and verify the recipient and amount
independently. The hash is public payment metadata; keep the preimage private
until submitting the payment:

```bash
python3 - <<'PYTHON'
import hashlib, os, secrets
preimage = secrets.token_bytes(32)
fd = os.open('/secure/keysend-preimage.bin', os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
with os.fdopen(fd, 'wb') as output:
    output.write(preimage)
print(hashlib.sha256(preimage).hexdigest())
PYTHON
```

Approve that hash with the independently selected destination and amount:

```bash
python3 tools/vls-approve/approve.py \
  --socket /private/vls-gateway/approval.sock \
  --token-file /private/vls-gateway/approval-token \
  --request-id YOUR_SAVED_UUID \
  keysend --payee COMPRESSED_RECIPIENT_PUBLIC_KEY \
  --payment-hash SHA256_OF_SELECTED_PREIMAGE --amount-msat 10000000
```

After successful approval, submit the matching payment with the preimage file:

```bash
nltg keysend COMPRESSED_RECIPIENT_PUBLIC_KEY 10000 \
  --preimage-file /secure/keysend-preimage.bin
```

The example pays **10,000 satoshis**, matching the approved **10,000,000 millisatoshis**.
The client reads exactly 32 raw bytes and carries the preimage through typed IPC
to `PayKeysendRequest.Preimage`; it never places the preimage itself in process
arguments. Keep the file intact for exact retries. Omitting `--preimage-file`
creates a random preimage in the node, which cannot match a previously approved
hash. The underlying Application API also accepts the same explicit preimage.

The helper uses Python's standard library, requires an operator-owned regular
credential file with owner-only permissions, refuses credential symlinks, and
does not print credentials, invoices, hashes, or gateway-supplied error text.
`VlsPaymentApprovalClient` offers equivalent explicit approval operations for a
trusted .NET operator application; it must remain outside node dependency
injection. Approval does not submit a payment and the node does not call it.

## Withdrawal destination

VLS signs a withdrawal only to its own wallet (change) or to an allowlisted
destination (NL-1335). After checking the destination address independently,
allowlist it, then run the node's ordinary `withdraw`:

```bash
python3 tools/vls-approve/approve.py \
  --socket /private/vls-gateway/approval.sock \
  --token-file /private/vls-gateway/approval-token \
  --request-id YOUR_SAVED_UUID \
  allowlist --address bcrt1q...
nltg withdraw bcrt1q... 250000
```

The allowlist is VLS node state and survives gateway restarts. A withdrawal to any
other address is refused by VLS's on-chain policy and nothing is published.
