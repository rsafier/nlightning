#!/usr/bin/env bash
# BOLT 7 gossip soak on Mutinynet (docs/agents/BOLT7_GOSSIP_PLAN.md G5-T5, docs/agents/MUTINYNET.md "Gossip soak"):
# runs our daemon with the graph, gossip sync and relay on, keeps it connected to the faucet node and logs a sample
# every SOAK_INTERVAL seconds: graph channels (closed ones too), graph nodes, connected peers, the daemon's RSS and CPU,
# the SQLite database size, the chain tip and bitcoind's RPC call rate. It never opens channels or spends funds.
#
# Usage: scripts/mutinynet/soak-gossip.sh start    stage the build, start the sampler detached (nohup); returns at once
#        scripts/mutinynet/soak-gossip.sh run      the sampler in the foreground (what start runs)
#        scripts/mutinynet/soak-gossip.sh stop     stop the sampler and the daemon it started
#        scripts/mutinynet/soak-gossip.sh status   the last samples
#
# SOAK_DURATION  seconds to sample (default 86400, 0 = until stopped); the daemon the soak started is stopped at the end
# SOAK_INTERVAL  seconds between samples (default 300)
# SOAK_PEER      node to stay connected to (default FAUCET_NODE, the faucet's LND)
# SOAK_RPC_LOG   1 (default) turns on bitcoind's "rpc" debug category at runtime (bitcoin-cli logging, reverted at the
#                end; a bitcoind restart also clears it) to count RPC calls from its console log; 0 skips the rate
# SOAK_BUILD     1 (default) builds the daemon and CLI first (build.sh); 0 stages the existing build
# The binaries and these scripts are copied to ~/.nltg/<network>/soak/bin and the sampler runs from there, so
# rebuilding, editing or removing the checkout does not touch a running soak.
# Output: ~/.nltg/<network>/soak/soak-<UTC date>.log (samples), soak/sampler.out, and the daemon's usual daemon.out.
# Credentials: bitcoind is asked through ~/mutinynet/cli.sh (cookie auth in the container); nothing prints the RPC
# settings of appsettings.json.
set -euo pipefail
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$script_dir/env.sh"

SOAK_DURATION="${SOAK_DURATION:-86400}"
SOAK_INTERVAL="${SOAK_INTERVAL:-300}"
SOAK_PEER="${SOAK_PEER:-$FAUCET_NODE}"
SOAK_RPC_LOG="${SOAK_RPC_LOG:-1}"
SOAK_BUILD="${SOAK_BUILD:-1}"
soak_dir="$NLTG_DIR/soak"
bin_dir="$soak_dir/bin"
runner_pid_file="$soak_dir/sampler.pid"
daemon_pid_file="$soak_dir/daemon.pid"
bitcoind_container="${MUTINYNET_CONTAINER:-mutinynet-bitcoind}"
mkdir -p "$soak_dir"

staged_daemon="$bin_dir/daemon/NLightning.Daemon"
staged_client="$bin_dir/client/NLightning.Client"

log() { echo "[$(date -u +%Y-%m-%dT%H:%M:%SZ)] $*"; }

bitcoin_cli() { "$MUTINYNET_DIR/cli.sh" "$@"; }

cli() { NLTG_CLIENT_BIN="$staged_client" "$script_dir/cli.sh" "$@"; }

daemon_pid() {
    # The staged daemon for this network (the soak's own, or one started by hand from the same staging)
    pgrep -f "$staged_daemon --network $NLTG_NETWORK" | head -n 1 || true
}

