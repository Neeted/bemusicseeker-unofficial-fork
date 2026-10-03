#!/usr/bin/env bash
set -euo pipefail

for command in python3 curl tar gzip sha512sum uname mkdir mktemp rm mv; do
    command -v "$command" >/dev/null 2>&1 || { printf 'Missing prerequisite: %s\n' "$command" >&2; exit 1; }
done
repo_root=$(cd -- "${BASH_SOURCE[0]%/*}/../.." && pwd -P)
source "$repo_root/scripts/linux/environment.sh"
cd -- "$repo_root"
if sdk_version=$(dotnet --version 2>/dev/null); then
    printf 'Using installed SDK: %s\n' "$sdk_version"
    exit 0
fi

sdk_version=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["sdk"]["version"])' "$repo_root/global.json")
install_dir="$BMS_LINUX_TOOLCHAIN_DIR/dotnet/$sdk_version"
[[ ! -e $install_dir && ! -L $install_dir ]] || { printf 'Unusable SDK cache: %s. Inspect it before removing it and retrying.\n' "$install_dir" >&2; exit 1; }
stage=$(mktemp -d "$BMS_LINUX_TOOLCHAIN_DIR/.sdk-stage.XXXXXX")
trap 'status=$?; if ! rm -rf -- "$stage"; then
    printf "SDK staging cleanup failed: %s\n" "$stage" >&2
    [[ $status != 0 ]] || status=1
fi; exit "$status"' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
IFS=. read -r major minor _ <<< "$sdk_version"
channel="$major.$minor"
curl --fail --silent --show-error --location --proto '=https' --proto-redir '=https' --tlsv1.2 \
    --connect-timeout 20 --max-time 120 \
    "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$channel/releases.json" -o "$stage/releases.json"
python3 - "$stage/releases.json" "$sdk_version" > "$stage/download" <<'PY'
import json
import re
import sys
from urllib.parse import urlparse

version = sys.argv[2]
matches = set()
for release in json.load(open(sys.argv[1]))["releases"]:
    for sdk in [release.get("sdk", {}), *release.get("sdks", [])]:
        if sdk.get("version") == version:
            for file in sdk.get("files", []):
                if file.get("rid") == "linux-x64" and file.get("name") == "dotnet-sdk-linux-x64.tar.gz":
                    matches.add((file["url"], file["hash"]))
if len(matches) != 1:
    sys.exit(f"Official metadata does not uniquely identify Linux x64 SDK {version}")
url, digest = matches.pop()
parsed = urlparse(url)
if (parsed.scheme != "https" or parsed.netloc not in
        ("builds.dotnet.microsoft.com", "dotnetcli.azureedge.net", "dotnetcli.blob.core.windows.net")
        or not parsed.path.endswith(f"/dotnet-sdk-{version}-linux-x64.tar.gz")
        or not re.fullmatch(r"[0-9a-fA-F]{128}", digest)):
    sys.exit("Invalid official SDK download metadata")
print(url, digest)
PY
read -r sdk_url sdk_hash < "$stage/download"
curl --fail --silent --show-error --location --proto '=https' --proto-redir '=https' --tlsv1.2 \
    --connect-timeout 20 --max-time 600 "$sdk_url" -o "$stage/sdk.tar.gz"
if ! printf '%s  %s\n' "$sdk_hash" "$stage/sdk.tar.gz" | sha512sum --check --status; then
    printf 'SDK SHA-512 verification failed.\n' >&2
    exit 1
fi
mkdir -- "$stage/sdk"
tar -xzf "$stage/sdk.tar.gz" -C "$stage/sdk"
selected=$(DOTNET_ROOT="$stage/sdk" "$stage/sdk/dotnet" --version)
[[ $selected == "$sdk_version" ]] || { printf 'Staged SDK selection failed: %s\n' "$selected" >&2; exit 1; }
mkdir -p -- "${install_dir%/*}"
mv -T -- "$stage/sdk" "$install_dir"
printf 'Installed SDK: %s\nRun: source scripts/linux/environment.sh\n' "$sdk_version"
