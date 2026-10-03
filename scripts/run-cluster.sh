#!/usr/bin/env bash
# The test runner of the Kubernetes harness (plan docs/agents/TEST_HARNESS_PLAN.md §5 step 5, R4, R13, R14) and the
# only way to run the integration suites (LND, on-chain, gossip, ABCD, CLN, Eclair, LDK, Postgres, partitions) on a
# cluster (OrbStack's locally). Every test process gets its own NLTG_TEST_RUN_ID, so its own namespaces
# (nltg-spike-<run>[-<n>]); it never starts Docker containers and needs no Docker lock. The cluster is the only backend
# of the LND suites (lnd, onchain, anchors, gossip, day0, abcd) since NL-820 and of the CLN, Eclair, LDK and Postgres
# suites since NL-866; only Tor stays on Docker (scripts/run-interop.sh tor). The harness never builds or pulls the
# locally built images (pull policy Never; OrbStack's cluster shares the Docker image store): build them once with
#   docker build -t custom_lnd:0.21.4-beta test/Docker/custom_lnd          (lnd, onchain, anchors, gossip, day0, abcd)
#   docker build -t nltg-eclair:0.14.3 test/Docker/eclair                   (eclair, eclair2)
#   docker build -t nltg-ldk-server:dc02b76c test/Docker/ldk_server         (ldk; a cold build takes 10-20 min)
# A missing image fails its pod at once (ErrImageNeverPull). CLN, bitcoind and Postgres are pulled by digest.
#
# Two modes, both building once (the test project and the nltg-cluster CLI), never per run:
#   --matrix [S,...]  runs the suites (default: every suite, see `nltg-cluster matrix list`) with at most -j suites in
#                     flight and at most --max-namespaces run namespaces held by them at once: an admission queue in
#                     plan order (the longest suites first) starts a suite as soon as its namespace count fits. A failed
#                     class is rerun alone once (the flake rule; at most --rerun-max classes per suite, never after a
#                     hang timeout, a crash or a fixture error) and a green rerun marks the suite rerun-green. A slot
#                     is freed once the suite's namespaces are gone, terminating ones included, and every run waits
#                     for each of its namespaces to go before it builds the next (NLTG_WAIT_NAMESPACE_DELETION). Prints
#                     a summary table (suite, result, tests, passed/failed/skipped/not run, rerun, start, wall, fixture
#                     ready, namespaces created/planned at once, dumps, first error), the log and diagnostics folders
#                     of every failed suite and the batch's sampled namespace peak; exits 1 on a real failure, 3 when
#                     nothing ran or a suite named in --matrix S,... was skipped. Docker-only suites (tor) are listed
#                     as skipped, with the reason; suites whose cluster proof is pending (`nltg-cluster matrix list`
#                     names them) are left out of the default matrix and run when named. The namespaces of ended suites
#                     that are still there (kept on failure, or still terminating) count against the budget.
#   default (-n N)    runs one selection (a --suite, or --class/--method/--trait tests) N times concurrently.
# Every run has a hang timeout (the suite's, or --timeout): its process is stopped (TERM, KILL 30 s later) and the run
# is marked TIMEOUT. Results go to TestResults/cluster/<batch>/ (matrix: <suite>/ and <suite>/rerun-<n>/, plan.txt;
# runs: <run>/), each with output.log, results.xml (xunit), exit, diag/ (ClusterDiagnostics: pod logs, describe,
# events, PVC/PV, node state), plus summary.txt. Each run's own namespaces are reaped once its process has ended.
#
# Usage: scripts/run-cluster.sh [options] [-- extra xunit v3 runner args]
#       --matrix [S,...]  the suite matrix (lnd, cln, gossip, eclair, ldk, eclair2, day0, onchain, anchors, faults,
#                         abcd, postgres, tor; default all). Not with -n, --suite, --class/--method, --project or --trait
#       --max-namespaces M  matrix: run namespaces its suites may hold at once (default 12 = the machine's cap,
#                         RunAdmission.DefaultMaxRuns, read through `nltg-cluster matrix cap`, NL-844; a suite
#                         whose parallel collections need more runs with -parallel none when that fits)
#       --rerun-max N     matrix: rerun at most N failed classes of a suite alone (default 3; 0 = no reruns)
#   -n, --runs N          default mode: runs (default 3)
#   -j, --jobs J          runs (default mode, default N) or suites (matrix, default the --max-namespaces budget) in
#                         flight at once; at most the machine's cap (12)
#   -c, --config C        build configuration (default Release)
#   -f, --framework F     target framework (default net10.0)
#   -p, --project P       the test project: cluster (default, test/NLightning.Testing.Cluster.Tests), integration
#                         (test/NLightning.Integration.Tests: our in-process node in cluster topologies, the
#                         NLightning.Integration.Tests.Cluster namespace) or a path to a test project directory
#       --suite S         one suite of the matrix on the cluster backend (NLTG_TEST_BACKEND=cluster), N times; its
#                         project, tests, explicit mode and hang timeout come from `nltg-cluster matrix list`: cln = the
#                         CLN interop suite (--explicit off: its 4 Explicit capture tests stay out unless --explicit on
#                         is given; eclair and ldk likewise; eclair2 = EclairSpliceTests, split from eclair),
#                         postgres = Docker/PostgresTests and the Explicit Cluster/Live/ServerDatabaseClusterTests on
#                         Postgres pods, faults = the partition and ZMQ-loss tests, lnd/gossip/day0/onchain/anchors/abcd
#                         = the LND suites on LightningRegtestNetworkFixture's cluster backend (lnd = its regtest
#                         collection, 2 namespaces per run, so -j is capped at 6; day0 = the gossip-regtest classes
#                         outside Docker.Gossip, split from gossip);
#                         --class/--method replace a suite's classes; tor is refused (Docker)
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
#       --keep            keep the namespaces (NLTG_KEEP_NAMESPACE=1; the reaper then leaves them until their TTL; not
#                         with --matrix)
#       --keep-on-failure keep only the namespaces of failed runs (NLTG_KEEP_NAMESPACE=failure; reaped after their
#                         TTL; kept namespaces count against the machine's cap until then)
#       --diag M          when to collect diagnostics: failure (default), always or off (NLTG_CLUSTER_DIAG)
#       --keep-logs       leave the logs of green runs as they are (by default they are gzipped once the summary is
#                         written: a suite's output.log reaches 0.3-0.8 GB, NL-818)
#
# Example: the default matrix, every suite that fits started at once within the 12-namespace cap (18 min)
#   scripts/run-cluster.sh --matrix
# Example: a small matrix within 2 namespaces (postgres then runs its two collections one after the other)
#   scripts/run-cluster.sh --matrix cln,ldk,postgres -j 2 --max-namespaces 2
# Example: the whole CLN interop suite, 3 runs at once (one CLN class: add --class)
#   scripts/run-cluster.sh -n 3 --suite cln
# Example: the Eclair, LDK, Postgres or partition suite alone
#   scripts/run-cluster.sh -n 1 --suite eclair; scripts/run-cluster.sh -n 1 --suite postgres
# Example: the LND suite (the regtest collection), alone; one class of the on-chain suite
#   scripts/run-cluster.sh -n 1 --suite lnd
#   scripts/run-cluster.sh -n 1 --suite onchain --class NLightning.Integration.Tests.Docker.BackupRestoreFlowTests
# Example: the scaffold's namespace test 3 times at once; our in-process node against CLN and LND pods
#   scripts/run-cluster.sh -n 3 --method '*ARunDeploysABusyboxStatefulSet*'
#   scripts/run-cluster.sh -n 3 -p integration --class NLightning.Integration.Tests.Cluster.Live.InProcessNodeClusterTests
#
# Never runs Docker suites and never touches namespaces outside nltg-spike-*: the test processes create only their
# own namespaces, and the reaper only deletes harness run namespaces (nltg-cluster reap, RunReaper). Stopping the
# runner (Ctrl-C, TERM) stops its test processes (TERM, KILL after 30 s) and then reaps their namespaces. Every kubectl
# call of the runner has a 15 s request timeout.
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
max_namespaces="" # default: the machine's cap (nltg-cluster matrix cap)
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
  [[ -z "$max_namespaces" || "$max_namespaces" =~ ^[0-9]+$ ]] || die "--max-namespaces must be a number"
  [[ "$rerun_max" =~ ^[0-9]+$ ]] || die "--rerun-max must be a number"
  # Kept namespaces hold machine slots until their TTL (6 h), so a matrix that keeps all of them could never start its
  # later suites; --keep-on-failure is fine (the queue counts the namespaces failed suites keep)
  [[ "$keep" != 1 ]] || die "--matrix takes no --keep (use --keep-on-failure, or --suite S --keep for one suite)"
  # -j defaults to the namespace budget below (every suite that fits starts at once; NL-844)
