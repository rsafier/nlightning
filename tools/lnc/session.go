package main

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/btcsuite/btcd/btcec/v2"
	"github.com/gofrs/flock"
	"github.com/lightninglabs/lightning-node-connect/mailbox"
)

type Session struct {
	ID             string    `json:"id"`
	Name           string    `json:"name"`
	PrivateKey     string    `json:"private_key"`
	Entropy        string    `json:"entropy"`
	RemoteKey      string    `json:"remote_key,omitempty"`
	Macaroon       string    `json:"macaroon"`
	RootKeyID      uint64    `json:"root_key_id"`
	ExpiresAt      time.Time `json:"expires_at"`
	CreatedAt      time.Time `json:"created_at"`
	Revoked        bool      `json:"revoked"`
	RootKeyDeleted bool      `json:"root_key_deleted"`
	// Confirmed is set by the first RPC that presents the session credential
	// over a transport bound to RemoteKey. Until then the pairing phrase stays
	// valid, so a client whose handshake completed here but failed on its own
	// side (and so never kept its keys) can pair again; confirming erases it.
	Confirmed      bool     `json:"confirmed,omitempty"`
	AllowedMethods []string `json:"allowed_methods"`
	// Type is empty for a session paired by a person's LNC client and
	// sessionTypeAutopilot for one created by AddAutopilotSession, which the
	// autopilot server connects to (autopilot.go).
	Type string `json:"type,omitempty"`
	// LitID is the 4-byte litrpc session ID (hex) and GroupID the LitID of the
	// first session of its linked group; set on autopilot sessions.
	LitID   string `json:"lit_id,omitempty"`
	GroupID string `json:"group_id,omitempty"`
	// ClientMacaroon, when set, is the credential sent to the client in the
	// handshake instead of Macaroon: Macaroon plus litd's firewall caveats.
	// The client must present it (with at most an added meta caveat); the
	// bridge still forwards with Macaroon only.
	ClientMacaroon string         `json:"client_macaroon,omitempty"`
	RevokedAt      time.Time      `json:"revoked_at,omitempty"`
	Autopilot      *autopilotInfo `json:"autopilot,omitempty"`
}

const sessionTypeAutopilot = "autopilot"

func (s *Session) isAutopilot() bool { return s.Type == sessionTypeAutopilot }

// clientCredential is the macaroon the client receives and must present.
func (s *Session) clientCredential() string {
	if s.ClientMacaroon != "" {
		return s.ClientMacaroon
	}
	return s.Macaroon
}

type Store struct{ Dir string }

func openStore(dir string) (*Store, error) {
	if err := os.MkdirAll(dir, 0700); err != nil {
		return nil, err
	}
	info, err := os.Lstat(dir)
	if err != nil {
		return nil, err
	}
	if !info.IsDir() || info.Mode().Perm()&0077 != 0 {
		return nil, errors.New("session directory must be a real directory with mode 0700")
	}
	return &Store{Dir: dir}, nil
}
func validID(id string) bool {
	b, e := hex.DecodeString(id)
	return e == nil && len(b) == 16 && strings.ToLower(id) == id
}
func (s *Store) path(id string) (string, error) {
	if !validID(id) {
		return "", errors.New("invalid session ID")
	}
	return filepath.Join(s.Dir, id+".json"), nil
}
func (s *Store) load(id string) (*Session, error) {
	p, e := s.path(id)
	if e != nil {
		return nil, e
	}
	info, e := os.Lstat(p)
	if e != nil {
		return nil, e
	}
	if !info.Mode().IsRegular() || info.Mode().Perm()&0077 != 0 {
		return nil, errors.New("session file must be regular with mode 0600")
	}
	data, e := os.ReadFile(p)
	if e != nil {
		return nil, e
	}
	var session Session
	if e = json.Unmarshal(data, &session); e != nil {
		return nil, e
	}
	if session.ID != id {
		return nil, errors.New("session ID mismatch")
	}
	return &session, nil
}
func (s *Store) save(session *Session) error {
	p, e := s.path(session.ID)
	if e != nil {
		return e
	}
	data, e := json.MarshalIndent(session, "", "  ")
	if e != nil {
		return e
	}
	return writePrivateFile(s.Dir, p, data)
}

// writePrivateFile replaces path atomically with data, mode 0600, and syncs
// the file and its directory.
func writePrivateFile(dir, path string, data []byte) error {
	f, e := os.CreateTemp(dir, ".session-")
	if e != nil {
		return e
	}
	defer os.Remove(f.Name())
	if e = f.Chmod(0600); e == nil {
		_, e = f.Write(data)
	}
	if e == nil {
		e = f.Sync()
	}
	ce := f.Close()
	if e == nil {
		e = ce
	}
	if e != nil {
		return e
	}
	if e = os.Rename(f.Name(), path); e != nil {
		return e
	}
	d, e := os.Open(dir)
	if e != nil {
		return e
	}
	defer d.Close()
	return d.Sync()
}

