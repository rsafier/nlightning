# BIP 352 vectors and independent oracle

`send_and_receive_test_vectors.json` is an unchanged upstream copy from
[bitcoin/bips commit c2ac36f48f71615984087fd151f410457edfed72](https://github.com/bitcoin/bips/tree/c2ac36f48f71615984087fd151f410457edfed72/bip-0352),
BIP 352 version 1.1.1. SHA-256:
`f5f9ed4afd76a1b76f3c70b1cbe67532f89abbe559f8e02d7fc3d8ecb93af4a1`.
There are 28 sending cases and 29 receiving cases, including the 2324-output
recipient-limit case. The C# tests exercise actual input classification, sender
scalar/public-key sums, shared secrets, the complete allowed output sets,
receiver aggregate/tweak points, private tweaks, output ownership and exact
BIP 340 signatures. Invalid/no-eligible and zero-sum sender inputs are required
to fail; a receiver skips them.

`reference.py`, `bech32m.py`, `bitcoin_utils.py`, `ripemd160.py` and the
`secp256k1lab/` subtree are unchanged upstream files from that same commit.
The reference is a separate, pure-Python implementation: it never calls our C#
implementation. It has no package dependencies and is test tooling only; its
secret operations are unsuitable for production. Run from the repository root:

```sh
PYTHONDONTWRITEBYTECODE=1 python3 test/NLightning.Infrastructure.Bitcoin.Tests/Crypto/SilentPayments/Vectors/reference.py test/NLightning.Infrastructure.Bitcoin.Tests/Crypto/SilentPayments/Vectors/send_and_receive_test_vectors.json
```

`derive_keys.py` is our independent standard-library BIP39/PBKDF2 and BIP32/HMAC
oracle, using only the upstream secp256k1lab point operations. Its public test
mnemonic has no passphrase; `derived_keys.json` includes mainnet/testnet coin
types and accounts 0/1 at `m/352'/coin'/account'/1'/0` (scan) and
`m/352'/coin'/account'/0'/0` (spend), plus labels and raw key-path signing keys.
Every private value is from this public test mnemonic. To check the fixture:

```sh
PYTHONDONTWRITEBYTECODE=1 python3 test/NLightning.Infrastructure.Bitcoin.Tests/Crypto/SilentPayments/Vectors/derive_keys.py --check
```

The upstream BIP and vectors govern behavior. In particular, outpoints use
serialized txid byte order and a little-endian vout, while BIP 352 label and
shared-secret counters use big-endian uint32. A received output is signed with
a raw BIP 340 key, without an additional BIP86/BIP341 output-key tweak.
