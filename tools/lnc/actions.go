package main

import (
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"sync"
	"time"

	"github.com/rsafier/nlightning/tools/lnc/internal/litrpc"
)

// action is one call an autopilot session made through the bridge, recorded
// like litd's firewall action (firewalldb.Action): what was called, by which
// actor and feature (from the request's meta caveat), with the real request
// parameters, and how it ended.
type action struct {
	Index       uint64 `json:"index"`
	SessionID   string `json:"session_id"`
	GroupID     string `json:"group_id"`
	MacaroonID  string `json:"macaroon_id"`
	ActorName   string `json:"actor_name,omitempty"`
	FeatureName string `json:"feature_name,omitempty"`
	Trigger     string `json:"trigger,omitempty"`
	Intent      string `json:"intent,omitempty"`
	Structured  string `json:"structured_json_data,omitempty"`
	Method      string `json:"rpc_method"`
	ParamsJSON  string `json:"rpc_params_json,omitempty"`
	AttemptedAt int64  `json:"attempted_at_ns"`
	State       int32  `json:"state"`
	ErrorReason string `json:"error_reason,omitempty"`
}

// The bridge keeps at most this many actions; the oldest are dropped first.
// The autopilot rate limits cap a session at about a thousand calls a day.
const maxStoredActions = 100000

// actionLog is the persistent action log behind litrpc.Firewall.ListActions
// and the rate-limit rule (actions.json in the state directory, mode 0600,
// rewritten atomically on every change).
type actionLog struct {
	dir     string
	mu      sync.Mutex
	actions []*action
	next    uint64
	now     func() time.Time
}

func openActionLog(dir string) (*actionLog, error) {
	l := &actionLog{dir: dir, next: 1, now: time.Now}
	data, err := readPrivateFile(l.path())
	if errors.Is(err, os.ErrNotExist) {
		return l, nil
	}
	if err != nil {
		return nil, err
	}
	if err := json.Unmarshal(data, &l.actions); err != nil {
		return nil, fmt.Errorf("action log: %w", err)
	}
	for _, a := range l.actions {
		if a.Index >= l.next {
			l.next = a.Index + 1
		}
		// A call cut off by a bridge stop never got its outcome.
		if a.State == int32(litrpc.ActionState_STATE_PENDING) {
			a.State = int32(litrpc.ActionState_STATE_ERROR)
			a.ErrorReason = "bridge stopped before the call finished"
		}
	}
	return l, nil
}

func (l *actionLog) path() string { return filepath.Join(l.dir, "actions.json") }

func (l *actionLog) saveLocked() error {
	if len(l.actions) > maxStoredActions {
		l.actions = append([]*action(nil), l.actions[len(l.actions)-maxStoredActions:]...)
	}
	data, err := json.Marshal(l.actions)
	if err != nil {
		return err
	}
	return writePrivateFile(l.dir, l.path(), data)
}

// add records a new pending action and returns its index.
func (l *actionLog) add(a action) (uint64, error) {
	index, _, err := l.addCounting(a, time.Time{}, nil)
	return index, err
}

// addCounting records a new pending action and, when isRead is set, also
// returns how many earlier actions of the same group and feature, read-only
// or not like this one, were attempted since the given time (the rate-limit
// rule's window), counted under the same lock.
func (l *actionLog) addCounting(a action, since time.Time, isRead func(string) bool) (uint64, uint32, error) {
	l.mu.Lock()
	defer l.mu.Unlock()
	var prior uint32
	if isRead != nil {
		read := isRead(a.Method)
		for _, p := range l.actions {
			if p.GroupID == a.GroupID && p.FeatureName == a.FeatureName && p.AttemptedAt >= since.UnixNano() && isRead(p.Method) == read {
				prior++
			}
		}
	}
	a.Index = l.next
	a.AttemptedAt = l.now().UnixNano()
	a.State = int32(litrpc.ActionState_STATE_PENDING)
	l.actions = append(l.actions, &a)
	if err := l.saveLocked(); err != nil {
		l.actions = l.actions[:len(l.actions)-1]
		return 0, 0, err
	}
	l.next++
	return a.Index, prior, nil
}

