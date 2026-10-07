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
	AllowedMethods []string  `json:"allowed_methods"`
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
	f, e := os.CreateTemp(s.Dir, ".session-")
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
	if e = os.Rename(f.Name(), p); e != nil {
		return e
	}
	d, e := os.Open(s.Dir)
	if e != nil {
		return e
	}
	defer d.Close()
	return d.Sync()
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
		if !strings.HasSuffix(entry.Name(), ".json") {
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
func (s *Store) bindRemote(id string, key *btcec.PublicKey) error {
	encoded := hex.EncodeToString(key.SerializeCompressed())
	return s.update(id, func(session *Session) error {
		if session.Revoked || !time.Now().Before(session.ExpiresAt) {
			return errors.New("session revoked or expired")
		}
		if session.RemoteKey != "" && session.RemoteKey != encoded {
			return fmt.Errorf("session already paired with another identity")
		}
		session.RemoteKey = encoded
		session.Entropy = ""
		return nil
	})
}
