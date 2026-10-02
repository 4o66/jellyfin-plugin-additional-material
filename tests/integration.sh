#!/usr/bin/env bash
# Integration test: a throwaway Jellyfin 12.1 server with sample media, the built plugin, and
# (optionally) the File Transformation plugin. Runs on any Docker host with bash, curl, jq and 7z.
#
#   tests/integration.sh <plugin-out-dir> [file-transformation-plugin-dir]
#   tests/integration.sh --cleanup
#
# Test-only credentials are generated per run into $WORK/creds and never leave the machine.
set -uo pipefail

IMAGE=${IMAGE:-jellyfin/jellyfin:12.1.20260915-010956}
NAME=am-test-jellyfin
PORT=${PORT:-18096}
WORK=${WORK:-/tmp/am-test}
GUID=10121f36-d2e1-4b8d-96c4-b2cc720880f3
BASE="http://127.0.0.1:$PORT"
CLIENT='MediaBrowser Client="am-test", Device="am-test", DeviceId="am-test-1", Version="1.0"'

if [[ ${1:-} == --cleanup ]]; then
  docker rm -f "$NAME" >/dev/null 2>&1
  rm -rf "$WORK"
  echo "cleaned up"
  exit 0
fi

OUT=${1:?usage: integration.sh <plugin-out-dir> [ft-plugin-dir]}
FT=${2:-}
pass=0; fail=0
ok()   { echo "PASS  $*"; pass=$((pass + 1)); }
bad()  { echo "FAIL  $*"; fail=$((fail + 1)); }
check() { if [[ $2 == "$3" ]]; then ok "$1"; else bad "$1 (expected '$3', got '$2')"; fi; }

docker rm -f "$NAME" >/dev/null 2>&1
rm -rf "$WORK"
mkdir -p "$WORK"/{config/plugins,cache,media/training,media/other}

# ---- sample media -----------------------------------------------------------
T="$WORK/media/training/Course A"
mkdir -p "$T/Season 1" "$T/Season 2" "$WORK/media/other/Course B/Season 1"
docker run --rm --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg -v "$WORK/media:/m" "$IMAGE" \
  -loglevel error -f lavfi -i testsrc=duration=3:size=320x240:rate=10 -c:v libx264 -pix_fmt yuv420p /m/sample.mp4
for f in "Season 1/S01E01 - Lesson One" "Season 1/S01E02 - Lesson Two" "Season 2/S02E01 - Sneaky"; do
  cp "$WORK/media/sample.mp4" "$T/$f.mp4"
done
cp "$WORK/media/sample.mp4" "$WORK/media/other/Course B/Season 1/S01E01 - Other.mp4"
rm "$WORK/media/sample.mp4"
mk() { local dir=$1 name=$2; echo "material for $name" > "$WORK/notes.txt"; (cd "$WORK" && 7z a -bso0 -bsp0 "$dir/$name" notes.txt); }
mk "$T" additional-material.zip                              # course
mk "$T/Season 1" additional-material.zip                     # section
mk "$T/Season 1" "S01E01 - Lesson One.material.zip"          # lesson
mk "$T/Season 1" "S01E02 - Lesson Two.material.7z"           # .7z is not recognized in this version
ln -s /etc/hostname "$T/Season 2/S02E01 - Sneaky.material.zip"  # link escaping the library: refused
mk "$WORK/media/other/Course B" additional-material.zip      # library not enabled: ignored
rm "$WORK/notes.txt"

# ---- plugins ----------------------------------------------------------------
mkdir -p "$WORK/config/plugins/Additional Material_1.0.0.0"
cp "$OUT/Jellyfin.Plugin.AdditionalMaterial.dll" "$WORK/config/plugins/Additional Material_1.0.0.0/"
[[ -n $FT ]] && cp -r "$FT" "$WORK/config/plugins/"

docker run -d --name "$NAME" -p "127.0.0.1:$PORT:8096" \
  -v "$WORK/config:/config" -v "$WORK/cache:/cache" -v "$WORK/media:/media:ro" "$IMAGE" >/dev/null
for _ in $(seq 1 90); do [[ $(curl -s "$BASE/health") == Healthy ]] && break; sleep 2; done
check "server healthy" "$(curl -s "$BASE/health")" Healthy

# ---- first-run setup ---------------------------------------------------------
PW_ADMIN=$(head -c 18 /dev/urandom | base64 | tr -dc A-Za-z0-9); PW_USER=$(head -c 18 /dev/urandom | base64 | tr -dc A-Za-z0-9)
printf 'admin %s\nusers %s\n' "$PW_ADMIN" "$PW_USER" > "$WORK/creds"; chmod 600 "$WORK/creds"
J=(-s -H "Content-Type: application/json" -H "Authorization: $CLIENT")
curl "${J[@]}" -X POST "$BASE/Startup/Configuration" -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null
curl "${J[@]}" "$BASE/Startup/User" >/dev/null
curl "${J[@]}" -X POST "$BASE/Startup/User" -d "{\"Name\":\"admin\",\"Password\":\"$PW_ADMIN\"}" >/dev/null
curl "${J[@]}" -X POST "$BASE/Startup/RemoteAccess" -d '{"EnableRemoteAccess":true,"EnableAutomaticPortMapping":false}' >/dev/null
curl "${J[@]}" -X POST "$BASE/Startup/Complete" >/dev/null

