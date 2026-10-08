#!/usr/bin/env python3
"""Trusted operator approval client; never run with credentials provisioned to the node."""

import argparse
import json
import os
from pathlib import Path
import socket
import stat
import sys
import uuid

MAX_FRAME = 1024 * 1024


def private_token(path):
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    try:
        metadata = os.fstat(fd)
        if not stat.S_ISREG(metadata.st_mode) or metadata.st_uid != os.getuid():
            raise ValueError("approval token must be an operator-owned regular file")
        if metadata.st_mode & 0o077:
            raise ValueError("approval token must have owner-only permissions")
        token = os.read(fd, 4097)
        if not token or len(token) > 4096:
            raise ValueError("approval token length must be between 1 and 4096 bytes")
        return token.decode("ascii")
    finally:
        os.close(fd)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--socket", required=True, help="gateway approval.sock")
    parser.add_argument("--token-file", required=True, help="operator-only approval credential")
    parser.add_argument("--request-id", required=True, type=uuid.UUID,
                        help="save this UUID; repeat it with the same arguments after a lost reply")
    commands = parser.add_subparsers(dest="action", required=True)
    invoice = commands.add_parser("invoice", help="approve a signed amount-bearing BOLT11 invoice")
    invoice.add_argument("--invoice-file", required=True,
                         help="UTF-8 file containing the exact invoice independently checked by the operator")
    keysend = commands.add_parser("keysend", help="approve an operator-selected keysend payment")
    keysend.add_argument("--payee", required=True)
    keysend.add_argument("--payment-hash", required=True)
    keysend.add_argument("--amount-msat", required=True, type=int)
    args = parser.parse_args()

    if args.action == "invoice":
        command = {"op": "authorize_invoice", "invoice": Path(args.invoice_file).read_text().strip()}
    else:
        if not 0 < args.amount_msat < 2 ** 64:
            raise ValueError("amount must be a positive uint64 in millisatoshis")
        payee = bytes.fromhex(args.payee)
        payment_hash = bytes.fromhex(args.payment_hash)
        if len(payee) != 33 or payee[0] not in (2, 3) or len(payment_hash) != 32:
            raise ValueError("payee must be a compressed public key and payment hash must be 32 bytes")
        command = {"op": "authorize_keysend", "payee": payee.hex(),
                   "hash": payment_hash.hex(), "amount_msat": args.amount_msat}

    request = {"token": private_token(args.token_file), "id": args.request_id.hex, "command": command}
    frame = json.dumps(request, separators=(",", ":")).encode("utf-8") + b"\n"
    if len(frame) > MAX_FRAME:
        raise ValueError("approval request exceeds gateway frame limit")
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
        connection.settimeout(5)
        connection.connect(args.socket)
        connection.sendall(frame)
        response = bytearray()
        while b"\n" not in response:
            chunk = connection.recv(min(65536, MAX_FRAME + 1 - len(response)))
            if not chunk:
                raise ValueError("approval reply was lost; retry the same request ID and arguments")
            response.extend(chunk)
            if len(response) > MAX_FRAME:
                raise ValueError("approval response exceeds gateway frame limit")
    reply = json.loads(response.split(b"\n", 1)[0])
    if reply.get("ok") is not True or reply.get("result", {}).get("added") is not True:
        raise ValueError("payment was not admitted; do not submit it to the node")
    print("Payment admitted by VLS. Submit the matching payment to the node.")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, UnicodeError, KeyError, TypeError):
        # Never echo requests, credentials, invoices, preimages, or arbitrary gateway error text.
        print("Approval failed or its result is uncertain. Do not pay. Retry the saved request ID "
              "with identical arguments if the reply was lost.", file=sys.stderr)
        sys.exit(1)