else
  runs="${runs:-3}"
  [[ "$runs" =~ ^[0-9]+$ && "$runs" -ge 1 ]] || die "--runs must be a positive number"
  jobs="${jobs:-$runs}"
fi
[[ ( -z "$jobs" && "$matrix" == 1 ) || ( "$jobs" =~ ^[0-9]+$ && "$jobs" -ge 1 ) ]] \
  || die "--jobs must be a positive number"
[[ "$diag" =~ ^(failure|always|off)$ ]] || die "--diag must be failure, always or off"
[[ "$explicit" =~ ^(only|on|off)$ ]] || die "--explicit must be only, on or off"
if [[ -n "$timeout" ]]; then
  timeout_seconds="$(seconds_of "$timeout")" || die "--timeout must be like 90s, 30m or 2h"
fi
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

# The machine's cap on run namespaces, set in one place (RunAdmission.DefaultMaxRuns, NL-844): --max-namespaces and
# -j stay within it, as the test processes' own admission (NLTG_MAX_CONCURRENT_RUNS) does.
machine_cap="$(matrix_cli cap)" && [[ "$machine_cap" =~ ^[0-9]+$ ]] || die "nltg-cluster matrix cap failed"
if (( matrix )); then
  max_namespaces="${max_namespaces:-$machine_cap}"
  (( max_namespaces >= 1 && max_namespaces <= machine_cap )) || die "--max-namespaces must be 1-$machine_cap"
  jobs="${jobs:-$max_namespaces}"
