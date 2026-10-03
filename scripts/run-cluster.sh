#!/usr/bin/env bash
# The test runner of the Kubernetes harness (plan docs/agents/TEST_HARNESS_PLAN.md §5 step 5, R4, R13, R14) and the
# primary way to run the Docker-class suites (LND, on-chain, gossip, ABCD, CLN, Eclair, LDK, Postgres, partitions) on a
# cluster (OrbStack's locally). Every test process gets its own NLTG_TEST_RUN_ID, so its own namespaces
# (nltg-spike-<run>[-<n>]); it never starts Docker containers and needs no Docker lock. The Docker runners
# (run-onchain.sh, run-gossip.sh, run-abcd.sh, run-interop.sh) stay as the fallback, and for Tor.
#
# Two modes, both building once (the test project and the nltg-cluster CLI), never per run:
#   --matrix [S,...]  runs the suites (default: every suite, see `nltg-cluster matrix list`) with at most -j suites in
#                     flight and at most --max-namespaces run namespaces held by them at once: an admission queue in
#                     plan order (the longest suites first) starts a suite as soon as its namespace count fits. A failed
#                     class is rerun alone once (the flake rule; at most --rerun-max classes per suite, never after a
#                     hang timeout, a crash or a fixture error) and a green rerun marks the suite rerun-green. A slot
#                     is freed once the suite's namespaces are gone, terminating ones included, and a suite run with
#                     -parallel none waits for each collection's namespace to go (NLTG_WAIT_NAMESPACE_DELETION). Prints
#                     a summary table (suite, result, tests, passed/failed/skipped/not run, rerun, start, wall, fixture
#                     ready, namespaces created/planned at once, dumps, first error), the log and diagnostics folders
#                     of every failed suite and the batch's sampled namespace peak; exits 1 on a real failure.
#                     Docker-only suites (tor) and suites not ported yet are listed as skipped, with the reason.
#   default (-n N)    runs one selection (a --suite, or --class/--method/--trait tests) N times concurrently.
# Every run has a hang timeout (the suite's, or --timeout): its process is stopped (TERM, KILL 30 s later) and the run
# is marked TIMEOUT. Results go to TestResults/cluster/<batch>/ (matrix: <suite>/ and <suite>/rerun-<n>/, plan.txt;
# runs: <run>/), each with output.log, results.xml (xunit), exit, diag/ (ClusterDiagnostics: pod logs, describe,
# events, PVC/PV, node state), plus summary.txt. Each run's own namespaces are reaped once its process has ended.
#
# Usage: scripts/run-cluster.sh [options] [-- extra xunit v3 runner args]
#       --matrix [S,...]  the suite matrix (lnd, onchain, anchors, gossip, eclair, cln, abcd, ldk, faults, postgres,
#                         tor; default all). Not with -n, --suite, --class/--method, --project or --trait
#       --max-namespaces M  matrix: run namespaces its suites may hold at once (default 6 = the machine's cap; a suite
#                         whose parallel collections need more runs with -parallel none when that fits)
#       --rerun-max N     matrix: rerun at most N failed classes of a suite alone (default 3; 0 = no reruns)
#   -n, --runs N          default mode: runs (default 3)
#   -j, --jobs J          runs (default mode, default N) or suites (matrix, default 3) in flight at once; at most 6
#   -c, --config C        build configuration (default Release)
#   -f, --framework F     target framework (default net10.0)
#   -p, --project P       the test project: cluster (default, test/NLightning.Testing.Cluster.Tests), integration
#                         (test/NLightning.Integration.Tests: our in-process node in cluster topologies, the
#                         NLightning.Integration.Tests.Cluster namespace) or a path to a test project directory
#       --suite S         one suite of the matrix on the cluster backend (NLTG_TEST_BACKEND=cluster), N times; its
#                         project, tests, explicit mode and hang timeout come from `nltg-cluster matrix list`: cln = the
#                         CLN interop suite (--explicit off: its 4 Explicit capture tests stay out unless --explicit on
#                         is given; eclair and ldk likewise), postgres = Docker/PostgresTests and the Explicit
#                         Cluster/Live/ServerDatabaseClusterTests on Postgres pods, faults = the partition and ZMQ-loss
#                         tests, lnd/onchain/anchors/gossip/abcd = the LND Docker suites (refused until their fixture
#                         has its cluster backend); --class/--method replace a suite's classes; tor is refused (Docker)
#       --class X         a test class to run (repeatable; default: every Category=Cluster test)
#       --method X        a test method (repeatable; xunit v3 wildcards allowed)
#       --trait T         the xunit trait filter instead of Category=Cluster (with --suite: instead of its trait)
#       --explicit M      xunit's -explicit mode: only (default: the Cluster tests are Explicit), on or off
#       --timeout D       the hang timeout of each run (90s, 30m, 2h; default: the suite's, else 60m)
#       --context C       kubeconfig context (default $NLTG_KUBE_CONTEXT, else orbstack)
#       --id ID           batch id (default rc-<UTC timestamp>, matrix mx-<UTC timestamp>); run i gets <id>-<i>, a
#                         matrix suite <id>-<suite> and its reruns <id>-<suite>-r<n>
#       --no-build        use the existing build
#       --reap-orphans    first reap runs whose owner process is gone or which are past their TTL
#       --keep            keep the namespaces (NLTG_KEEP_NAMESPACE=1; the reaper then leaves them until their TTL)
#       --keep-on-failure keep only the namespaces of failed runs (NLTG_KEEP_NAMESPACE=failure; reaped after their
#                         TTL; kept namespaces count against the machine's cap until then)
#       --diag M          when to collect diagnostics: failure (default), always or off (NLTG_CLUSTER_DIAG)
#       --keep-logs       leave the logs of green runs as they are (by default they are gzipped once the summary is
#                         written: a suite's output.log reaches 0.3-0.8 GB, NL-818)
#
# Example: the default matrix, 3 suites at once within the 6-namespace cap
#   scripts/run-cluster.sh --matrix
# Example: a small matrix within 2 namespaces (postgres then runs its two collections one after the other)
#   scripts/run-cluster.sh --matrix cln,ldk,postgres -j 2 --max-namespaces 2
# Example: the whole CLN interop suite, 3 runs at once (one CLN class: add --class)
#   scripts/run-cluster.sh -n 3 --suite cln
# Example: the Eclair, LDK, Postgres or partition suite alone
#   scripts/run-cluster.sh -n 1 --suite eclair; scripts/run-cluster.sh -n 1 --suite postgres
# Example: the scaffold's namespace test 3 times at once; our in-process node against CLN and LND pods
#   scripts/run-cluster.sh -n 3 --method '*ARunDeploysABusyboxStatefulSet*'
#   scripts/run-cluster.sh -n 3 -p integration --class NLightning.Integration.Tests.Cluster.Live.InProcessNodeClusterTests
#
# Never runs Docker suites and never touches namespaces outside nltg-spike-*: the test processes create only their
# own namespaces, and the reaper only deletes harness run namespaces (nltg-cluster reap, RunReaper). Stopping the
# runner (Ctrl-C, TERM) stops its test processes and reaps their namespaces.
# Test hook (scripts/tests/run-cluster-tests.sh only): NLTG_RUN_CLUSTER_FAKE_TESTS=<command> runs <command> instead of
# the test assembly and skips its build check, the cluster check and the reaper.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
runs=""
jobs=""
config=Release
framework=net10.0
project=cluster
project_set=""
context="${NLTG_KUBE_CONTEXT:-orbstack}"
batch=""
build=1
reap_orphans=0
keep_logs=0
keep=0
diag="${NLTG_CLUSTER_DIAG:-failure}"
trait="Category=Cluster"
trait_set=""
explicit=only
explicit_set=""
suite=""
matrix=0
matrix_suites=""
max_namespaces=6
rerun_max=3
timeout=""
timeout_seconds=""
filters=()
extra=()
fake_tests="${NLTG_RUN_CLUSTER_FAKE_TESTS:-}"

