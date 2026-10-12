#!/usr/bin/env bash
set -euo pipefail

revision=cb8a64c71d3b214951e752281f05b9090e77f074
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
cache_dir=${VLS_SPIKE_CACHE:-${XDG_CACHE_HOME:-${HOME}/.cache}/nlightning/vls-compat-${revision}}
source_dir=${cache_dir}/source
run_dir=${cache_dir}/run

command -v git >/dev/null
command -v cargo >/dev/null
cargo +1.94.0 --version
mkdir -p -- "$cache_dir"
if [[ ! -d "$source_dir/.git" ]]; then
    git clone --no-checkout https://gitlab.com/lightning-signer/validating-lightning-signer.git "$source_dir"
    git -C "$source_dir" checkout --detach "$revision"
fi
if [[ $(git -C "$source_dir" rev-parse HEAD) != "$revision" ]]; then
    echo "VLS cache is at the wrong revision; use another VLS_SPIKE_CACHE directory." >&2
    exit 1
fi
git -C "$source_dir" diff --quiet
git -C "$source_dir" diff --cached --quiet
mkdir -p -- "$run_dir"
cp -- "$script_dir/Cargo.toml" "$script_dir/Cargo.lock" "$script_dir/main.rs" "$run_dir/"
printf 'VLS source revision: %s\n' "$revision"
cargo +1.94.0 run --locked --manifest-path "$run_dir/Cargo.toml" "$@"