// finish sets the outcome of an action.
func (l *actionLog) finish(index uint64, state litrpc.ActionState, reason string) error {
	l.mu.Lock()
	defer l.mu.Unlock()
	for i := len(l.actions) - 1; i >= 0; i-- {
		if a := l.actions[i]; a.Index == index {
			a.State, a.ErrorReason = int32(state), reason
			return l.saveLocked()
		}
	}
	return nil
}

// list answers litrpc.Firewall.ListActions with litd's semantics
// (lightning-terminal session_rpcserver.go ListActions and
// db/sqlc/actions_custom.go): every set filter must match, results are
// ordered by attempt time and index (descending when reversed), index_offset
// skips that many matches, max_num_actions defaults to 100, and
// last_index_offset is the index of the last returned action.
func (l *actionLog) list(req *litrpc.ListActionsRequest) (*litrpc.ListActionsResponse, error) {
	if req.SessionId != nil && len(req.SessionId) != 4 {
		return nil, errors.New("session ID must be 4 bytes")
	}
	if req.SessionId == nil && req.GroupId != nil && len(req.GroupId) != 4 {
		return nil, errors.New("group ID must be 4 bytes")
	}
	if req.State < 0 || req.State > litrpc.ActionState_STATE_ERROR {
		return nil, fmt.Errorf("unknown action state %d", req.State)
	}
	max := req.MaxNumActions
	if max == 0 {
		max = 100
	}
	l.mu.Lock()
	matched := make([]*action, 0)
	for _, a := range l.actions {
		switch {
		case req.FeatureName != "" && a.FeatureName != req.FeatureName,
			req.ActorName != "" && a.ActorName != req.ActorName,
			req.MethodName != "" && a.Method != req.MethodName,
			req.State != litrpc.ActionState_STATE_UNKNOWN && a.State != int32(req.State),
			req.SessionId != nil && a.SessionID != hex.EncodeToString(req.SessionId),
			req.SessionId == nil && req.GroupId != nil && a.GroupID != hex.EncodeToString(req.GroupId),
			req.EndTimestamp != 0 && a.AttemptedAt > time.Unix(clampUnix(req.EndTimestamp), 0).UnixNano(),
			req.StartTimestamp != 0 && a.AttemptedAt < time.Unix(clampUnix(req.StartTimestamp), 0).UnixNano():
			continue
		}
		c := *a
		matched = append(matched, &c)
	}
	l.mu.Unlock()
	sort.SliceStable(matched, func(i, j int) bool {
		if matched[i].AttemptedAt != matched[j].AttemptedAt {
			return matched[i].AttemptedAt < matched[j].AttemptedAt
		}
		return matched[i].Index < matched[j].Index
	})
	if req.Reversed {
		for i, j := 0, len(matched)-1; i < j; i, j = i+1, j-1 {
			matched[i], matched[j] = matched[j], matched[i]
		}
	}
	resp := &litrpc.ListActionsResponse{}
	if req.CountTotal {
		resp.TotalCount = uint64(len(matched))
	}
	if req.IndexOffset >= uint64(len(matched)) {
		return resp, nil
	}
	page := matched[req.IndexOffset:]
	if uint64(len(page)) > max {
		page = page[:max]
	}
	for _, a := range page {
		sid, _ := hex.DecodeString(a.SessionID)
		mid, _ := hex.DecodeString(a.MacaroonID)
		resp.Actions = append(resp.Actions, &litrpc.Action{
			ActorName: a.ActorName, FeatureName: a.FeatureName, Trigger: a.Trigger, Intent: a.Intent,
			StructuredJsonData: a.Structured, RpcMethod: a.Method, RpcParamsJson: a.ParamsJSON,
			Timestamp: uint64(time.Unix(0, a.AttemptedAt).Unix()), State: litrpc.ActionState(a.State),
			ErrorReason: a.ErrorReason, SessionId: sid, MacaroonIdentifier: mid,
		})
		resp.LastIndexOffset = a.Index
	}
	return resp, nil
}

// clampUnix bounds a request timestamp to the year 9999, so the nanosecond
// conversion cannot overflow.
func clampUnix(v uint64) int64 {
	const max = 253402300799
	if v > max {
		return max
	}
	return int64(v)
}
