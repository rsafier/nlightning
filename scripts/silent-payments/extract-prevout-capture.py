#!/usr/bin/env python3
"""Extract only real Core CAPTURE lines from a silent payments cluster runner log."""
import argparse
import base64
import gzip
import hashlib
import json
import re
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('log', type=Path)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
raw = gzip.decompress(args.log.read_bytes()) if args.log.suffix == '.gz' else args.log.read_bytes()
text = raw.decode('utf-8')
values = {}
for kind in ('block', 'getblock3', 'rest'):
    matches = re.findall(r'CAPTURE core31-' + kind + r' ([A-Za-z0-9+/=]+)', text)
    if not matches:
        raise SystemExit('Missing real Core capture: ' + kind)
    values[kind] = matches[0]
verbose = base64.b64decode(values['getblock3'], validate=True)
rest = base64.b64decode(values['rest'], validate=True)
block = bytes.fromhex(values['block'])
fixture = {
    'source': 'SilentPaymentPrevoutClusterTests; real Bitcoin Core 31.1 regtest, getblock 3 and REST spenttxouts',
    'core_image': 'bitcoin/bitcoin:31.1@sha256:da25cedc66b1daefff9f412ee196c901a899c3fa68a33b20849c3e08b5c40d63',
    'block_hex': block.hex(),
    'getblock3': json.loads(verbose),
    'rest_hex': rest.hex(),
    'sha256': {
        'block': hashlib.sha256(block).hexdigest(),
        'getblock3': hashlib.sha256(verbose).hexdigest(),
        'rest': hashlib.sha256(rest).hexdigest(),
    },
}
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(fixture, indent=2) + '\n')
print(f'Wrote real Core prevout fixture: {args.output}')