// readPrivateFile reads a regular file that only its owner may access.
func readPrivateFile(path string) ([]byte, error) {
	info, e := os.Lstat(path)
	if e != nil {
		return nil, e
	}
	if !info.Mode().IsRegular() || info.Mode().Perm()&0077 != 0 {
		return nil, fmt.Errorf("%s must be a regular file with mode 0600", filepath.Base(path))
	}
	return os.ReadFile(path)
}
func (s *Store) update(id string, fn func(*Session) error) error {
	lock := flock.New(filepath.Join(s.Dir, "state.lock"))
	if e := lock.Lock(); e != nil {
		return e
	}
	defer lock.Unlock()
	session, e := s.load(id)
	if e != nil {
		return e
	}
	if e = fn(session); e != nil {
		return e
	}
	return s.save(session)
}
func (s *Store) list() ([]*Session, error) {
	entries, e := os.ReadDir(s.Dir)
	if e != nil {
		return nil, e
	}
	sessions := []*Session{}
	for _, entry := range entries {
		// The directory also holds the autopilot files (autopilot.json,
		// actions.json, privacy-*.json); session files are named by their ID.
		if !strings.HasSuffix(entry.Name(), ".json") || !validID(strings.TrimSuffix(entry.Name(), ".json")) {
			continue
		}
		session, e := s.load(strings.TrimSuffix(entry.Name(), ".json"))
		if e != nil {
			return nil, e
		}
		sessions = append(sessions, session)
	}
	return sessions, nil
}
func newSession(name string, expiry time.Time, methods []string) (*Session, string, error) {
	id := make([]byte, 16)
	if _, e := rand.Read(id); e != nil {
		return nil, "", e
	}
	key, e := btcec.NewPrivateKey()
	if e != nil {
		return nil, "", e
	}
	words, entropy, e := mailbox.NewPassphraseEntropy()
	if e != nil {
		return nil, "", e
	}
	// Random root IDs are independent of the default root (0); reserve 63 bits for implementations using signed storage.
	rootBytes := make([]byte, 8)
	if _, e = rand.Read(rootBytes); e != nil {
		return nil, "", e
	}
	var root uint64
	for _, b := range rootBytes {
		root = root<<8 | uint64(b)
	}
	root &= (1 << 63) - 1
	if root == 0 {
		root = 1
	}
	return &Session{ID: hex.EncodeToString(id), Name: name, PrivateKey: hex.EncodeToString(key.Serialize()), Entropy: hex.EncodeToString(entropy[:]), RootKeyID: root, CreatedAt: time.Now().UTC(), ExpiresAt: expiry.UTC(), AllowedMethods: methods}, strings.Join(words[:], " "), nil
}

// bindRemote records the client identity a handshake authenticated. The same
// identity is always accepted. Another identity replaces it only while the
// binding is unconfirmed and the pairing entropy is still stored, i.e. when the
// phrase was used again before any authenticated RPC proved the first client
// completed its side of the handshake.
func (s *Store) bindRemote(id string, key *btcec.PublicKey) error {
	encoded := hex.EncodeToString(key.SerializeCompressed())
	return s.update(id, func(session *Session) error {
		if session.Revoked || !time.Now().Before(session.ExpiresAt) {
			return errors.New("session revoked or expired")
		}
		if session.RemoteKey == encoded {
			return nil
		}
		if session.RemoteKey != "" && !session.canRepair() {
			return fmt.Errorf("session already paired with another identity")
		}
		if session.Entropy == "" {
			return errors.New("session has no pairing phrase")
		}
		session.RemoteKey = encoded
		session.Confirmed = false
		return nil
	})
}

// canRepair reports whether the pairing phrase may still bind a new identity.
// Sessions paired before confirmation existed have no entropy left and are
// treated as confirmed.
func (s *Session) canRepair() bool { return !s.Confirmed && s.Entropy != "" }

// confirm marks the binding to key as proven by an authenticated RPC and erases
// the pairing entropy, ending re-pairing with the phrase. A key that is no
// longer the bound one is ignored.
func (s *Store) confirm(id, key string) error {
	return s.update(id, func(session *Session) error {
		if session.RemoteKey == "" || session.RemoteKey != key {
			return errStaleConfirmation
		}
		session.Confirmed = true
		session.Entropy = ""
		return nil
	})
}

var errStaleConfirmation = errors.New("confirmation for an identity that is not bound")