die() { echo "run-cluster: $*" >&2; exit 2; }

# "90s", "30m", "2h", "1d" or seconds -> seconds
seconds_of() {
  local number unit
  [[ "$1" =~ ^([0-9]+)([smhd]?)$ ]] || return 1
  number="${BASH_REMATCH[1]}"
  unit="${BASH_REMATCH[2]}"
  case "$unit" in
    m) number=$((number * 60)) ;;
    h) number=$((number * 3600)) ;;
    d) number=$((number * 86400)) ;;
  esac
  (( number > 0 )) || return 1
  echo "$number"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --matrix)
      matrix=1
      shift
      if [[ $# -gt 0 && "$1" != -* ]]; then matrix_suites="$1"; shift; fi ;;
    --max-namespaces) max_namespaces="${2:?}"; shift 2 ;;
    --rerun-max) rerun_max="${2:?}"; shift 2 ;;
    -n|--runs) runs="${2:?}"; shift 2 ;;
    -j|--jobs) jobs="${2:?}"; shift 2 ;;
    -c|--config) config="${2:?}"; shift 2 ;;
    -f|--framework) framework="${2:?}"; shift 2 ;;
    -p|--project) project="${2:?}"; project_set=1; shift 2 ;;
    --class) filters+=(-class "${2:?}"); shift 2 ;;
    --method) filters+=(-method "${2:?}"); shift 2 ;;
    --context) context="${2:?}"; shift 2 ;;
    --id) batch="${2:?}"; shift 2 ;;
    --no-build) build=0; shift ;;
    --reap-orphans) reap_orphans=1; shift ;;
    --keep) keep=1; shift ;;
    --keep-on-failure) keep=failure; shift ;;
    --keep-logs) keep_logs=1; shift ;;
    --diag) diag="${2:?}"; shift 2 ;;
    --trait) trait="${2:?}"; trait_set=1; shift 2 ;;
    --explicit) explicit="${2:?}"; explicit_set=1; shift 2 ;;
    --timeout) timeout="${2:?}"; shift 2 ;;
    --suite) suite="${2:?}"; shift 2 ;;
    -h|--help) awk 'NR == 1 { next } /^#/ { sub(/^# ?/, ""); print; next } { exit }' "$0"; exit 0 ;;
    --) shift; extra=("$@"); break ;;
    *) die "unknown argument $1 (see --help)" ;;
  esac
