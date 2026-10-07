#!/usr/bin/env python3
"""Regtest oracle over the vendored, unmodified BIP 352 reference functions.

The caller supplies chain data and throwaway reference-wallet keys. No NLightning
math, receiver private key, network download, or RPC scan service is involved.
"""
import base64
import json
import os
import sys

sys.path.insert(0, os.environ.get('SP_REFERENCE_PATH', '/oracle/reference'))
from reference import (
    G, GE, Scalar, COutPoint, get_input_hash, decode_silent_payment_address,
    encode_silent_payment_address, create_outputs, scanning,
)

request = json.loads(base64.b64decode(sys.argv[1]))
mode = request['mode']
scan = Scalar.from_bytes_checked(bytes.fromhex(request.get('scan', '00' * 31 + '2b')))
spend = Scalar.from_bytes_checked(bytes.fromhex(request.get('spend', '00' * 31 + '2c')))
if mode == 'address':
    print(json.dumps({'address': encode_silent_payment_address(scan * G, spend * G, hrp='sprt')}))
    sys.exit(0)

outpoints = [COutPoint(bytes.fromhex(item['txid'])[::-1], item['vout']) for item in request['outpoints']]
if mode == 'send':
    keys = [(Scalar.from_bytes_checked(bytes.fromhex(item['secret'])), item.get('xonly', False))
            for item in request['keys']]
    adjusted = [(-key if xonly and not (key * G).has_even_y() else key) for key, xonly in keys]
    total = Scalar.sum(*adjusted)
    input_hash_scalar = Scalar.from_bytes_checked(get_input_hash(outpoints, total * G))
    recipients = []
    shared = {}
    for index, address in enumerate(request['recipients']):
        b_scan, b_spend = decode_silent_payment_address(address, hrp='sprt')
        recipients.append({'address': address, 'scan_pub_key': b_scan.to_bytes_compressed().hex(),
                           'spend_pub_key': b_spend.to_bytes_compressed().hex()})
        shared[index] = (input_hash_scalar * total * b_scan).to_bytes_compressed().hex()
    outputs = create_outputs(keys, outpoints, recipients,
                             expected={'input_private_key_sum': total.to_bytes().hex(), 'shared_secrets': shared}, hrp='sprt')
    print(json.dumps({'outputs': outputs}))
elif mode == 'receive':
    total = GE.sum(*[GE.from_bytes_compressed(bytes.fromhex(key)) for key in request['input_pubkeys']])
    input_hash = get_input_hash(outpoints, total)
    scalar = Scalar.from_bytes_checked(input_hash)
    expected = {'tweak': (scalar * total).to_bytes_compressed().hex(),
                'shared_secret': (scalar * scan * total).to_bytes_compressed().hex()}
    outputs = [bytes.fromhex(output) for output in request['outputs']]
    found = scanning(scan, spend * G, total, input_hash, outputs, expected=expected)
    print(json.dumps({'found': found}))
else:
    raise ValueError('Unknown oracle mode')