fi
if (( jobs > machine_cap )); then
  echo "run-cluster: capping --jobs at $machine_cap (the harness's namespace cap)"
  jobs=$machine_cap
fi

# 2. The selection. A suite comes from the catalog (nltg-cluster matrix plan), never from this script. Plan line
#    fields: run|skip, name, project, explicit, namespaces, parallel, timeout s, selection, constraints, note.
select_args=()     # replaced by --class/--method, and by a rerun's class
constraint_args=() # always applied
suite_timeout=3600
backend="${NLTG_TEST_BACKEND:-}"
if [[ -n "$suite" ]]; then
  plan_line="$(matrix_cli plan --suites "$suite" --repo "$repo_root")" || die "--suite $suite: see above"
  IFS='|' read -r st _ _ s_explicit s_namespaces _ s_timeout s_selection s_constraints s_note <<< "$plan_line"
  [[ "$st" == run ]] || die "--suite $suite: $s_note"
  # Each run holds the suite's namespaces at once: runs in flight x namespaces stay within the machine's cap
  if (( s_namespaces > 1 && jobs * s_namespaces > machine_cap )); then
    echo "run-cluster: --suite $suite holds $s_namespaces namespaces per run; capping --jobs at" \
         "$(( machine_cap / s_namespaces ))"
    jobs=$(( machine_cap / s_namespaces ))
  fi
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
  local start pid watchdog code parallel_args=()
  if [[ "$parallel" != - ]]; then parallel_args=(-parallel "$parallel"); fi
  # Every run's disposal waits until its namespace is gone (NLTG_WAIT_NAMESPACE_DELETION), so a process never holds
  # more namespaces than its count: collections run one after another (-parallel none) never overlap, and a class that
  # builds one topology per test (faults) never creates the next while the last one terminates (NL-840: a faults suite
  # held 3 namespaces, 1 terminating, under a count of 2)
  local wait_deletion=1
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

