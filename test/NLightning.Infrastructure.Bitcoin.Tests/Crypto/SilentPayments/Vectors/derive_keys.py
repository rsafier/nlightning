#!/usr/bin/env python3
"""Independent standard-library BIP39/BIP32 oracle for the public test mnemonic only."""
import hashlib
import hmac
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from reference import G, Scalar, encode_silent_payment_address, generate_label

MNEMONIC = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about"
ORDER = 0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141


def derive(seed, path):
    root = hmac.new(b"Bitcoin seed", seed, hashlib.sha512).digest()
    key, chain = int.from_bytes(root[:32], "big"), root[32:]
    for index in path:
        key_bytes = key.to_bytes(32, "big")
        data = b"\x00" + key_bytes if index >= 0x80000000 else (Scalar(key) * G).to_bytes_compressed()
        child = hmac.new(chain, data + index.to_bytes(4, "big"), hashlib.sha512).digest()
        left = int.from_bytes(child[:32], "big")
        assert left < ORDER and (left + key) % ORDER != 0
        key, chain = (left + key) % ORDER, child[32:]
    return Scalar(key)


def generate():
    seed = hashlib.pbkdf2_hmac("sha512", MNEMONIC.encode(), b"mnemonic", 2048)
    result = {"mnemonic": MNEMONIC, "passphrase": "", "accounts": []}
    for coin, hrp in [(0, "sp"), (1, "tsp")]:
        for account in [0, 1]:
            base = [352 | 0x80000000, coin | 0x80000000, account | 0x80000000]
            scan = derive(seed, base + [1 | 0x80000000, 0])
            spend = derive(seed, base + [0 | 0x80000000, 0])
            result["accounts"].append({"coin_type": coin, "account": account,
                "scan_path": f"m/352'/{coin}'/{account}'/1'/0",
                "spend_path": f"m/352'/{coin}'/{account}'/0'/0",
                "scan_private_key": scan.to_bytes().hex(), "spend_private_key": spend.to_bytes().hex(),
                "scan_public_key": (scan * G).to_bytes_compressed().hex(),
                "spend_public_key": (spend * G).to_bytes_compressed().hex(),
                "address": encode_silent_payment_address(scan * G, spend * G, hrp=hrp)})
    account = result["accounts"][2]
    scan = Scalar(int(account["scan_private_key"], 16))
    spend = Scalar(int(account["spend_private_key"], 16))
    account["scan_shared_generator"] = (scan * G).to_bytes_compressed().hex()
    account["scan_shared_twice_generator"] = ((scan * Scalar(2)) * G).to_bytes_compressed().hex()
    account["labels"] = []
    for label in [0, 1, 0xffffffff]:
        tweak = generate_label(scan, label)
        raw = spend + Scalar(1) + tweak
        point = raw * G
        signing = raw if point.has_even_y() else -raw
        account["labels"].append({"label": label, "tweak": tweak.to_bytes().hex(),
            "point": (tweak * G).to_bytes_compressed().hex(),
            "spend_tweak_one_key": signing.to_bytes().hex(),
            "spend_tweak_one_point": point.to_bytes_compressed().hex()})
    raw = spend + Scalar(1)
    point = raw * G
    account["unlabeled_spend_tweak_one_key"] = (raw if point.has_even_y() else -raw).to_bytes().hex()
    account["unlabeled_spend_tweak_one_point"] = point.to_bytes_compressed().hex()
    return json.dumps(result, indent=2) + "\n"


if __name__ == "__main__":
    generated = generate()
    target = Path(__file__).with_name("derived_keys.json")
    if "--check" in sys.argv:
        assert target.read_text() == generated, "Derivation fixture differs from independent oracle"
        print("All four BIP39/BIP32 derivation fixtures match")
    else:
        target.write_text(generated)
        print(generated, end="")
