// Prints LND signmessage vectors with the code path LND uses (netann.NodeSigner.SignMessageCompact ->
// btcec/v2 ecdsa.SignCompact over chainhash.DoubleHashB, zbase32 of tv42/zbase32): `go run .` prints
// key|message|single_hash|pubkey|signature hex|zbase32 lines (test/NLightning.Infrastructure.Bitcoin.Tests
// Signers/LightningMessageSignatureTests and test/NLightning.LndGrpc.Tests hold the output).
package main

import (
	"encoding/hex"
	"fmt"

	"github.com/btcsuite/btcd/btcec/v2"
	"github.com/btcsuite/btcd/btcec/v2/ecdsa"
	"github.com/btcsuite/btcd/chaincfg/chainhash"
	"github.com/tv42/zbase32"
)

func main() {
	keys := []string{
		"0000000000000000000000000000000000000000000000000000000000000001",
		"1111111111111111111111111111111111111111111111111111111111111111",
		"e126f68f7eafcc8b74f54d269fe206be715000f94dac067d1c04a8ca3b2db734",
	}
	msgs := []string{"", "hi", "is this compatible?", "Lightning Signed Message: nested"}
	for _, k := range keys {
		kb, _ := hex.DecodeString(k)
		priv, pub := btcec.PrivKeyFromBytes(kb)
		for _, m := range msgs {
			full := append([]byte("Lightning Signed Message:"), []byte(m)...)
			for _, single := range []bool{false, true} {
				var digest []byte
				if single {
					d := chainhash.HashB(full)
					digest = d
				} else {
					digest = chainhash.DoubleHashB(full)
				}
				sig := ecdsa.SignCompact(priv, digest, true)
				fmt.Printf("%s|%q|%v|%s|%s|%s\n", k, m, single, hex.EncodeToString(pub.SerializeCompressed()), hex.EncodeToString(sig), zbase32.EncodeToString(sig))
			}
		}
	}
}