# The harness namespaces (name and status, one per line); fails (no output) when the API server does not answer within
# 15 s, so a stalled cluster never hangs the runner and an error is never read as "no namespaces".
list_namespaces() {
  kubectl --context "$context" --request-timeout=15s get ns -l app.kubernetes.io/managed-by=nltg-test-harness \
    --no-headers 2> /dev/null
}

# "<all> <active>": the harness namespaces whose name starts with $1 (terminating ones hold an admission slot too);
# fails (no output) when the cluster does not answer.
count_namespaces() {
  local listing
  listing="$(list_namespaces)" || return 1
  awk -v p="$1" 'index($1, p) == 1 { n++; if ($2 == "Active") a++ } END { print n + 0, a + 0 }' <<< "$listing"
}

# Matrix: until run $1's namespaces are gone (terminating ones included; at most 3 min), so the slot it frees is free.
# A failed listing is retried until the deadline, never taken for "gone". Namespaces the run kept on failure
# (--keep-on-failure; its log $2/output.log says so) are not waited for: the queue counts them (held_by_finished).
wait_namespaces_gone() {
  if [[ -n "$fake_tests" || "$keep" == 1 ]]; then return 0; fi
  if [[ "$keep" == failure && -f "$2/output.log" ]] && grep -q "kept on failure" "$2/output.log"; then return 0; fi
  local deadline=$(( $(date +%s) + 180 )) all active counts
  while (( $(date +%s) < deadline )); do
    if counts="$(count_namespaces "nltg-spike-$1")"; then
      read -r all active <<< "$counts"
      if (( all == 0 )); then return 0; fi
    fi
    sleep 2
  done
  echo "run-cluster: namespaces of $1 still there after 3 min (or the cluster did not answer); freeing its slot" \
       "(the queue keeps counting them)"
}

# After the summary: gzip the logs of the green runs (output.log -> output.log.gz), judged as the summary judges them
# (nltg-cluster matrix green-attempts: exit 0, no timeout, results green); every other run keeps its log as it is,
# the summary links it.
compress_green_logs() {
  if (( keep_logs )); then return 0; fi
  local dir
  while IFS= read -r dir; do
    if [[ -f "$dir/output.log" ]]; then gzip -f "$dir/output.log" & fi
  done < <(matrix_cli green-attempts "$results")
  wait
}