login() { curl "${J[@]}" -X POST "$BASE/Users/AuthenticateByName" -d "{\"Username\":\"$1\",\"Pw\":\"$2\"}" | jq -r .AccessToken; }
ADMIN=$(login admin "$PW_ADMIN")
[[ ${#ADMIN} -gt 10 ]] && ok "admin login" || { bad "admin login"; exit 1; }
A=(-s -H "Content-Type: application/json" -H "Authorization: $CLIENT, Token=\"$ADMIN\"")

for lib in training other; do
  curl "${A[@]}" -X POST "$BASE/Library/VirtualFolders?name=$lib&collectionType=tvshows&paths=/media/$lib&refreshLibrary=false" \
    -d '{"LibraryOptions":{"EnableRealtimeMonitor":false,"EnableInternetProviders":false}}' >/dev/null
done
curl "${A[@]}" -X POST "$BASE/Library/Refresh" >/dev/null
for _ in $(seq 1 60); do
  n=$(curl "${A[@]}" "$BASE/Items?Recursive=true&IncludeItemTypes=Episode" | jq -r '.TotalRecordCount // 0')
  [[ $n -ge 4 ]] && break; sleep 2
done
check "library scanned (episodes)" "$n" 4

lib_id() { curl "${A[@]}" "$BASE/Library/VirtualFolders" | jq -r --arg n "$1" '.[] | select(.Name==$n) | .ItemId'; }
TRAINING=$(lib_id training); OTHER=$(lib_id other)
cfg=$(curl "${A[@]}" "$BASE/Plugins/$GUID/Configuration")
[[ -n $cfg && $cfg != null ]] && ok "plugin loaded (configuration readable)" || bad "plugin loaded"
echo "$cfg" | jq --arg t "$TRAINING" '.EnabledLibraryIds=[$t]' | curl "${A[@]}" -X POST "$BASE/Plugins/$GUID/Configuration" -d @- >/dev/null

newuser() {   # name, downloads(true/false), all-folders(true/false), [folder id]
  local id; id=$(curl "${A[@]}" -X POST "$BASE/Users/New" -d "{\"Name\":\"$1\",\"Password\":\"$PW_USER\"}" | jq -r .Id)
  curl "${A[@]}" "$BASE/Users/$id" | jq --argjson d "$2" --argjson a "$3" --arg f "${4:-}" \
    '.Policy | .EnableContentDownloading=$d | .EnableAllFolders=$a | (if $a then . else .EnabledFolders=[$f] end)' |
    curl "${A[@]}" -X POST "$BASE/Users/$id/Policy" -d @- >/dev/null
  echo "$id"
}
READER_ID=$(newuser reader false true); DL_ID=$(newuser downloader true true); OUTSIDE_ID=$(newuser outsider true false "$OTHER")
READER=$(login reader "$PW_USER"); DL=$(login downloader "$PW_USER"); OUTSIDE=$(login outsider "$PW_USER")
as() { local t=$1; shift; curl -s -H "Authorization: $CLIENT, Token=\"$t\"" "$@"; }

# Look items up by path: test servers may still rename them from online metadata.
item() { curl "${A[@]}" "$BASE/Items?Recursive=true&IncludeItemTypes=$1&Fields=Path" | jq -r --arg p "$2" '.Items[] | select(.Path==$p) | .Id'; }
SERIES=$(item Series "/media/training/Course A"); SERIESB=$(item Series "/media/other/Course B")
SEASON1=$(item Season "/media/training/Course A/Season 1")
E1=$(item Episode "/media/training/Course A/Season 1/S01E01 - Lesson One.mp4")
E2=$(item Episode "/media/training/Course A/Season 1/S01E02 - Lesson Two.mp4")
E3=$(item Episode "/media/training/Course A/Season 2/S02E01 - Sneaky.mp4")
for v in SERIES SERIESB SEASON1 E1 E2 E3; do [[ -n ${!v} ]] || bad "test item $v not found"; done
info() { as "$1" "$BASE/AdditionalMaterial/Items/$2"; }

# ---- lookups -------------------------------------------------------------------
check "course archive found"                "$(info "$ADMIN" "$SERIES" | jq -r '.FileName')" additional-material.zip
check "section archive found"               "$(info "$ADMIN" "$SEASON1" | jq -r '.FileName')" additional-material.zip
check "lesson archive found"                "$(info "$ADMIN" "$E1" | jq -r '.FileName')" "S01E01 - Lesson One.material.zip"
check ".7z is ignored"                      "$(info "$ADMIN" "$E2" | jq -r '.Available')" false
check "link escaping library refused"       "$(info "$ADMIN" "$E3" | jq -r '.Available')" false
check "library not enabled: ignored"        "$(info "$ADMIN" "$SERIESB" | jq -r '.Available')" false
check "style defaults to one color"      "$(info "$ADMIN" "$SERIES" | jq -r '.ButtonStyle + " " + .AccentColor')" "mono #00A4DC"
curl "${A[@]}" "$BASE/Plugins/$GUID/Configuration" | jq '.ButtonStyle="color" | .AccentColor="#DB781B"' | curl "${A[@]}" -X POST "$BASE/Plugins/$GUID/Configuration" -d @- >/dev/null
check "two-color style and accent saved"  "$(info "$ADMIN" "$SERIES" | jq -r '.ButtonStyle + " " + .AccentColor')" "color #DB781B"
curl "${A[@]}" "$BASE/Plugins/$GUID/Configuration" | jq '.AccentColor="red;}<script>"' | curl "${A[@]}" -X POST "$BASE/Plugins/$GUID/Configuration" -d @- >/dev/null
check "invalid accent falls back"         "$(info "$ADMIN" "$SERIES" | jq -r '.AccentColor')" "#00A4DC"
curl "${A[@]}" "$BASE/Plugins/$GUID/Configuration" | jq '.AccentColor="#DB781B"' | curl "${A[@]}" -X POST "$BASE/Plugins/$GUID/Configuration" -d @- >/dev/null
check "admin may download"                  "$(info "$ADMIN" "$SERIES" | jq -r '.CanDownload')" true
check "no-download user: CanDownload false" "$(info "$READER" "$SERIES" | jq -r '.CanDownload')" false
check "no-download user: link refused"      "$(as "$READER" -o /dev/null -w '%{http_code}' -X POST "$BASE/AdditionalMaterial/Items/$SERIES/Link")" 403
check "user without library access: 404"    "$(as "$OUTSIDE" -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Items/$SERIES")" 404
check "unauthenticated lookup: 401"         "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Items/$SERIES")" 401

# ---- downloads -----------------------------------------------------------------
TOKEN=$(as "$DL" -X POST "$BASE/AdditionalMaterial/Items/$E1/Link" | jq -r .Token)
code=$(curl -s -D "$WORK/h" -o "$WORK/got" -w '%{http_code}' "$BASE/AdditionalMaterial/Download/$TOKEN")
check "download: 200" "$code" 200
check "download: bytes match the file" "$(sha256sum < "$WORK/got" | cut -c1-64)" "$(sha256sum < "$T/Season 1/S01E01 - Lesson One.material.zip" | cut -c1-64)"
grep -qi '^content-disposition: attachment' "$WORK/h" && ok "download: sent as attachment" || bad "download: sent as attachment"
grep -qi '^content-type: application/zip' "$WORK/h" && ok "download: content type zip" || bad "download: content type zip"
check "download: range request 206" "$(curl -s -o /dev/null -w '%{http_code}' -r 0-9 "$BASE/AdditionalMaterial/Download/$TOKEN")" 206
# Flip a character in the middle (it carries payload bits), and try a non-canonical last character.
c=${TOKEN:20:1}; [[ $c == A ]] && r=B || r=A
check "tampered token: 404" "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Download/${TOKEN:0:20}$r${TOKEN:21}")" 404
last=${TOKEN: -1}; alt=$(printf '%s' "$last" | tr 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-' 'BADCFEHGJILKNMPORQTSVUXWZYbadcfehgjilknmporqtsvuxwzy1032547698-_')
check "non-canonical token: 404" "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Download/${TOKEN%?}$alt")" 404
check "garbage token: 404"  "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Download/not-a-token")" 404
# Revoke downloads after the link was issued: the link must stop working.
curl "${A[@]}" "$BASE/Users/$DL_ID" | jq '.Policy | .EnableContentDownloading=false' | curl "${A[@]}" -X POST "$BASE/Users/$DL_ID/Policy" -d @- >/dev/null
check "revoked permission: existing link dies" "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Download/$TOKEN")" 404

# ---- web client ----------------------------------------------------------------
check "script served" "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/web/additional-material.js")" 200
check "strings: English"           "$(curl -s "$BASE/AdditionalMaterial/web/strings" | jq -r '."config.save"')" Save
check "strings: unknown language falls back" "$(curl -s "$BASE/AdditionalMaterial/web/strings?lang=xx-YY" | jq -r '."config.save"')" Save
check "strings: bad tag ignored"   "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/web/strings?lang=../../etc")" 200
if [[ -n $FT ]]; then
  sleep 5
  curl -s "$BASE/web/index.html" | grep -q 'plugin="AdditionalMaterial"' && ok "index.html carries the script (File Transformation)" || bad "index.html carries the script"
fi

echo; echo "passed $pass, failed $fail"; echo "test server left running on $BASE; '$0 --cleanup' removes it"
[[ $fail -eq 0 ]]
