# LND gRPC vector generators (NL-1161, NL-1162)

Small Go programs that run the exact libraries LND v0.21.4 links, so the in-tree C# implementations can be
checked byte for byte (`docs/agents/LND_GRPC_PLAN.md` §4):

- `signmessage-vectors/`: `go run .` prints LND `signmessage` signatures (btcec/v2 `ecdsa.SignCompact` over
  SHA256d / SHA256 of `"Lightning Signed Message:" || msg`, z-base-32 by `tv42/zbase32`) for fixed keys.
- `macaroon-vectors/`: `go run .` prints macaroons baked with `gopkg.in/macaroon.v2` and a bakery v3 id
  (`github.com/go-macaroon-bakery/macaroonpb`); `go run . verify <rootkey hex> <macaroon hex> <entity> <action>`
  checks a macaroon with `gopkg.in/macaroon-bakery.v2`'s checker, as LND's `macaroons.Service` does (exit 0
  allowed, 2 denied).

Needs Go 1.22+ and network access for the module download.