# Stopping the runner stops its suites, their watchdogs and test processes (TERM; KILL after 30 s, as the hang timeout
# does), then reaps the batch's namespaces once their owners are gone (kept ones stay, as --keep* asked).
pids=()
on_signal() {
  trap - INT TERM
  echo "run-cluster: stopping batch $batch"
  local pid f tests=() alive deadline
  # The suites first (so none starts another attempt), then the watchdogs, then the test processes themselves
  for pid in ${pids[@]+"${pids[@]}"}; do kill -TERM "$pid" 2> /dev/null || true; done
  while IFS= read -r f; do kill -TERM "$(cat "$f")" 2> /dev/null || true; done \
    < <(find "$results" -name watchdog -type f 2> /dev/null)
  while IFS= read -r f; do tests+=("$(cat "$f" 2> /dev/null)"); done \
    < <(find "$results" -name pid -type f 2> /dev/null)
  for pid in ${tests[@]+"${tests[@]}"}; do kill -TERM "$pid" 2> /dev/null || true; done
  deadline=$(( $(date +%s) + 30 ))
  while (( $(date +%s) < deadline )); do
    alive=0
    for pid in ${tests[@]+"${tests[@]}"}; do if kill -0 "$pid" 2> /dev/null; then alive=1; fi; done
    if (( ! alive )); then break; fi
    sleep 1
  done
  for pid in ${tests[@]+"${tests[@]}"}; do
    if kill -0 "$pid" 2> /dev/null; then
      echo "run-cluster: test process $pid ignored TERM for 30 s; killing it"
      kill -KILL "$pid" 2> /dev/null || true
    fi
  done
  echo "run-cluster: reaping the batch's namespaces"
  cli reap --run "$batch" --wait > "$results/reap-on-stop.txt" 2>&1 || echo "run-cluster: reap of $batch failed"
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
    wait_namespaces_gone "$id" "$results/$name"
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
      wait_namespaces_gone "$id-r$k" "$results/$name/rerun-$k"
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
    local all active counts
    counts="$(count_namespaces "nltg-spike-$batch-")" || return 0 # the cluster did not answer: no sample
    read -r all active <<< "$counts"
    if (( all > peak_all )); then peak_all=$all; fi
    if (( active > peak_active )); then peak_active=$active; fi
    echo "$peak_all $peak_active $max_namespaces" > "$results/peak-namespaces"
  }
  # The namespaces that suites which already ended still hold (kept on failure, or still terminating after the 3-min
  # wait): they count against the budget until they are gone. Test hook: FAKE_HELD (a number) stands in for them.
  finished=()
  held=0
  held_by_finished() {
    if [[ -n "$fake_tests" ]]; then
      if (( ${#finished[@]} > 0 )); then held="${FAKE_HELD:-0}"; fi
      return 0
    fi
    if (( ${#finished[@]} == 0 )); then held=0; return 0; fi
    local listing
    listing="$(list_namespaces)" || return 0 # the cluster did not answer: keep the last count
    held="$(awk -v b="nltg-spike-$batch-" -v names="${finished[*]}" '
      BEGIN { k = split(names, n, " ") }
      { for (i = 1; i <= k; i++) { p = b n[i]; if ($1 == p || index($1, p "-") == 1) { c++; break } } }
      END { print c + 0 }' <<< "$listing")"
  }
  stalled_since=""
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
        finished+=("${slot_names[$k]}")
        echo "run-cluster: $(date -u +%H:%M:%S) ${slot_names[$k]} done, $(( $(date +%s) - batch_start ))s into the matrix"
      fi
    done
    pids=(${still[@]+"${still[@]}"})
    slot_weights=(${still_w[@]+"${still_w[@]}"})
    slot_names=(${still_n[@]+"${still_n[@]}"})
    # Admit what fits, counting what ended suites still hold
    if (( ${#queued[@]} > 0 )); then held_by_finished; fi
    rest=()
    admitted=0
    for q in ${queued[@]+"${queued[@]}"}; do
      if (( ${#pids[@]} < jobs && used + held + weights[q] <= max_namespaces )); then
        admitted=1
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
    # Nothing runs and nothing fits: the namespaces ended suites hold block the rest. Kept ones (--keep-on-failure)
    # stay for their TTL, so give up at once; terminating ones get 10 min. The suites given up on read NOT RUN.
    if (( ${#pids[@]} == 0 && ${#queued[@]} > 0 && ! admitted )); then
      stalled_since="${stalled_since:-$(date +%s)}"
      if [[ "$keep" == failure ]] || (( $(date +%s) - stalled_since >= 600 )); then
        for q in "${queued[@]}"; do
          echo "run-cluster: not starting ${names[$q]}: $held namespace(s) of ended suites still hold the budget of" \
               "$max_namespaces"
        done
        queued=()
      fi
    else
      stalled_since=""
    fi
    if (( ${#queued[@]} > 0 || ${#pids[@]} > 0 )); then sleep 2; fi
  done

  set +e
  summary_args=(--repo "$repo_root" --started "$batch_start" --rerun-max "$rerun_max")
  if [[ -n "$matrix_suites" ]]; then summary_args+=(--named); fi
  matrix_cli summary "$results" "${summary_args[@]}" | tee "$results/summary.txt"
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