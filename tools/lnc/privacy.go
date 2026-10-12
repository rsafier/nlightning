package main

import (
	"crypto/rand"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"math/big"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"
)

// Privacy flags (lightning-terminal session/privacy_flags.go): bit i set means
// the privacy mapper leaves that kind of data in clear text. Unknown bits are
// refused, as litd refuses them.
const (
	privClearPubkeys       = 0
	privClearAmounts       = 1
	privClearChanIDs       = 2
	privClearTimeStamps    = 3
	privClearChanInitiator = 4
	privClearHTLCs         = 5
	privClearClosingTxIDs  = 6
	privClearNetAddresses  = 7
	privKnownFlags         = uint64(1)<<8 - 1
)

type privacyFlags uint64

func (f privacyFlags) has(flag uint) bool { return uint64(f)&(1<<flag) != 0 }

func checkPrivacyFlags(f uint64) error {
	if f&^privKnownFlags != 0 {
		return fmt.Errorf("unknown privacy flags %d", f&^privKnownFlags)
	}
	return nil
}

// Like litd's privacy mapper: amounts vary by up to 5 %, timestamps by up to
// 10 minutes, both drawn fresh on every response; identifiers get a random
// pseudonym that is stored per session group and reused.
const (
	privAmountVariation = 0.05
	privTimeVariation   = 10 * time.Minute
	pseudoAlphabet      = "abcdef0123456789"
)

// privacyMap is one session group's real<->pseudo pairs. It is persisted in
// the state directory (privacy-<group>.json, mode 0600) so that a linked
// session and a bridge restart see the same pseudonyms.
type privacyMap struct {
	RealToPseudo map[string]string `json:"real_to_pseudo"`
	PseudoToReal map[string]string `json:"pseudo_to_real"`
	dirty        bool
}

var errNoSuchPair = errors.New("no such privacy map pair")

func (m *privacyMap) pseudo(real string) (string, bool) { p, ok := m.RealToPseudo[real]; return p, ok }

func (m *privacyMap) real(pseudo string) (string, error) {
	r, ok := m.PseudoToReal[pseudo]
	if !ok {
		return "", errNoSuchPair
	}
	return r, nil
}

func (m *privacyMap) add(real, pseudo string) error {
	if p, ok := m.RealToPseudo[real]; ok {
		if p == pseudo {
			return nil
		}
		return errors.New("real value already mapped")
	}
	if _, ok := m.PseudoToReal[pseudo]; ok {
		return errors.New("pseudo value already in use")
	}
	m.RealToPseudo[real] = pseudo
	m.PseudoToReal[pseudo] = real
	m.dirty = true
	return nil
}

func newPseudoStr(n int) (string, error) {
	b := make([]byte, n)
	max := big.NewInt(int64(len(pseudoAlphabet)))
	for i := range b {
		idx, err := rand.Int(rand.Reader, max)
		if err != nil {
			return "", err
		}
		b[i] = pseudoAlphabet[idx.Int64()]
	}
	return string(b), nil
}

// hideString maps a string (a node public key) to a pseudonym of its length.
func (m *privacyMap) hideString(real string) (string, error) {
	if p, ok := m.pseudo(real); ok {
		return p, nil
	}
	for {
		p, err := newPseudoStr(len(real))
		if err != nil {
			return "", err
		}
		if _, used := m.PseudoToReal[p]; used {
			continue
		}
		return p, m.add(real, p)
	}
}

func u64ToStr(v uint64) string {
	b := make([]byte, 8)
	binary.BigEndian.PutUint64(b, v)
	return hex.EncodeToString(b)
}

func strToU64(s string) (uint64, error) {
	b, err := hex.DecodeString(s)
	if err != nil || len(b) != 8 {
		return 0, errors.New("bad privacy map value")
	}
	return binary.BigEndian.Uint64(b), nil
}

// hideUint64 maps a channel ID to a random 64-bit pseudonym.
func (m *privacyMap) hideUint64(real uint64) (uint64, error) {
	if p, ok := m.pseudo(u64ToStr(real)); ok {
		return strToU64(p)
	}
	for {
		b := make([]byte, 8)
		if _, err := rand.Read(b); err != nil {
			return 0, err
		}
		p := hex.EncodeToString(b)
		if _, used := m.PseudoToReal[p]; used {
			continue
		}
		if err := m.add(u64ToStr(real), p); err != nil {
			return 0, err
		}
		return binary.BigEndian.Uint64(b), nil
	}
}

func (m *privacyMap) revealUint64(pseudo uint64) (uint64, error) {
	if pseudo == 0 {
		return 0, nil
	}
	r, err := m.real(u64ToStr(pseudo))
	if err != nil {
		return 0, err
	}
	return strToU64(r)
}

func decodeChanPoint(cp string) (string, uint32, error) {
	parts := strings.Split(cp, ":")
	if len(parts) != 2 || len(parts[0]) != 64 {
		return "", 0, errors.New("bad channel point encoding")
	}
	if _, err := hex.DecodeString(parts[0]); err != nil {
		return "", 0, errors.New("bad channel point encoding")
	}
	idx, err := strconv.ParseUint(parts[1], 10, 32)
	if err != nil {
		return "", 0, errors.New("bad channel point encoding")
	}
	return parts[0], uint32(idx), nil
}