stage_build() {
    if [[ "$SOAK_BUILD" == "1" ]]; then
        "$script_dir/build.sh"
    fi
    local daemon_src client_src
    daemon_src="$(dirname "$repo_root/src/NLightning.Daemon/bin/$NLTG_BUILD/$NLTG_FRAMEWORK/NLightning.Daemon")"
    client_src="$(dirname "$repo_root/src/NLightning.Client/bin/$NLTG_BUILD/$NLTG_FRAMEWORK/NLightning.Client")"
    if [[ -n "$(daemon_pid)" ]]; then
        log "a staged daemon is running; keeping the staged binaries"
        return
    fi
    rm -rf "$bin_dir"
    mkdir -p "$bin_dir"
    cp -R "$daemon_src" "$bin_dir/daemon"
    cp -R "$client_src" "$bin_dir/client"
    # The sampler runs from a copy of these scripts too: bash reads a script while it runs it, so editing or removing
    # the checkout must not reach a running soak
    mkdir -p "$bin_dir/scripts"
    cp "$script_dir"/*.sh "$bin_dir/scripts/"
    git -C "$repo_root" rev-parse HEAD > "$bin_dir/commit" 2>/dev/null || true
    log "staged the build of $(cat "$bin_dir/commit" 2>/dev/null || echo unknown) in $bin_dir"
}

start_daemon() {
    local pid
    pid="$(daemon_pid)"
    if [[ -n "$pid" ]]; then
        log "daemon already running (pid $pid)"
        return
    fi
    # Gossip on explicitly (the signet defaults are on too; plan D12), whatever the file says
    NLTG_Gossip__Enabled=true NLTG_Gossip__SyncEnabled=true NLTG_Gossip__RelayEnabled=true \
        NLTG_DAEMON_BIN="$staged_daemon" nohup "$script_dir/start-daemon.sh" > /dev/null 2>&1 &
    for _ in $(seq 1 120); do
        sleep 1
        pid="$(daemon_pid)"
        if [[ -n "$pid" ]] && cli info > /dev/null 2>&1; then
            echo "$pid" > "$daemon_pid_file"
            log "daemon started (pid $pid)"
            return
        fi
    done
    log "daemon did not answer on IPC within 120 s (see $NLTG_DIR/daemon.out)"
    return 1
}

ensure_peer() {
    local node_id="${SOAK_PEER%%@*}"
    if ! cli listpeers 2>/dev/null | grep -A1 "Id:          $node_id" | grep -q "Connected:   Yes"; then
        log "connecting to $SOAK_PEER"
        cli connect "$SOAK_PEER" > /dev/null 2>&1 || log "connect failed"
    fi
}

rpc_log_on() {
    [[ "$SOAK_RPC_LOG" == "1" ]] || return 0
    bitcoin_cli logging '["rpc"]' > /dev/null 2>&1 && log "bitcoind rpc logging on" \
        || log "could not turn on bitcoind rpc logging; the RPC rate is not logged"
}

rpc_log_off() {
    [[ "$SOAK_RPC_LOG" == "1" ]] || return 0
    bitcoin_cli logging '[]' '["rpc"]' > /dev/null 2>&1 || true
}

sample() {
    local log_file="$1" started="$2" since="$3" out_start="$4"
    local now elapsed pid rss cpu graph nodes spent peers db_kb tip rpc rpc_rate btc_stats daemon_log wrn err
    now="$(date +%s)"
    elapsed=$(( now - started ))
    pid="$(daemon_pid)"
    rss="-"
    cpu="-"
    if [[ -n "$pid" ]]; then
        rss="$(ps -o rss= -p "$pid" 2>/dev/null | tr -d ' ' || true)"
        cpu="$(ps -o %cpu= -p "$pid" 2>/dev/null | tr -d ' ' || true)"
    fi
    local channels_out
    channels_out="$(cli listgraphchannels 2>/dev/null || true)"
    graph="$(sed -n 's/^Graph channels: \([0-9]*\)$/\1/p' <<< "$channels_out" | head -n 1)"
    spent="$(grep -c '(closed)$' <<< "$channels_out" || true)"
    nodes="$(cli listnodes 2>/dev/null | sed -n 's/^Graph nodes: \([0-9]*\)$/\1/p' | head -n 1)"
    peers="$(cli listpeers 2>/dev/null | grep -c 'Connected:   Yes' || true)"
    # The database with its write-ahead log
    db_kb="$(cat "$NLTG_DIR/nltg.db" "$NLTG_DIR/nltg.db-wal" 2>/dev/null | wc -c | awk '{ printf "%d", $1 / 1024 }')"
    # Warnings and errors the daemon logged since the soak started
    daemon_log="$(tail -n "+$out_start" "$NLTG_DIR/daemon.out" 2>/dev/null || true)"
    wrn="$(grep -c ' WRN\] ' <<< "$daemon_log" || true)"
    err="$(grep -c -E ' (ERR|FTL)\] ' <<< "$daemon_log" || true)"
    tip="$(bitcoin_cli getblockcount 2>/dev/null || echo -)"
    rpc="-"
    rpc_rate="-"
    if [[ "$SOAK_RPC_LOG" == "1" ]]; then
        rpc="$(docker logs --since "$since" "$bitcoind_container" 2>&1 | grep -c 'ThreadRPCServer method=' || true)"
        rpc_rate="$(awk -v n="$rpc" -v s="$(( now - since ))" 'BEGIN { if (s > 0) printf "%.1f", n * 60 / s; else print "-" }')"
    fi
    btc_stats="$(docker stats --no-stream --format '{{.CPUPerc}}' "$bitcoind_container" 2>/dev/null || echo -)"
    echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) elapsed=${elapsed}s graph_channels=${graph:--} spent=${spent:-0}" \
        "graph_nodes=${nodes:--} peers=${peers:-0} daemon_pid=${pid:--} rss_kb=${rss:--} cpu=${cpu:--}" \
        "db_kb=${db_kb:--} tip=${tip} bitcoind_rpc_calls=${rpc} rpc_per_min=${rpc_rate}" \
        "bitcoind_cpu=${btc_stats} daemon_wrn=${wrn:-0} daemon_err=${err:-0}" >> "$log_file"
}

run() {
    echo $$ > "$runner_pid_file"
    local log_file started last out_start started_daemon=0
    log_file="$soak_dir/soak-$(date -u +%Y%m%d).log"
    out_start=$(( $(wc -l < "$NLTG_DIR/daemon.out" 2>/dev/null || echo 0) + 1 ))
    started="$(date +%s)"
    [[ -f "$staged_daemon" ]] || stage_build
    if [[ -z "$(daemon_pid)" ]]; then
        start_daemon
        started_daemon=1
    fi
    rpc_log_on
    trap 'rpc_log_off; [[ $started_daemon == 1 ]] && stop_daemon; rm -f "$runner_pid_file"' EXIT
    trap 'exit 0' TERM INT
    echo "# soak start $(date -u +%Y-%m-%dT%H:%M:%SZ) commit $(cat "$bin_dir/commit" 2>/dev/null || echo unknown)" \
        "network $NLTG_NETWORK peer ${SOAK_PEER%%@*} interval ${SOAK_INTERVAL}s duration ${SOAK_DURATION}s" \
        >> "$log_file"
    log "sampling to $log_file"
    ensure_peer
    last="$started"
    sleep 5
    while :; do
        if [[ -z "$(daemon_pid)" ]]; then
            echo "# $(date -u +%Y-%m-%dT%H:%M:%SZ) daemon not running; restarting" >> "$log_file"
            start_daemon || true
            started_daemon=1
        fi
        ensure_peer
        local now
        now="$(date +%s)"
        sample "$log_file" "$started" "$last" "$out_start"
        last="$now"
        if [[ "$SOAK_DURATION" != "0" ]] && (( now - started >= SOAK_DURATION )); then
            echo "# soak end $(date -u +%Y-%m-%dT%H:%M:%SZ)" >> "$log_file"
            break
        fi
        sleep "$SOAK_INTERVAL" &
        wait $!
    done
}

stop_daemon() {
    local pid
    pid="$(daemon_pid)"
    [[ -n "$pid" ]] || return 0
    log "stopping daemon (pid $pid)"
    kill -TERM "$pid" 2>/dev/null || true
    for _ in $(seq 1 60); do
        kill -0 "$pid" 2>/dev/null || break
        sleep 1
    done
    rm -f "$daemon_pid_file"
}

case "${1:-}" in
    start)
        if [[ -f "$runner_pid_file" ]] && kill -0 "$(cat "$runner_pid_file")" 2>/dev/null; then
            echo "soak already running (pid $(cat "$runner_pid_file"))" >&2
            exit 1
        fi
        stage_build
        nohup "$bin_dir/scripts/soak-gossip.sh" run >> "$soak_dir/sampler.out" 2>&1 &
        echo "soak started (sampler pid $!); samples in $soak_dir/soak-$(date -u +%Y%m%d).log"
        ;;
    run)
        run
        ;;
    stop)
        if [[ -f "$runner_pid_file" ]]; then
            kill -TERM "$(cat "$runner_pid_file")" 2>/dev/null || true
        fi
        stop_daemon
        ;;
    status)
        latest="$(ls -1t "$soak_dir"/soak-*.log 2>/dev/null | head -n 1 || true)"
        if [[ -n "$latest" ]]; then tail -n "${2:-12}" "$latest"; else echo "no soak log in $soak_dir"; fi
        ;;
    *)
        echo "usage: $0 start | run | stop | status [lines]" >&2
        exit 2
        ;;
esac