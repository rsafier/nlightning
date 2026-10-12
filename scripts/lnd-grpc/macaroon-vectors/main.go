// Generates and checks LND-format macaroons with the libraries LND itself uses (gopkg.in/macaroon.v2,
// gopkg.in/macaroon-bakery.v2): `go run .` prints vectors, `go run . verify <rootkey> <macaroon> <entity> <action>`
// checks a macaroon the way LND's macaroons.Service does (bakery checker with the time-before checker).
package main

import (
	"context"
	"encoding/hex"
	"fmt"
	"os"
	"time"

	"github.com/go-macaroon-bakery/macaroonpb"
	"gopkg.in/macaroon-bakery.v2/bakery"
	"gopkg.in/macaroon-bakery.v2/bakery/checkers"
	macaroon "gopkg.in/macaroon.v2"
)

type fixedStore struct{ key []byte }

func (s fixedStore) Get(_ context.Context, id []byte) ([]byte, error) {
	if string(id) != "0" {
		return nil, bakery.ErrNotFound
	}
	return s.key, nil
}
func (s fixedStore) RootKey(_ context.Context) ([]byte, []byte, error) { return s.key, []byte("0"), nil }

func main() {
	if len(os.Args) > 1 && os.Args[1] == "verify" {
		key, _ := hex.DecodeString(os.Args[2])
		raw, _ := hex.DecodeString(os.Args[3])
		m := &macaroon.Macaroon{}
		if err := m.UnmarshalBinary(raw); err != nil {
			fmt.Println("unmarshal:", err)
			os.Exit(1)
		}
		b := bakery.New(bakery.BakeryParams{Location: "lnd", RootKeyStore: fixedStore{key}})
		_, err := b.Checker.Auth(macaroon.Slice{m}).Allow(context.Background(), bakery.Op{Entity: os.Args[4], Action: os.Args[5]})
		if err != nil {
			fmt.Println("denied:", err)
			os.Exit(2)
		}
		fmt.Println("allowed")
		return
	}
	rootKey, _ := hex.DecodeString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f")
	nonce, _ := hex.DecodeString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf")
	id := &macaroonpb.MacaroonId{
		Nonce:     nonce,
		StorageId: []byte("0"),
		Ops: []*macaroonpb.Op{
			{Entity: "info", Actions: []string{"read"}},
			{Entity: "invoices", Actions: []string{"read", "write"}},
		},
	}
	idb, _ := id.MarshalBinary()
	idBytes := append([]byte{3}, idb...)
	m, _ := macaroon.New(rootKey, idBytes, "lnd", macaroon.V2)
	b, _ := m.MarshalBinary()
	fmt.Println("id", hex.EncodeToString(idBytes))
	fmt.Println("plain", hex.EncodeToString(b))
	m.AddFirstPartyCaveat([]byte(checkers.TimeBeforeCaveat(time.Date(2030, 1, 2, 3, 4, 5, 0, time.UTC)).Condition))
	b, _ = m.MarshalBinary()
	fmt.Println("timebefore", hex.EncodeToString(b))
	m.AddFirstPartyCaveat([]byte("ipaddr 127.0.0.1"))
	b, _ = m.MarshalBinary()
	fmt.Println("ipaddr", hex.EncodeToString(b))
}