// hideChanPoint maps txid:index to a pseudo txid (64 hex characters) and a
// random index.
func (m *privacyMap) hideChanPoint(txid string, index uint32) (string, uint32, error) {
	realCP := fmt.Sprintf("%s:%d", txid, index)
	if p, ok := m.pseudo(realCP); ok {
		return decodeChanPoint(p)
	}
	for {
		t, err := newPseudoStr(64)
		if err != nil {
			return "", 0, err
		}
		b := make([]byte, 4)
		if _, err := rand.Read(b); err != nil {
			return "", 0, err
		}
		p := fmt.Sprintf("%s:%d", t, binary.BigEndian.Uint32(b))
		if _, used := m.PseudoToReal[p]; used {
			continue
		}
		if err := m.add(realCP, p); err != nil {
			return "", 0, err
		}
		return decodeChanPoint(p)
	}
}

func (m *privacyMap) hideChanPointStr(cp string) (string, error) {
	txid, index, err := decodeChanPoint(cp)
	if err != nil {
		return "", err
	}
	t, i, err := m.hideChanPoint(txid, index)
	if err != nil {
		return "", err
	}
	return fmt.Sprintf("%s:%d", t, i), nil
}

func (m *privacyMap) revealChanPoint(txid string, index uint32) (string, uint32, error) {
	r, err := m.real(fmt.Sprintf("%s:%d", txid, index))
	if err != nil {
		return "", 0, err
	}
	return decodeChanPoint(r)
}

// cryptoIntn returns a uniform random number in [0, n).
func cryptoIntn(n int64) (int64, error) {
	if n <= 0 {
		return 0, nil
	}
	v, err := rand.Int(rand.Reader, big.NewInt(n))
	if err != nil {
		return 0, err
	}
	return v.Int64(), nil
}

func randBetween(lo, hi int64) (int64, error) {
	if hi < lo {
		return 0, errors.New("min above max")
	}
	if hi == lo {
		return lo, nil
	}
	add, err := cryptoIntn(hi - lo)
	return lo + add, err
}

// hideAmount varies an amount symmetrically by privAmountVariation.
func hideAmount(amount uint64) (uint64, error) {
	if amount == 0 {
		return 0, nil
	}
	if amount > math64/2 {
		return 0, errors.New("hide amount: amount too large")
	}
	fuzz := uint64(float64(amount) * privAmountVariation)
	v, err := randBetween(int64(amount-fuzz), int64(amount+fuzz))
	return uint64(v), err
}

const math64 = uint64(1)<<63 - 1

func maybeHideAmount(flags privacyFlags, a int64) (int64, error) {
	if flags.has(privClearAmounts) || a <= 0 {
		return a, nil
	}
	v, err := hideAmount(uint64(a))
	return int64(v), err
}

// hideTimestamp varies a timestamp symmetrically by privTimeVariation.
func hideTimestamp(t time.Time) (time.Time, error) {
	if t.IsZero() || t.Add(-privTimeVariation).Unix() < 0 {
		return t, nil
	}
	ns, err := randBetween(t.Add(-privTimeVariation).UnixNano(), t.Add(privTimeVariation).UnixNano())
	return time.Unix(0, ns), err
}

func hideBool() (bool, error) {
	v, err := cryptoIntn(2)
	return v == 1, err
}

// privacyStore holds every group's privacy map, loaded lazily and written
// atomically when changed. Callers hold its mutex across a whole request or
// response mapping (do), so a mapping and its persistence happen together.
type privacyStore struct {
	dir  string
	mu   sync.Mutex
	maps map[string]*privacyMap
}

func newPrivacyStore(dir string) *privacyStore {
	return &privacyStore{dir: dir, maps: map[string]*privacyMap{}}
}

func (s *privacyStore) path(group string) (string, error) {
	if b, err := hex.DecodeString(group); err != nil || len(b) != 4 {
		return "", errors.New("invalid group ID")
	}
	return filepath.Join(s.dir, "privacy-"+group+".json"), nil
}

func (s *privacyStore) load(group string) (*privacyMap, error) {
	if m, ok := s.maps[group]; ok {
		return m, nil
	}
	p, err := s.path(group)
	if err != nil {
		return nil, err
	}
	m := &privacyMap{RealToPseudo: map[string]string{}, PseudoToReal: map[string]string{}}
	data, err := readPrivateFile(p)
	if err != nil && !errors.Is(err, os.ErrNotExist) {
		return nil, err
	}
	if err == nil {
		if err := json.Unmarshal(data, m); err != nil {
			return nil, fmt.Errorf("privacy map %s: %w", group, err)
		}
		if m.RealToPseudo == nil || m.PseudoToReal == nil || len(m.RealToPseudo) != len(m.PseudoToReal) {
			return nil, fmt.Errorf("privacy map %s is inconsistent", group)
		}
	}
	s.maps[group] = m
	return m, nil
}

// do runs fn on the group's map and saves the map if fn added pairs. A failed
// fn leaves no new pair behind in memory or on disk.
func (s *privacyStore) do(group string, fn func(*privacyMap) error) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	m, err := s.load(group)
	if err != nil {
		return err
	}
	work := &privacyMap{RealToPseudo: make(map[string]string, len(m.RealToPseudo)), PseudoToReal: make(map[string]string, len(m.PseudoToReal))}
	for k, v := range m.RealToPseudo {
		work.RealToPseudo[k] = v
		work.PseudoToReal[v] = k
	}
	if err := fn(work); err != nil {
		return err
	}
	if !work.dirty {
		return nil
	}
	p, err := s.path(group)
	if err != nil {
		return err
	}
	data, err := json.Marshal(work)
	if err != nil {
		return err
	}
	if err := writePrivateFile(s.dir, p, data); err != nil {
		return err
	}
	work.dirty = false
	s.maps[group] = work
	return nil
}
