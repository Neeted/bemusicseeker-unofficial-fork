#!/usr/bin/env bash
# source して現在のシェルだけに適用する。シェルのオプションと作業ディレクトリは変えない。
_bms_linux_environment() {
    local repo_root cache sdk_version local_sdk command directory path_rest path_entry next_path
    for command in python3 mkdir uname; do
        command -v "$command" >/dev/null 2>&1 || { printf 'Missing prerequisite: %s\n' "$command" >&2; return 1; }
    done
    [[ $(uname -s) == Linux && $(uname -m) == x86_64 ]] || { printf 'Linux x64 is required.\n' >&2; return 1; }
    repo_root=$(cd -- "${BASH_SOURCE[0]%/*}/../.." && pwd -P) || return 1
    sdk_version=$(python3 - "$repo_root/global.json" <<'PY'
import json
import re
import sys

try:
    sdk = json.load(open(sys.argv[1], encoding="utf-8"))["sdk"]
    version = sdk["version"]
    policies = ("disable", "patch", "feature", "minor", "major", "latestPatch",
                "latestFeature", "latestMinor", "latestMajor")
    if not isinstance(version, str) or not re.fullmatch(r"\d+\.\d+\.\d+(?:-[\w.-]+)?", version):
        raise ValueError("sdk.version must be a complete SDK version")
    if sdk.get("rollForward", "patch") not in policies:
        raise ValueError("invalid sdk.rollForward")
    print(version)
except (OSError, ValueError, KeyError, TypeError) as error:
    sys.exit(f"Invalid global.json: {error}")
PY
    ) || return 1
    cache=${BMS_LINUX_TOOLCHAIN_DIR:-"$repo_root/.tmp/linux-toolchain"}
    [[ $cache == /* ]] || { printf 'BMS_LINUX_TOOLCHAIN_DIR must be absolute.\n' >&2; return 1; }
    for directory in cli-home nuget-packages nuget-http-cache nuget-scratch nuget-plugins-cache; do
        mkdir -p -- "$cache/$directory" || return 1
        [[ -w $cache/$directory ]] || { printf 'Toolchain cache is not writable: %s\n' "$cache/$directory" >&2; return 1; }
    done
    [[ -w $cache ]] || { printf 'Toolchain cache is not writable: %s\n' "$cache" >&2; return 1; }
    cache=$(cd -- "$cache" && pwd -P) || return 1
    export BMS_LINUX_TOOLCHAIN_DIR="$cache"
    export DOTNET_CLI_HOME="$cache/cli-home"
    export NUGET_PACKAGES="$cache/nuget-packages"
    export NUGET_HTTP_CACHE_PATH="$cache/nuget-http-cache"
    export NUGET_SCRATCH="$cache/nuget-scratch"
    export NUGET_PLUGINS_CACHE_PATH="$cache/nuget-plugins-cache"
    # WPF のクロスビルドに不要な開発用 HTTPS 証明書を初回実行で生成しない。
    export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
    # global.json の選択規則は dotnet 自身に任せ、利用可能な既存 SDK を優先する。
    if (cd -- "$repo_root" && dotnet --version) >/dev/null 2>&1; then
        return 0
    fi
    local_sdk="$cache/dotnet/$sdk_version"
    if [[ -x $local_sdk/dotnet ]] && (cd -- "$repo_root" && DOTNET_ROOT="$local_sdk" "$local_sdk/dotnet" --version) >/dev/null 2>&1; then
        export DOTNET_ROOT="$local_sdk"
        # 後方に同じ配置先がある場合も、重複させず選択可能な SDK を先頭へ移す。
        path_rest=${PATH-}
        next_path=$local_sdk
        while :; do
            path_entry=${path_rest%%:*}
            [[ $path_entry == "$local_sdk" ]] || next_path+=":$path_entry"
            [[ $path_rest == *:* ]] || break
            path_rest=${path_rest#*:}
        done
        export PATH="$next_path"
    fi
}

if _bms_linux_environment; then
    unset -f _bms_linux_environment
else
    unset -f _bms_linux_environment
    return 1 2>/dev/null || exit 1
fi