done

if (( matrix )); then
  [[ -z "$runs" ]] || die "--matrix runs each suite once; start a second runner (another --id) for a second pass"
  [[ -z "$suite" ]] || die "--matrix and --suite exclude each other (--matrix $suite runs one suite)"
  (( ${#filters[@]} == 0 )) || die "--matrix takes no --class/--method (use --suite S --class X)"
  [[ -z "$project_set" && -z "$trait_set" ]] || die "--matrix takes no --project/--trait (the suites set them)"
  [[ "$max_namespaces" =~ ^[0-9]+$ && "$max_namespaces" -ge 1 && "$max_namespaces" -le 6 ]] \
    || die "--max-namespaces must be 1-6"
  [[ "$rerun_max" =~ ^[0-9]+$ ]] || die "--rerun-max must be a number"
  jobs="${jobs:-3}"
else
  runs="${runs:-3}"
  [[ "$runs" =~ ^[0-9]+$ && "$runs" -ge 1 ]] || die "--runs must be a positive number"
  jobs="${jobs:-$runs}"
fi
[[ "$jobs" =~ ^[0-9]+$ && "$jobs" -ge 1 ]] || die "--jobs must be a positive number"
[[ "$diag" =~ ^(failure|always|off)$ ]] || die "--diag must be failure, always or off"
[[ "$explicit" =~ ^(only|on|off)$ ]] || die "--explicit must be only, on or off"
if [[ -n "$timeout" ]]; then
  timeout_seconds="$(seconds_of "$timeout")" || die "--timeout must be like 90s, 30m or 2h"
fi
if (( jobs > 6 )); then echo "run-cluster: capping --jobs at 6 (the harness's namespace cap)"; jobs=6; fi
if (( matrix )); then
  batch="${batch:-mx-$(date -u +%Y%m%d%H%M%S)}"
  max_batch=24 # <batch>-<suite>-r<n> must stay a 40-character run id with room for a "-<n>" suffix
else
  batch="${batch:-rc-$(date -u +%Y%m%d%H%M%S)}"
  max_batch=34
fi
batch="$(echo "$batch" | tr '[:upper:]' '[:lower:]' | tr -c 'a-z0-9\n' '-' | sed 's/^-*//; s/-*$//')"
[[ -n "$batch" && ${#batch} -le $max_batch ]] || die "--id must leave 1-$max_batch characters of [a-z0-9-]"

# The suites all live in the integration project (nltg-cluster matrix list); the project is known before the build.
if [[ -n "$suite" ]] || (( matrix )); then project=integration; fi
case "$project" in
  cluster) test_project="$repo_root/test/NLightning.Testing.Cluster.Tests" ;;
  integration) test_project="$repo_root/test/NLightning.Integration.Tests" ;;
  *) test_project="$(cd "$project" 2> /dev/null && pwd)" || die "--project $project: no such directory" ;;
esac
cli_project="$repo_root/test/NLightning.Testing.Cluster.Cli"
test_dll="$test_project/bin/$config/$framework/$(basename "$test_project").dll"
cli_dll="$cli_project/bin/$config/$framework/nltg-cluster.dll"
results="$repo_root/TestResults/cluster/$batch"
mkdir -p "$results"

export NLTG_KUBE_CONTEXT="$context"
cli() { if [[ -n "$fake_tests" ]]; then return 0; fi; dotnet "$cli_dll" "$@" --context "$context"; }
matrix_cli() { dotnet "$cli_dll" matrix "$@"; }

# 1. Build once (the tests and the CLI), never per run.
if (( build )); then
  echo "run-cluster: building ($config, $framework)"
  for p in "$test_project" "$cli_project"; do
    dotnet build "$p" -c "$config" -f "$framework" -p:NltgTargetNet11=false \
      -p:MSBuildWarningsAsMessages=MSB4121 -nologo -v quiet > "$results/build-$(basename "$p").log" 2>&1 \
      || { tail -30 "$results/build-$(basename "$p").log"; die "build of $(basename "$p") failed"; }
  done
fi
[[ -f "$cli_dll" ]] || die "missing build output ($cli_dll); drop --no-build"
if [[ -n "$fake_tests" ]]; then
  read -ra test_cmd <<< "$fake_tests"
else
  [[ -f "$test_dll" ]] || die "missing build output ($test_dll); drop --no-build"
  test_cmd=(dotnet "$test_dll")
fi

# 2. The selection. A suite comes from the catalog (nltg-cluster matrix plan), never from this script. Plan line
#    fields: run|skip, name, project, explicit, namespaces, parallel, timeout s, selection, constraints, note.
select_args=()     # replaced by --class/--method, and by a rerun's class
constraint_args=() # always applied
suite_timeout=3600
backend="${NLTG_TEST_BACKEND:-}"
if [[ -n "$suite" ]]; then
  plan_line="$(matrix_cli plan --suites "$suite" --repo "$repo_root")" || die "--suite $suite: see above"
  IFS='|' read -r st _ _ s_explicit _ _ s_timeout s_selection s_constraints s_note <<< "$plan_line"
  [[ "$st" == run ]] || die "--suite $suite: $s_note"
  [[ -n "$explicit_set" ]] || explicit="$s_explicit"
  [[ "$s_selection" == - ]] || read -ra select_args <<< "$s_selection"
  read -ra constraint_args <<< "$s_constraints"
  if [[ -n "$trait_set" ]]; then # --trait replaces the suite's own -trait
    kept=()
    for (( i = 0; i < ${#constraint_args[@]}; i++ )); do
      if [[ "${constraint_args[$i]}" == -trait ]]; then i=$((i + 1)); else kept+=("${constraint_args[$i]}"); fi
    done
    constraint_args=(${kept[@]+"${kept[@]}"} -trait "$trait")
  fi
  suite_timeout="$s_timeout"
  backend=cluster
else
  constraint_args=(-trait "$trait")
fi
if (( ${#filters[@]} > 0 )); then select_args=("${filters[@]}"); fi
run_timeout="${timeout_seconds:-$suite_timeout}"

if [[ -z "$fake_tests" ]]; then
  kubectl --context "$context" version --request-timeout=10s > /dev/null 2>&1 \
    || die "the cluster of context '$context' does not answer"
fi

if (( reap_orphans )); then
  echo "run-cluster: reaping orphaned runs"
  cli reap | tee "$results/reap-before.txt"
fi

# One test process: its own run id, log, xunit XML and diagnostics folder, stopped by its hang timeout (TERM, then KILL
# 30 s later; marked by a "timedout" file). Writes "<exit code> <wall s> <start epoch>" to <dir>/exit when it ended.
# Usage: run_attempt <dir> <run id> <timeout s> <explicit> <parallel or -> <backend> [xunit filters...]
run_attempt() {
  local dir="$1" id="$2" limit="$3" mode="$4" parallel="$5" run_backend="$6"
  shift 6
  local start pid watchdog code parallel_args=() wait_deletion=""
  if [[ "$parallel" != - ]]; then parallel_args=(-parallel "$parallel"); fi
  # Collections one after another: each waits for its namespace to be gone before the next one starts
  if [[ "$parallel" == none ]]; then wait_deletion=1; fi
  mkdir -p "$dir"
  echo "$id" > "$dir/run-id"
  start=$(date +%s)
  NLTG_TEST_RUN_ID="$id" NLTG_KEEP_NAMESPACE="$keep" NLTG_CLUSTER_DIAG="$diag" NLTG_CLUSTER_DIAG_DIR="$dir/diag" \
    NLTG_TEST_BACKEND="$run_backend" NLTG_WAIT_NAMESPACE_DELETION="$wait_deletion" \
    "${test_cmd[@]}" -explicit "$mode" ${parallel_args[@]+"${parallel_args[@]}"} "$@" -xml "$dir/results.xml" \
    -showLiveOutput -noColor ${extra[@]+"${extra[@]}"} > "$dir/output.log" 2>&1 < /dev/null &
  pid=$!
  echo "$pid" > "$dir/pid"
  (
    sleeper=""
    trap 'if [[ -n "$sleeper" ]]; then kill "$sleeper" 2> /dev/null; fi; exit 0' TERM
    sleep "$limit" &
    sleeper=$!
    wait "$sleeper"
    if kill -0 "$pid" 2> /dev/null; then
      touch "$dir/timedout"
      echo "run-cluster: $id hit its hang timeout (${limit}s); stopping it"
      kill -TERM "$pid" 2> /dev/null
      sleep 30 &
      sleeper=$!
      wait "$sleeper"
      kill -KILL "$pid" 2> /dev/null
    fi
    exit 0
  ) &
  watchdog=$!
  echo "$watchdog" > "$dir/watchdog"
  set +e
  wait "$pid"
  code=$?
  kill -TERM "$watchdog" 2> /dev/null
  wait "$watchdog" 2> /dev/null
  set -e
  rm -f "$dir/pid" "$dir/watchdog"
  echo "$code $(( $(date +%s) - start )) $start" > "$dir/exit"
}

# The namespaces of a run whose process has ended (its owner is gone, so the reaper takes them).
reap_run() {
  if [[ "$keep" == 1 ]]; then return 0; fi
  cli reap --run "$1" --wait > "$2/reap-after.txt" 2>&1 || echo "run-cluster: reap of $1 failed"
}

# "<all> <active>": the harness namespaces whose name starts with $1 (terminating ones hold an admission slot too).
count_namespaces() {
  kubectl --context "$context" get ns -l app.kubernetes.io/managed-by=nltg-test-harness --no-headers 2> /dev/null \
    | awk -v p="$1" 'index($1, p) == 1 { n++; if ($2 == "Active") a++ } END { print n + 0, a + 0 }'
}

# Matrix: until run $1's namespaces are gone (terminating ones included; at most 3 min), so the slot it frees is free.
wait_namespaces_gone() {
  if [[ -n "$fake_tests" || "$keep" == 1 ]]; then return 0; fi
  local deadline=$(( $(date +%s) + 180 )) all active
  while (( $(date +%s) < deadline )); do
    read -r all active < <(count_namespaces "nltg-spike-$1")
    if (( all == 0 )); then return 0; fi
    sleep 2
  done
  echo "run-cluster: namespaces of $1 still there after 3 min; freeing its slot anyway"
}

# After the summary: gzip the logs of runs that exited 0 (output.log -> output.log.gz; failed runs keep theirs as they
# are, the summary links them).
compress_green_logs() {
  if (( keep_logs )); then return 0; fi
  local f dir
  while IFS= read -r f; do
    dir="$(dirname "$f")"
    if [[ "$(cut -d' ' -f1 "$f")" == 0 && ! -e "$dir/timedout" && -f "$dir/output.log" ]]; then
      gzip -f "$dir/output.log" &
    fi
  done < <(find "$results" -name exit -type f)
  wait
}

# Stopping the runner stops its suites, their watchdogs and test processes; their namespaces are reaped (owner gone).
pids=()
on_signal() {
  trap - INT TERM
  echo "run-cluster: stopping batch $batch"
  local pid f
  for pid in ${pids[@]+"${pids[@]}"}; do kill -TERM "$pid" 2> /dev/null || true; done
  while IFS= read -r f; do kill -TERM "$(cat "$f")" 2> /dev/null || true; done \
    < <(find "$results" \( -name pid -o -name watchdog \) -type f 2> /dev/null)
  sleep 2
  cli reap --run "$batch" > /dev/null 2>&1 || true
  exit 130
}
trap on_signal INT TERM

batch_start=$(date +%s)

# 3a. The matrix.
if (( matrix )); then
  plan_args=(--max-namespaces "$max_namespaces" --repo "$repo_root")
  if [[ -n "$matrix_suites" ]]; then plan_args+=(--suites "$matrix_suites"); fi
  matrix_cli plan "${plan_args[@]}" > "$results/plan.txt" || die "no plan (see above)"

  names=()
  weights=()
  lines=()
  while IFS= read -r line; do
    IFS='|' read -r st name _ _ ns _ _ _ _ note <<< "$line"
    if [[ "$st" == run ]]; then
      names+=("$name")
      weights+=("$ns")
      lines+=("$line")
    else
      echo "run-cluster: skipping $name: $note"
    fi
  done < "$results/plan.txt"
  echo "run-cluster: matrix $batch: ${names[*]:-nothing to run}; $jobs suite(s) and $max_namespaces namespace(s) at" \
       "once, context $context, results in $results"

  # One suite: its run, its namespaces reaped, then each failed class rerun alone (in the same slot and weight).
  run_suite() {
    local line="$1" st name s_project s_explicit ns parallel s_timeout s_selection s_constraints note
    IFS='|' read -r st name s_project s_explicit ns parallel s_timeout s_selection s_constraints note <<< "$line"
    local mode="${explicit_set:+$explicit}" limit="${timeout_seconds:-$s_timeout}" sel=() con=() id="$batch-$name"
    mode="${mode:-$s_explicit}"
    if [[ "$parallel" == collections ]]; then parallel=-; fi
    if [[ "$s_selection" != - ]]; then read -ra sel <<< "$s_selection"; fi
    read -ra con <<< "$s_constraints"
    run_attempt "$results/$name" "$id" "$limit" "$mode" "$parallel" cluster \
      ${sel[@]+"${sel[@]}"} ${con[@]+"${con[@]}"}
    reap_run "$id" "$results/$name"
    wait_namespaces_gone "$id"
    if (( rerun_max == 0 )); then return 0; fi
    local k=0 class
    while IFS= read -r class; do
      if [[ -z "$class" ]]; then continue; fi
      k=$((k + 1))
      echo "run-cluster: $name: rerunning $class alone (flake rule)"
      mkdir -p "$results/$name/rerun-$k"
      echo "$class" > "$results/$name/rerun-$k/class"
      run_attempt "$results/$name/rerun-$k" "$id-r$k" "$limit" "$mode" "$parallel" cluster \
        -class "$class" ${con[@]+"${con[@]}"}
      reap_run "$id-r$k" "$results/$name/rerun-$k"
      wait_namespaces_gone "$id-r$k"
    done < <(matrix_cli rerun-classes "$results/$name" --max "$rerun_max")
  }

  # The admission queue: in plan order, start every queued suite that fits (jobs and namespaces) as slots free up.
  queued=()
  for (( q = 0; q < ${#names[@]}; q++ )); do queued+=("$q"); done
  slot_weights=()
  slot_names=()
  used=0
  # The batch's namespaces as the cluster shows them, sampled every few seconds: the peak goes in the summary
  peak_all=0
  peak_active=0
  tick=0
  sample_peak() {
    if [[ -n "$fake_tests" ]]; then return 0; fi
    local all active
    read -r all active < <(count_namespaces "nltg-spike-$batch-")
    if (( all > peak_all )); then peak_all=$all; fi
    if (( active > peak_active )); then peak_active=$active; fi
    echo "$peak_all $peak_active $max_namespaces" > "$results/peak-namespaces"
  }
  while (( ${#queued[@]} > 0 || ${#pids[@]} > 0 )); do
    if (( tick % 3 == 0 )); then sample_peak; fi
    tick=$((tick + 1))
    # Free the slots of finished suites
    still=()
    still_w=()
    still_n=()
    for (( k = 0; k < ${#pids[@]}; k++ )); do
      if kill -0 "${pids[$k]}" 2> /dev/null; then
        still+=("${pids[$k]}")
        still_w+=("${slot_weights[$k]}")
        still_n+=("${slot_names[$k]}")
      else
        wait "${pids[$k]}" 2> /dev/null || true
        used=$((used - slot_weights[k]))
        echo "run-cluster: $(date -u +%H:%M:%S) ${slot_names[$k]} done, $(( $(date +%s) - batch_start ))s into the matrix"
      fi
    done
    pids=(${still[@]+"${still[@]}"})
    slot_weights=(${still_w[@]+"${still_w[@]}"})
    slot_names=(${still_n[@]+"${still_n[@]}"})
    # Admit what fits
    rest=()
    for q in ${queued[@]+"${queued[@]}"}; do
      if (( ${#pids[@]} < jobs && used + weights[q] <= max_namespaces )); then
        run_suite "${lines[$q]}" > "$results/${names[$q]}.runner.log" 2>&1 &
        pids+=("$!")
        slot_weights+=("${weights[$q]}")
        slot_names+=("${names[$q]}")
        used=$((used + weights[q]))
        echo "run-cluster: $(date -u +%H:%M:%S) started ${names[$q]} (${weights[$q]} namespace(s);" \
             "$used/$max_namespaces in use)"
      else
        rest+=("$q")
      fi
    done
    queued=(${rest[@]+"${rest[@]}"})
    if (( ${#queued[@]} > 0 || ${#pids[@]} > 0 )); then sleep 2; fi
  done

  set +e
  matrix_cli summary "$results" --repo "$repo_root" --started "$batch_start" --rerun-max "$rerun_max" \
    | tee "$results/summary.txt"
  status=${PIPESTATUS[0]}
  set -e
  compress_green_logs
  if [[ -z "$fake_tests" ]]; then
    echo "run-cluster: run namespaces left under nltg-spike:"
    cli list || true
  fi
  exit "$status"
fi

# 3b. N runs of one selection: each its own process and run id; at most $jobs in flight.
echo "run-cluster: batch $batch, $runs run(s), $jobs at once, context $context, results in $results${suite:+, suite $suite on the cluster backend}"
ids=()
running() { local n=0 pid; for pid in ${pids[@]+"${pids[@]}"}; do kill -0 "$pid" 2> /dev/null && n=$((n + 1)); done; echo "$n"; }

for (( i = 1; i <= runs; i++ )); do
  while (( $(running) >= jobs )); do sleep 1; done
  id="$batch-$i"
  run_attempt "$results/$id" "$id" "$run_timeout" "$explicit" - "$backend" \
    ${select_args[@]+"${select_args[@]}"} ${constraint_args[@]+"${constraint_args[@]}"} &
  pids+=("$!")
  ids+=("$id")
  echo "run-cluster: started $id (pid $!)"
done
for pid in "${pids[@]}"; do wait "$pid" || true; done

# 4. Cleanup: each run's own namespaces (their processes have ended, so the reaper sees their owner gone).
for id in "${ids[@]}"; do reap_run "$id" "$results/$id"; done

# 5. Summary.
set +e
python3 - "$results" "$batch_start" "$repo_root" "${ids[@]}" << 'PY' | tee "$results/summary.txt"
import os, re, sys, xml.etree.ElementTree as ET
results, batch_start, repo_root, ids = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4:]
rows = [("RUN", "EXIT", "TOTAL", "PASSED", "FAILED", "SKIPPED", "START", "END", "WALL", "SLOT", "NAMESPACES", "DIAG",
         "FIRST ERROR")]
failed = 0
diag_lines = []

def dumps_of(run_dir):
    """The dump folders of a run: diag/<test or fixture>/<namespace>[-n] (each has a summary.txt)."""
    root = os.path.join(run_dir, "diag")
    found = []
    for base, _, files in os.walk(root):
        if "summary.txt" in files:
            found.append(base)
    return sorted(found)
for run in ids:
    d = os.path.join(results, run)
    exit_file = os.path.join(d, "exit")
    code, wall, start = (open(exit_file).read().split() + ["?"] * 3)[:3] if os.path.exists(exit_file) else ("?",) * 3
    begin = f"+{int(start) - batch_start}s" if start.isdigit() else "?"
    end = f"+{int(start) + int(wall) - batch_start}s" if start.isdigit() and wall.isdigit() else "?"
    total = passed = fails = skipped = "?"
    error = ""
    xml = os.path.join(d, "results.xml")
    if os.path.exists(xml):
        try:
            a = ET.parse(xml).getroot().find("assembly")
        except ET.ParseError:
            a = None
        if a is not None:
            total, passed, fails, skipped = (a.get(k, "?") for k in ("total", "passed", "failed", "skipped"))
            for t in a.iter("test"):
                if t.get("result") == "Fail":
                    m = t.find("failure/message")
                    error = f"{t.get('method') or t.get('name')}: {(m.text or '').strip().splitlines()[0] if m is not None and m.text else 'failed'}"
                    break
    timed_out = os.path.exists(os.path.join(d, "timedout"))
    if timed_out:
        error = f"hang timeout after {wall}s"
    log = os.path.join(d, "output.log")
    namespaces, kept, slot = set(), set(), "?"
    if os.path.exists(log):
        slot = "direct"
        with open(log, errors="replace") as lines:  # a suite's log reaches hundreds of MB: line by line
            for line in lines:
                if "namespace nltg-" in line:
                    namespaces.update(re.findall(r"namespace (nltg-[a-z0-9-]+) created", line))
                    kept.update(re.findall(r"namespace (nltg-[a-z0-9-]+) kept on failure", line))
                if "waiting for a slot" in line or "over the cap" in line:
                    slot = "waited"
    namespaces, kept = sorted(namespaces), sorted(kept)
    dumps = dumps_of(d)
    run_failed = timed_out or code != "0" or fails not in ("0", "?") or total in ("0", "?")
    if run_failed:
        failed += 1
        if not error:
            error = "no tests ran" if total == "0" else f"exit {code}"
        diag_lines.append(f"{run}: " + (f"{len(dumps)} dump(s)" if dumps else "no diagnostics collected")
                          + (f", kept namespaces {','.join(kept)}" if kept else ""))
        diag_lines.extend(f"  {os.path.relpath(p, repo_root)}" for p in dumps)
    rows.append((run, code, total, passed, fails, skipped, begin, end, f"{wall}s", slot, ",".join(namespaces) or "-",
                 str(len(dumps)), error[:120]))
widths = [max(len(r[i]) for r in rows) for i in range(len(rows[0]))]
for r in rows:
    print("  ".join(c.ljust(w) for c, w in zip(r, widths)).rstrip())
print(f"{len(ids) - failed}/{len(ids)} run(s) green")
if diag_lines:
    print("diagnostics of the failed runs:")
    for line in diag_lines:
        print(line)
sys.exit(1 if failed else 0)
PY
status=${PIPESTATUS[0]}
set -e
compress_green_logs

if [[ -z "$fake_tests" ]]; then
  echo "run-cluster: run namespaces left under nltg-spike:"
  cli list || true
fi
exit "$status"