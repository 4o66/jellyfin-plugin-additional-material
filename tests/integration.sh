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
VT=am-test-vt            # a fake VirusTotal (tests/fake_virustotal.py) the server reaches by name
NET=am-test-net
VT_IMAGE=${VT_IMAGE:-python:3-alpine}
PORT=${PORT:-18096}
WORK=${WORK:-/tmp/am-test}
GUID=10121f36-d2e1-4b8d-96c4-b2cc720880f3
BASE="http://127.0.0.1:$PORT"
CLIENT='MediaBrowser Client="am-test", Device="am-test", DeviceId="am-test-1", Version="1.0"'

if [[ ${1:-} == --cleanup ]]; then
  docker rm -f "$NAME" "$VT" >/dev/null 2>&1
  docker network rm "$NET" >/dev/null 2>&1
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

docker rm -f "$NAME" "$VT" >/dev/null 2>&1
docker network rm "$NET" >/dev/null 2>&1
rm -rf "$WORK"
mkdir -p "$WORK"/{config/plugins,cache,media/training,media/other}

# ---- sample media -----------------------------------------------------------
T="$WORK/media/training/Course A"
mkdir -p "$T/Season 1" "$T/Season 2" "$WORK/media/other/Course B/Season 1" "$WORK/media/outside/Season 3"
docker run --rm --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg -v "$WORK/media:/m" "$IMAGE" \
  -loglevel error -f lavfi -i testsrc=duration=3:size=320x240:rate=10 -c:v libx264 -pix_fmt yuv420p /m/sample.mp4
for f in "Season 1/S01E01 - Lesson One" "Season 1/S01E02 - Lesson Two" "Season 2/S02E01 - Sneaky"; do
  cp "$WORK/media/sample.mp4" "$T/$f.mp4"
done
cp "$WORK/media/sample.mp4" "$WORK/media/other/Course B/Season 1/S01E01 - Other.mp4"
cp "$WORK/media/sample.mp4" "$WORK/media/outside/Season 3/S03E01 - Linked.mp4"
ln -s "../../outside/Season 3" "$T/Season 3"                      # folder link out of the library
rm "$WORK/media/sample.mp4"
mk() { local dir=$1 name=$2; echo "material for $name" > "$WORK/notes.txt"; (cd "$WORK" && 7z a -bso0 -bsp0 "$dir/$name" notes.txt); }
mk "$T" additional-material.zip                              # course
mk "$T/Season 1" additional-material.zip                     # section
# lesson: a folder, a nested zip and a removal note, for the Contents view
L="$WORK/lesson"; mkdir -p "$L/slides"
echo "material for S01E01" > "$L/notes.txt"; echo "intro slides" > "$L/slides/intro.txt"
echo "tool.exe was removed" > "$L/tool.exe.REMOVED.txt"; echo "lab one" > "$WORK/lab1.txt"
(cd "$WORK" && 7z a -bso0 -bsp0 "$L/labs.zip" lab1.txt)
(cd "$L" && 7z a -bso0 -bsp0 "$T/Season 1/S01E01 - Lesson One.material.zip" notes.txt slides tool.exe.REMOVED.txt labs.zip)
mk "$T/Season 1" "S01E02 - Lesson Two.material.7z"           # .7z is not recognized in this version
ln -s /etc/hostname "$T/Season 2/S02E01 - Sneaky.material.zip"  # link escaping the library: refused
mk "$WORK/media/other/Course B" additional-material.zip      # library not enabled: ignored
mk "$WORK/media/outside/Season 3" "S03E01 - Linked.material.zip"  # reached only through the folder link: refused
# Course C has no archives at all: the plugin builds them when building is on.
TC="$WORK/media/training/Course C"; mkdir -p "$TC/Season 1"
cp "$T/Season 1/S01E01 - Lesson One.mp4" "$TC/Season 1/S01E01 - Build Lesson.mp4"
cp "$T/Season 1/S01E01 - Lesson One.mp4" "$TC/Season 1/S01E02 - Two Files.mp4"
printf '%%PDF-1.4\nlesson one handout\n' > "$TC/Season 1/S01E01 - Build Lesson.pdf"     # one file: handed out as is
echo "lesson two notes" > "$TC/Season 1/S01E02 - Two Files.txt"
printf 'PK\003\004 macro document' > "$TC/Season 1/S01E02 - Two Files.docm"         # replaced by a note
echo "section notes" > "$TC/Season 1/notes.txt"; echo "# slides" > "$TC/Season 1/section-slides.md"
printf 'https://freecourseweb.com\nhttps://devcourseweb.com\n' > "$TC/Season 1/Bonus Resources.txt"   # advert: left out
echo "course readme" > "$TC/readme.txt"
echo '<script type="text/javascript">window.location = "https://www.udemy.com/course/x/quiz/3";</script>' > "$TC/Season 1/3. Practice Quiz.html"   # redirect: a link
TD="$WORK/media/training/Course D"; mkdir -p "$TD/Season 1"     # mounted read-only in the server: built archives go to the cache
cp "$T/Season 1/S01E01 - Lesson One.mp4" "$TD/Season 1/S01E01 - Read Only.mp4"
echo "read-only notes" > "$TD/Season 1/S01E01 - Read Only.txt"; echo "more" > "$TD/Season 1/S01E01 - Read Only.md"
rm "$WORK/notes.txt"

# ---- plugins ----------------------------------------------------------------
mkdir -p "$WORK/config/plugins/Additional Material_1.6.1.0"
cp "$OUT/Jellyfin.Plugin.AdditionalMaterial.dll" "$OUT/Tomlyn.dll" "$WORK/config/plugins/Additional Material_1.6.1.0/"
[[ -n $FT ]] && cp -r "$FT" "$WORK/config/plugins/"
# A signing key left readable by others (as 1.2.2 and earlier wrote it on Windows) must be replaced.
KEYFILE="$WORK/config/plugins/Jellyfin.Plugin.AdditionalMaterial/signing.key"
mkdir -p "${KEYFILE%/*}"; head -c 32 /dev/urandom > "$KEYFILE"; chmod 644 "$KEYFILE"
SEEDSUM=$(sha256sum < "$KEYFILE" | cut -c1-64)

docker network create "$NET" >/dev/null
cp "$(dirname "$0")/fake_virustotal.py" "$WORK/fake_virustotal.py"
docker run -d --name "$VT" --network "$NET" -v "$WORK/fake_virustotal.py:/fake_virustotal.py:ro" "$VT_IMAGE" python /fake_virustotal.py 8000 >/dev/null
docker run -d --name "$NAME" --network "$NET" -e AM_VT_BASE="http://$VT:8000" -p "127.0.0.1:$PORT:8096" \
  -v "$WORK/config:/config" -v "$WORK/cache:/cache" -v "$WORK/media:/media" \
  -v "$WORK/media/training/Course D:/media/training/Course D:ro" "$IMAGE" >/dev/null   # Course D: a folder the server cannot write
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
  [[ $n -ge 8 ]] && break; sleep 2
done
check "library scanned (episodes)" "$n" 8

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
E4=$(item Episode "/media/training/Course A/Season 3/S03E01 - Linked.mp4")
for v in SERIES SERIESB SEASON1 E1 E2 E3 E4; do [[ -n ${!v} ]] || bad "test item $v not found"; done
info() { as "$1" "$BASE/AdditionalMaterial/Items/$2"; }

# ---- lookups -------------------------------------------------------------------
check "course archive found"                "$(info "$ADMIN" "$SERIES" | jq -r '.FileName')" additional-material.zip
check "section archive found"               "$(info "$ADMIN" "$SEASON1" | jq -r '.FileName')" additional-material.zip
check "lesson archive found"                "$(info "$ADMIN" "$E1" | jq -r '.FileName')" "S01E01 - Lesson One.material.zip"
check ".7z is ignored"                      "$(info "$ADMIN" "$E2" | jq -r '.Available')" false
check "link escaping library refused"       "$(info "$ADMIN" "$E3" | jq -r '.Available')" false
check "zip under a linked folder refused"     "$(info "$ADMIN" "$E4" | jq -r '.Available')" false
check "zip under a linked folder: no link"    "$(as "$ADMIN" -o /dev/null -w '%{http_code}' -X POST "$BASE/AdditionalMaterial/Items/$E4/Link")" 404
check "library not enabled: ignored"        "$(info "$ADMIN" "$SERIESB" | jq -r '.Available')" false
check "style defaults to two colors"     "$(info "$ADMIN" "$SERIES" | jq -r '.ButtonStyle + " " + .AccentColor')" "color #00A4DC"
check "display settings defaults"        "$(as "$ADMIN" "$BASE/AdditionalMaterial/web/settings" | jq -c '[.ButtonStyle,.ShowOnParents,.ShowOnCards,.ShowInLists]')" '["color","all",true,true]'
cfg() { curl "${A[@]}" "$BASE/Plugins/$GUID/Configuration" | jq "$@" | curl "${A[@]}" -X POST "$BASE/Plugins/$GUID/Configuration" -d @- >/dev/null; }
cfg '.ButtonStyle="mono"';  check "one-color style saved" "$(info "$ADMIN" "$SERIES" | jq -r '.ButtonStyle')" mono
cfg '.ButtonStyle="color" | .AccentColor="#DB781B"'
check "two-color style and accent saved"  "$(info "$ADMIN" "$SERIES" | jq -r '.ButtonStyle + " " + .AccentColor')" "color #DB781B"

# ---- listings (tree) and batch status ---------------------------------------------------------
tree() { as "$1" "$BASE/AdditionalMaterial/Items/$2/Tree"; }
check "tree: course lists self + section + lesson" "$(tree "$ADMIN" "$SERIES" | jq -c '[(.Self!=null), [.Groups[].Items[] | .Level]]')" '[true,["section","lesson"]]'
check "tree: rows carry names and item ids"        "$(tree "$ADMIN" "$SERIES" | jq -r '.Groups[0].Items[1].ItemId')" "$E1"
check "tree: section lists its lesson"             "$(tree "$ADMIN" "$SEASON1" | jq -c '[(.Self!=null), [.Groups[].Items[] | .Level]]')" '[true,["lesson"]]'
check "tree: lesson has only itself"               "$(tree "$ADMIN" "$E1" | jq -c '[(.Self!=null), (.Groups|length)]')" '[true,0]'
check "tree: type and name"                        "$(tree "$ADMIN" "$SERIES" | jq -r '.Type')" Series
st() { as "$1" -X POST -H "Content-Type: application/json" "$BASE/AdditionalMaterial/Items/Status" -d "{\"Ids\":[\"$SERIES\",\"$SEASON1\",\"$E1\",\"$E3\",\"$SERIESB\"]}"; }
check "status: own and below counts"  "$(st "$ADMIN" | jq -c --arg s "$SERIES" --arg n "$SEASON1" --arg e "$E1" '[.[$s].Own,.[$s].Below,.[$n].Below,.[$e].Below]')" '[true,2,1,0]'
check "status: items without material omitted" "$(st "$ADMIN" | jq -c --arg a "$E3" --arg b "$SERIESB" '[has($a),has($b)]')" '[false,false]'
check "status: no access, nothing returned"     "$(st "$OUTSIDE" | jq 'length')" 0
cfg '.ShowOnParents="section"'; sleep 1
check "levels=section: course shows only its own" "$(tree "$ADMIN" "$SERIES" | jq '.Groups|length')" 0
check "levels=section: section still lists lesson" "$(tree "$ADMIN" "$SEASON1" | jq '[.Groups[].Items[]]|length')" 1
cfg '.ShowOnParents="item"'; sleep 1
check "levels=item: section shows only its own"   "$(tree "$ADMIN" "$SEASON1" | jq '.Groups|length')" 0
cfg '.ShowOnParents="all"'
check "outsider cannot list the course"           "$(as "$OUTSIDE" -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Items/$SERIES/Tree")" 404
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
check "signing key: readable-by-others key replaced" "$( [[ $(sha256sum < "$KEYFILE" | cut -c1-64) != "$SEEDSUM" ]] && echo yes)" yes
check "signing key: owner-only (600)" "$(stat -c %a "$KEYFILE")" 600
check "download: bytes match the file" "$(sha256sum < "$WORK/got" | cut -c1-64)" "$(sha256sum < "$T/Season 1/S01E01 - Lesson One.material.zip" | cut -c1-64)"
grep -qi '^content-disposition: attachment' "$WORK/h" && ok "download: sent as attachment" || bad "download: sent as attachment"
grep -qiE "^content-disposition: attachment; filename=\"?[^\"]+ - S01E01 - [^\"]+ - Additional Material\.zip" "$WORK/h" && ok "download: descriptive file name" || bad "download: descriptive file name ($(grep -i '^content-disposition' "$WORK/h" | tr -d '\r'))"
check "index.html is never answered 'not modified'" "$(curl -s -o /dev/null -w '%{http_code}' -H "If-Modified-Since: $(date -u -R)" "$BASE/web/index.html")" 200
grep -qi '^content-type: application/zip' "$WORK/h" && ok "download: content type zip" || bad "download: content type zip"
check "download: range request 206" "$(curl -s -o /dev/null -w '%{http_code}' -r 0-9 "$BASE/AdditionalMaterial/Download/$TOKEN")" 206
# Flip a character in the middle (it carries payload bits), and try a non-canonical last character.
c=${TOKEN:20:1}; [[ $c == A ]] && r=B || r=A
check "tampered token: 404" "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Download/${TOKEN:0:20}$r${TOKEN:21}")" 404
last=${TOKEN: -1}; alt=$(printf '%s' "$last" | tr 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-' 'BADCFEHGJILKNMPORQTSVUXWZYbadcfehgjilknmporqtsvuxwzy1032547698-_')
check "non-canonical token: 404" "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Download/${TOKEN%?}$alt")" 404
check "garbage token: 404"  "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Download/not-a-token")" 404
# ---- contents ------------------------------------------------------------------
cont() { as "$1" "$BASE/AdditionalMaterial/Items/$2/Contents"; }
check "contents: lesson files listed" "$(cont "$ADMIN" "$E1" | jq -c '[.Entries[].Path] | sort')" '["labs.zip","notes.txt","slides/intro.txt","tool.exe.REMOVED.txt"]'
check "contents: nested zip listed"   "$(cont "$ADMIN" "$E1" | jq -c '[.Entries[] | select(.Path=="labs.zip") | .Children[].Path]')" '["lab1.txt"]'
check "contents: sizes are real"      "$(cont "$ADMIN" "$E1" | jq -r '.Entries[] | select(.Path=="notes.txt") | .Size')" "$(wc -c < "$L/notes.txt" | tr -d ' ')"
check "contents: course archive"      "$(cont "$ADMIN" "$SERIES" | jq -c '[.Entries[].Path]')" '["notes.txt"]'
check "contents: no material: 404"    "$(as "$ADMIN" -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Items/$E3/Contents")" 404
check "contents: no access: 404"      "$(as "$OUTSIDE" -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Items/$E1/Contents")" 404
check "contents: unauthenticated 401" "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Items/$E1/Contents")" 401
check "contents: reader may list"     "$(cont "$READER" "$E1" | jq -c '[.CanDownload, (.Entries|length)]')" '[false,4]'
TOKEN2=$(as "$DL" -X POST "$BASE/AdditionalMaterial/Items/$E1/Link" | jq -r .Token)
code=$(curl -s -D "$WORK/h2" -o "$WORK/got2" -w '%{http_code}' "$BASE/AdditionalMaterial/Download/$TOKEN2?entry=slides%2Fintro.txt")
check "entry download: 200" "$code" 200
check "entry download: bytes match" "$(cat "$WORK/got2")" "intro slides"
grep -qiE '^content-disposition: attachment; filename="?intro\.txt' "$WORK/h2" && ok "entry download: attachment named after the file" || bad "entry download: attachment named after the file ($(grep -i '^content-disposition' "$WORK/h2" | tr -d '\r'))"
grep -qi '^content-type: application/octet-stream' "$WORK/h2" && ok "entry download: opaque content type" || bad "entry download: opaque content type"
check "entry download: file inside the nested zip" "$(curl -s "$BASE/AdditionalMaterial/Download/$TOKEN2?entry=labs.zip%21%2Flab1.txt")" "lab one"
check "entry download: no such entry 404"  "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Download/$TOKEN2?entry=nope.txt")" 404
check "entry download: folder is not a file" "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Download/$TOKEN2?entry=slides")" 404
check "entry download: path tricks 404"    "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Download/$TOKEN2?entry=..%2F..%2Fetc%2Fpasswd")" 404
check "entry download: needs a valid token" "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Download/not-a-token?entry=notes.txt")" 404
cfg '.ListNestedZips=false'
check "nested listing off: shown as a plain file" "$(cont "$ADMIN" "$E1" | jq -c '[.Entries[] | select(.Path=="labs.zip") | .Children]')" '[null]'
check "nested listing off: inner file not served" "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Download/$TOKEN2?entry=labs.zip%21%2Flab1.txt")" 404
cfg '.ListNestedZips=true'
check "index refresh: readers refused"   "$(as "$READER" -o /dev/null -w '%{http_code}' -X POST "$BASE/AdditionalMaterial/Index/Refresh")" 403
check "index refresh: admin accepted"    "$(as "$ADMIN" -o /dev/null -w '%{http_code}' -X POST "$BASE/AdditionalMaterial/Index/Refresh")" 202
for _ in $(seq 20); do [[ $(as "$ADMIN" "$BASE/AdditionalMaterial/Index/Status" | jq -r .Running) == false ]] && break; sleep 1; done
check "index status: counts folders and zips" "$(as "$ADMIN" "$BASE/AdditionalMaterial/Index/Status" | jq -c '[(.Folders>0), (.Zips>0), (.FinishedUtc!=null)]')" '[true,true,true]'

# Revoke downloads after the link was issued: the link must stop working.
curl "${A[@]}" "$BASE/Users/$DL_ID" | jq '.Policy | .EnableContentDownloading=false' | curl "${A[@]}" -X POST "$BASE/Users/$DL_ID/Policy" -d @- >/dev/null
check "revoked permission: existing link dies" "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Download/$TOKEN")" 404
check "revoked permission: entry links die too" "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Download/$TOKEN2?entry=notes.txt")" 404

# ---- folder index -----------------------------------------------------------------
# Pages are answered from the plugin's index; only downloads look at the disk.
refresh() {   # run "Refresh additional material" and wait for it
  local id; id=$(curl "${A[@]}" "$BASE/ScheduledTasks" | jq -r '.[] | select(.Key=="Jellyfin.Plugin.AdditionalMaterial.IndexRefresh") | .Id')
  curl "${A[@]}" -X POST "$BASE/ScheduledTasks/Running/$id" >/dev/null
  for _ in $(seq 30); do sleep 1; [[ $(curl "${A[@]}" "$BASE/ScheduledTasks/$id" | jq -r .State) == Idle ]] && break; done
  echo "$id"
}
logs=$(docker logs "$NAME" 2>&1)   # not piped into grep -q: with pipefail, SIGPIPE fails the pipeline
[[ $logs == *"Additional Material: indexed"* ]] && ok "index built at startup" || bad "index built at startup"
check "refresh task listed" "$(curl "${A[@]}" "$BASE/ScheduledTasks" | jq -r '.[] | select(.Key=="Jellyfin.Plugin.AdditionalMaterial.IndexRefresh") | .Name')" "Refresh additional material"
L2="$T/Season 1/S01E02 - Lesson Two.material.zip"; L1="$T/Season 1/S01E01 - Lesson One.material.zip"
cp "$L1" "$L2"
check "new zip: not seen before a refresh"  "$(info "$ADMIN" "$E2" | jq -r '.Available')" false
refresh >/dev/null
check "new zip: seen after the refresh task" "$(info "$ADMIN" "$E2" | jq -r '.Available')" true
mv "$L1" "$WORK/held.zip"
check "deleted zip: page still shows it"    "$(info "$ADMIN" "$E1" | jq -r '.Available')" true
check "deleted zip: link refused (disk checked)" "$(as "$ADMIN" -o /dev/null -w '%{http_code}' -X POST "$BASE/AdditionalMaterial/Items/$E1/Link")" 404
cfg '.IndexRefreshMinutes=0'
info "$ADMIN" "$E1" >/dev/null; sleep 2
check "stale entry re-checked in the background" "$(info "$ADMIN" "$E1" | jq -r '.Available')" false
# Put the media back as it was, for the browser test.
mv "$WORK/held.zip" "$L1"; rm "$L2"
cfg '.IndexRefreshMinutes=10'; refresh >/dev/null
check "index restored" "$(info "$ADMIN" "$E1" | jq -r '.Available') $(info "$ADMIN" "$E2" | jq -r '.Available')" "true false"

# ---- building archives ------------------------------------------------------------------
EC1=$(item Episode "/media/training/Course C/Season 1/S01E01 - Build Lesson.mp4")
EC2=$(item Episode "/media/training/Course C/Season 1/S01E02 - Two Files.mp4")
SEASONC=$(item Season "/media/training/Course C/Season 1"); SERIESC=$(item Series "/media/training/Course C")
for v in EC1 EC2 SEASONC SERIESC; do [[ -n ${!v} ]] || bad "test item $v not found"; done
check "building off: nothing offered for a course without archives" "$(info "$ADMIN" "$EC1" | jq -r .Available)" false
bstat() { as "$ADMIN" "$BASE/AdditionalMaterial/Build/Status"; }
wait_planned() { for _ in $(seq 60); do local st; st=$(bstat); [[ $(jq -r .Running <<<"$st") == false && $(jq -r .Planned <<<"$st") -gt 0 ]] && return; sleep 1; done; }
cfg '.BuildArchives=true | .BuildInBackground=false | .BuiltArchiveLocation="beside" | .ShowLeftOutFiles="everyone"'
as "$ADMIN" -o /dev/null -X POST "$BASE/AdditionalMaterial/Index/Refresh"; sleep 2; wait_planned
check "building on: archives planned" "$( [[ $(bstat | jq -r .Planned) -ge 3 ]] && echo yes)" yes
check "a course with its own archives is left alone" "$( [[ $(bstat | jq -r .CoursesLeftAlone) -ge 1 ]] && echo yes)" yes
check "left alone: no archive invented for Course A's Lesson Two" "$(info "$ADMIN" "$E2" | jq -r .Available)" false
check "one-file material: offered as the file itself" "$(info "$ADMIN" "$EC1" | jq -c '[.FileName,.Format]')" '["S01E01 - Build Lesson.pdf","pdf"]'
check "nothing written before a download" "$(ls "$TC/Season 1" | grep -c 'material.zip$')" 0
TC1=$(as "$ADMIN" -X POST "$BASE/AdditionalMaterial/Items/$EC1/Link" | jq -r .Token)
curl -s -D "$WORK/hc" -o "$WORK/gotc" "$BASE/AdditionalMaterial/Download/$TC1"
check "one-file download: the original file" "$(sha256sum < "$WORK/gotc" | cut -c1-64)" "$(sha256sum < "$TC/Season 1/S01E01 - Build Lesson.pdf" | cut -c1-64)"
grep -qi 'filename="\?S01E01 - Build Lesson.pdf' "$WORK/hc" && ok "one-file download: under its own name" || bad "one-file download: under its own name ($(grep -i '^content-disposition' "$WORK/hc" | tr -d '\r'))"
check "planned lesson archive offered" "$(info "$ADMIN" "$EC2" | jq -r .FileName)" "S01E02 - Two Files.material.zip"
check "planned contents: files, the macro document as a note" "$(cont "$ADMIN" "$EC2" | jq -c '[.Entries[] | select(.LeftOut|not) | .Path] | sort')" '["S01E02 - Two Files.docm.REMOVED.txt","S01E02 - Two Files.txt"]'
check "planned contents: the removal reason" "$(cont "$ADMIN" "$EC2" | jq -r '.Entries[] | select(.Path|endswith(".REMOVED.txt")) | .Reason')" ".docm files are executable or script content"
check "section: its file and the left-out advert" "$(cont "$ADMIN" "$SEASONC" | jq -c '[.Entries[] | [.Path, .LeftOut]]')" '[["notes.txt",false],["section-slides.md",false],["3. Practice Quiz.html",true],["Bonus Resources.txt",true]]'
check "left-out files carry the rule's reason" "$(cont "$ADMIN" "$SEASONC" | jq -r '.Entries[] | select(.Path=="Bonus Resources.txt") | .Reason')" "link-only text file (advert) [rule link-only-text]"
check "redirect placeholder: offered as a link to the site" "$(cont "$ADMIN" "$SEASONC" | jq -c '[.Entries[] | select(.Path=="3. Practice Quiz.html") | .Link, (.Link==null)]')" '["https://www.udemy.com/course/x/quiz/3",false]'
check "an advert carries no link" "$(cont "$ADMIN" "$SEASONC" | jq -r '.Entries[] | select(.Path=="Bonus Resources.txt") | .Link')" null
cfg '.ShowLeftOutFiles="admins"'
check "left-out files: admins only (reader)" "$(cont "$READER" "$SEASONC" | jq '[.Entries[] | select(.LeftOut)] | length')" 0
check "left-out files: admins only (admin)"  "$(cont "$ADMIN" "$SEASONC" | jq '[.Entries[] | select(.LeftOut)] | length')" 2
cfg '.ShowLeftOutFiles="nobody"'
check "left-out files: nobody" "$(cont "$ADMIN" "$SEASONC" | jq '[.Entries[] | select(.LeftOut)] | length')" 0
cfg '.ShowLeftOutFiles="everyone"'
check "course: the readme" "$(cont "$ADMIN" "$SERIESC" | jq -c '[.Entries[].Path]')" '["readme.txt"]'
TC2=$(as "$ADMIN" -X POST "$BASE/AdditionalMaterial/Items/$EC2/Link" | jq -r .Token)
ZC="$TC/Season 1/S01E02 - Two Files.material.zip"
curl -s -o "$WORK/zc1" "$BASE/AdditionalMaterial/Download/$TC2"
check "first download builds the archive beside the video" "$( [[ -f $ZC ]] && echo yes)" yes
check "download is the built archive" "$(sha256sum < "$WORK/zc1" | cut -c1-64)" "$(sha256sum < "$ZC" | cut -c1-64)"
check "built archive: entry names" "$(7z l -ba -slt "$ZC" | sed -n 's/^Path = //p' | sort | tr '\n' '|')" "S01E02 - Two Files.docm.REMOVED.txt|S01E02 - Two Files.txt|"
7z e -so "$ZC" "S01E02 - Two Files.docm.REMOVED.txt" 2>/dev/null | grep -q '^REMOVED: S01E02 - Two Files.docm' && ok "built archive: the note, as the script writes it" || bad "built archive: the note"
m1=$(stat -c %Y "$ZC"); sleep 1; curl -s -o "$WORK/zc2" "$BASE/AdditionalMaterial/Download/$TC2"
check "second download: not rebuilt" "$(stat -c %Y "$ZC")" "$m1"
check "entry download from a built archive: the original file" "$(curl -s "$BASE/AdditionalMaterial/Download/$TC2?entry=S01E02%20-%20Two%20Files.txt")" "lesson two notes"
curl -s "$BASE/AdditionalMaterial/Download/$TC2?entry=S01E02%20-%20Two%20Files.docm.REMOVED.txt" | grep -q '^REMOVED: ' && ok "entry download: the note for a removed file" || bad "entry download: the note for a removed file"
check "entry download: a left-out file is not served" "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Download/$TC2?entry=Bonus%20Resources.txt")" 404
echo "lesson two notes, revised" > "$TC/Season 1/S01E02 - Two Files.txt"; sleep 1
curl -s -o /dev/null "$BASE/AdditionalMaterial/Download/$TC2"
check "changed file: archive rebuilt" "$(7z e -so "$ZC" "S01E02 - Two Files.txt" 2>/dev/null)" "lesson two notes, revised"
ED1=$(item Episode "/media/training/Course D/Season 1/S01E01 - Read Only.mp4")
TD1=$(as "$ADMIN" -X POST "$BASE/AdditionalMaterial/Items/$ED1/Link" | jq -r .Token)
check "folder the server cannot write: still downloads" "$(curl -s -o "$WORK/zd" -w '%{http_code}' "$BASE/AdditionalMaterial/Download/$TD1")" 200
check "folder the server cannot write: built in the cache" "$(7z l -ba -slt "$WORK/zd" | sed -n 's/^Path = //p' | sort | tr '\n' '|')" "S01E01 - Read Only.md|S01E01 - Read Only.txt|"
check "folder the server cannot write: nothing written there" "$(ls "$TD/Season 1" | grep -c 'zip$')" 0
check "rebuild: readers refused" "$(as "$READER" -o /dev/null -w '%{http_code}' -X POST "$BASE/AdditionalMaterial/Build/Rebuild")" 403
cfg '.BuiltArchiveLocation="cache"'
as "$ADMIN" -o /dev/null -X POST "$BASE/AdditionalMaterial/Build/Rebuild"; sleep 1
for _ in $(seq 60); do [[ $(bstat | jq -r .Running) == false ]] && break; sleep 1; done
check "cache location: the copy beside the video is removed" "$( [[ -f $ZC ]] && echo yes || echo no)" no
check "cache location: still downloads" "$(curl -s "$BASE/AdditionalMaterial/Download/$TC2" -o "$WORK/zc3" -w '%{http_code}')" 200
check "cache location: the media folder holds no built archives" "$(ls "$TC" "$TC/Season 1" | grep -c 'material.zip$\|additional-material.zip$')" 0
cfg '.BuiltArchiveLocation="beside" | .BuildInBackground=true'
as "$ADMIN" -o /dev/null -X POST "$BASE/AdditionalMaterial/Index/Refresh"; sleep 2
for _ in $(seq 90); do st=$(bstat); [[ $(jq -r .Running <<<"$st") == false && $(jq -r .UpToDate <<<"$st") == $(jq -r .Planned <<<"$st") ]] && break; sleep 1; done
check "background: every planned archive built" "$(bstat | jq -c '[.Planned == .UpToDate, .Failed]')" '[true,0]'
check "background: section archive beside its videos" "$( [[ -f "$TC/Season 1/additional-material.zip" ]] && echo yes)" yes
check "background: no archive for one-file material (lesson, course)" "$( [[ -f "$TC/Season 1/S01E01 - Build Lesson.material.zip" || -f "$TC/additional-material.zip" ]] && echo yes || echo no)" no
check "background: only the unwritable folder's archive stays cached" "$(ls "$WORK/config/plugins/Jellyfin.Plugin.AdditionalMaterial/archives" 2>/dev/null | wc -l | tr -d ' ')" 1
# A course added while the server runs is planned (and built) without a re-read or a full scan.
curl "${A[@]}" "$BASE/System/Configuration" | jq '.LibraryMonitorDelay=1' | curl "${A[@]}" -X POST "$BASE/System/Configuration" -d @- >/dev/null
full_plans=$(docker logs "$NAME" 2>&1 | grep -c "Additional Material: planned [0-9]* archives in")
TE="$WORK/media/training/Course E"; mkdir -p "$TE/Season 1"
cp "$T/Season 1/S01E01 - Lesson One.mp4" "$TE/Season 1/S01E01 - Late Lesson.mp4"
echo "late notes" > "$TE/Season 1/S01E01 - Late Lesson.txt"; echo "# late" > "$TE/Season 1/S01E01 - Late Lesson.md"
curl "${A[@]}" -X POST "$BASE/Library/Media/Updated" -d '{"Updates":[{"Path":"/media/training/Course E","UpdateType":"Created"}]}' >/dev/null
EE=""; for _ in $(seq 60); do EE=$(item Episode "/media/training/Course E/Season 1/S01E01 - Late Lesson.mp4"); [[ -n $EE ]] && break; sleep 2; done
check "new course: Jellyfin added it" "$( [[ -n $EE ]] && echo yes)" yes
got=false; for _ in $(seq 45); do [[ $(info "$ADMIN" "$EE" | jq -r .Available) == true && -f "$TE/Season 1/S01E01 - Late Lesson.material.zip" ]] && { got=true; break; }; sleep 2; done
check "new course: material offered and built within a minute, no re-read" "$got" true
check "new course: planned on its own, not by a full re-plan" "$(docker logs "$NAME" 2>&1 | grep -c "Additional Material: planned [0-9]* archives in")" "$full_plans"
docker logs "$NAME" 2>&1 | grep -q "planned /media/training/Course E again" && ok "new course: the watcher planned it" || bad "new course: the watcher planned it"

# ---- rules from the settings ------------------------------------------------------------------
rules() { as "$1" "$BASE/AdditionalMaterial/Rules"; }
rcheck() { jq -n --arg t "$1" --arg o "${2:-}" '{Text:$t, OriginalId:$o, OtherIds:[]}' | as "$ADMIN" -X POST -H "Content-Type: application/json" "$BASE/AdditionalMaterial/Rules/Check" -d @-; }
# Re-read, plan and (background building is on) build; waits for a build run that finished after the request.
replan() { local before st; before=$(bstat | jq -r .FinishedUtc); as "$ADMIN" -o /dev/null -X POST "$BASE/AdditionalMaterial/Index/Refresh"; for _ in $(seq 90); do sleep 1; st=$(bstat); [[ $(jq -r .Running <<<"$st") == false && $(jq -r .FinishedUtc <<<"$st") != "$before" ]] && break; done; }
reason_of() { cont "$ADMIN" "$SEASONC" | jq -r --arg p "$1" '.Entries[] | select(.Path==$p) | (if .LeftOut then .Reason else "kept" end)'; }
check "rules: the built-in ones listed, all on" "$(rules "$ADMIN" | jq -c '[length, all(.Enabled), any(.Custom)]')" '[7,true,false]'
check "rules: readers refused" "$(as "$READER" -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Rules")" 403
check "rules: check refused to readers" "$(as "$READER" -o /dev/null -w '%{http_code}' -X POST -H "Content-Type: application/json" "$BASE/AdditionalMaterial/Rules/Check" -d '{"Text":""}')" 403
MDRULE=$'id = "markdown-slides"
description = "Markdown slide sources"
action = "skip"
reason = "slide source"

[match]
names = ["*-slides.md"]

[[test]]
name = "section-slides.md"
expect = "match"

[[test]]
name = "notes.md"
expect = "no-match"
'
check "rule check: a valid rule, its tests run" "$(rcheck "$MDRULE" | jq -c '[.Ok, .Id, .Tests]')" '[true,"markdown-slides",2]'
check "rule check: the file must be named after a valid id" "$(rcheck "${MDRULE/markdown-slides/Markdown Slides}" | jq -c '[.Ok, (.Errors[0] | test("lowercase"))]')" '[false,true]'
check "rule check: a failing test case refuses it" "$(rcheck "${MDRULE/notes.md/notes-slides.md}" | jq -c '[.Ok, (.Errors[0] | test("expected no-match"))]')" '[false,true]'
check "rule check: unknown keys refused" "$(rcheck "${MDRULE/action/acton}" | jq -r .Ok)" false
check "before: the advert is left out by link-only-text" "$(reason_of "Bonus Resources.txt")" "link-only text file (advert) [rule link-only-text]"
check "before: the slides are kept" "$(reason_of "section-slides.md")" kept
cfg --arg r "$MDRULE" '.DisabledRules=["link-only-text"] | .CustomRules=[{Id:"markdown-slides", Text:$r}]'
replan
check "a turned-off rule no longer applies (the next rule catches the advert)" "$(reason_of "Bonus Resources.txt")" "release-group advert [rule release-group-advert-text]"
check "an added rule applies" "$(reason_of "section-slides.md")" "slide source [rule markdown-slides]"
check "rules: the added one listed as yours, the turned-off one off" "$(rules "$ADMIN" | jq -c '[length, (.[] | select(.Id=="markdown-slides") | .Custom), (.[] | select(.Id=="link-only-text") | .Enabled)]')" '[8,true,false]'
check "downloads follow the rules (only notes.txt is left: handed out as that file)" "$(info "$ADMIN" "$SEASONC" | jq -r .FileName)" notes.txt
as "$ADMIN" -o "$WORK/rules.zip" "$BASE/AdditionalMaterial/Rules/Export"
check "export: the rules that are on, as rule files" "$(7z l -ba -slt "$WORK/rules.zip" | sed -n 's/^Path = //p' | sort | tr '\n' ' ')" "jellyfin-chapter-sidecars.toml lesson-attachment-folders.toml markdown-slides.toml release-group-advert-text.toml release-group-adverts.toml shortcuts-and-system-files.toml udemy-redirect-placeholders.toml "
check "export: readers refused" "$(as "$READER" -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/Rules/Export")" 403
EDITED=$(rules "$ADMIN" | jq -r '.[] | select(.Id=="release-group-adverts") | .Text' | sed 's/"Bonus Resources.txt",/"Bonus Resources.txt", "Bonus*.txt",/')
cfg --arg r "$EDITED" '.DisabledRules=[] | .CustomRules=[{Id:"release-group-adverts", Text:$r}, {Id:"broken-rule", Text:"id = \"broken-rule\"\n"}]'
replan
check "an edited built-in rule replaces the original" "$(rules "$ADMIN" | jq -c '.[] | select(.Id=="release-group-adverts") | [.Custom, .BuiltIn, (.BuiltInText != .Text)]')" '[true,true,true]'
check "a broken rule from the settings is reported, not used" "$(rules "$ADMIN" | jq -c '.[] | select(.Id=="broken-rule") | [(.Error | test("required")), .Enabled]')" '[true,true]'
check "a broken rule does not stop planning" "$(reason_of "Bonus Resources.txt")" "link-only text file (advert) [rule link-only-text]"
docker logs "$NAME" 2>&1 | grep -q "rule broken-rule from the settings cannot be used" && ok "a broken rule is logged" || bad "a broken rule is logged"
cfg '.DisabledRules=[] | .CustomRules=[]'
replan
check "rules back to the built-in ones" "$(7z l -ba -slt "$TC/Season 1/additional-material.zip" | sed -n 's/^Path = //p' | sort | tr '\n' '|')" "notes.txt|section-slides.md|"

# ---- VirusTotal --------------------------------------------------------------------------------
vtstats() { docker exec "$VT" wget -qO- http://127.0.0.1:8000/stats; }
vttest() { jq -n --arg k "$1" '{Key:$k}' | as "$2" "${@:3}" -X POST -H "Content-Type: application/json" "$BASE/AdditionalMaterial/VirusTotal/Test" -d @-; }
check "VirusTotal key test: a good key" "$(vttest test-key "$ADMIN" | jq -r .Ok)" true
check "VirusTotal key test: a wrong key, with VirusTotal's reason" "$(vttest wrong-key "$ADMIN" | jq -c '[.Ok, (.Message | test("401"))]')" '[false,true]'
check "VirusTotal key test: readers refused" "$(vttest test-key "$READER" -o /dev/null -w '%{http_code}')" 403
TF="$WORK/media/training/Course F"; mkdir -p "$TF/Season 1"
for l in "S01E01 - Clean" "S01E02 - Bad" "S01E03 - Unknown"; do
  cp "$T/Season 1/S01E01 - Lesson One.mp4" "$TF/Season 1/$l.mp4"; echo "notes for $l" > "$TF/Season 1/$l.txt"
done
printf 'clean tool' > "$TF/Season 1/S01E01 - Clean.exe"; printf 'bad tool' > "$TF/Season 1/S01E02 - Bad.exe"; printf 'never seen' > "$TF/Season 1/S01E03 - Unknown.exe"
note() { 7z e -so "$TF/Season 1/$1.material.zip" "$1.exe.REMOVED.txt" 2>/dev/null | sed -n 's/^  Scan: *//p'; }
cfg '.VirusTotalApiKey="test-key" | .VirusTotalRequestsPerMinute=600 | .AllowCleanExecutables=false'
replan
got=""; for _ in $(seq 60); do got=$(note "S01E01 - Clean"); [[ $got == VirusTotal:* ]] && break; sleep 2; done
check "VirusTotal: a known file's answer, in its note" "$got" "VirusTotal: known, flagged by 0 of 70 engines"
for _ in $(seq 20); do [[ $(note "S01E02 - Bad") == VirusTotal:* && $(note "S01E03 - Unknown") == VirusTotal:* ]] && break; sleep 2; done
check "VirusTotal: a flagged file" "$(note "S01E02 - Bad")" "VirusTotal: FLAGGED by 12 engine(s) as malicious and 1 as suspicious, out of 70"
check "VirusTotal: a file it does not know" "$(note "S01E03 - Unknown")" "VirusTotal: not known to VirusTotal"
# Course C's macro document is blocked too, so it is looked up as well.
check "VirusTotal: each blocked file looked up once, by its SHA-256" "$(vtstats | jq -c '.lookups | sort')" "$({ for c in 'clean tool' 'bad tool' 'never seen'; do printf %s "$c" | sha256sum | cut -c1-64; done; sha256sum < "$TC/Season 1/S01E02 - Two Files.docm" | cut -c1-64; } | jq -R . | jq -sc 'sort')"
check "VirusTotal: nothing uploaded, nothing else asked" "$(vtstats | jq -c .other)" '[]'
replan
check "VirusTotal: answers remembered (no second lookup)" "$(vtstats | jq '.lookups | length')" 4
curl "${A[@]}" -X POST "$BASE/Library/Media/Updated" -d '{"Updates":[{"Path":"/media/training/Course F","UpdateType":"Created"}]}' >/dev/null
EF2=""; for _ in $(seq 60); do EF2=$(item Episode "/media/training/Course F/Season 1/S01E02 - Bad.mp4"); [[ -n $EF2 ]] && break; sleep 2; done
check "VirusTotal: the contents view marks a flagged file" "$(cont "$ADMIN" "$EF2" | jq -c '.Entries[] | select(.Path|endswith(".REMOVED.txt")) | [.Flagged, (.Scan|test("FLAGGED by 12")), .ScanLink]')" "[true,true,\"https://www.virustotal.com/gui/file/$(printf 'bad tool' | sha256sum | cut -c1-64)\"]"
cfg '.AllowCleanExecutables=true'
replan
check "allow clean: a file no engine flags is included as is" "$(7z l -ba -slt "$TF/Season 1/S01E01 - Clean.material.zip" | sed -n 's/^Path = //p' | sort | tr '\n' '|')" "S01E01 - Clean.exe|S01E01 - Clean.txt|"
check "allow clean: a flagged file is still a note" "$(7z l -ba -slt "$TF/Season 1/S01E02 - Bad.material.zip" | sed -n 's/^Path = //p' | sort | tr '\n' '|')" "S01E02 - Bad.exe.REMOVED.txt|S01E02 - Bad.txt|"
check "allow clean: an unknown file is still a note" "$(7z l -ba -slt "$TF/Season 1/S01E03 - Unknown.material.zip" | sed -n 's/^Path = //p' | sort | tr '\n' '|')" "S01E03 - Unknown.exe.REMOVED.txt|S01E03 - Unknown.txt|"
cfg '.VirusTotalApiKey="" | .AllowCleanExecutables=false | .VirusTotalRequestsPerMinute=4'
replan
check "no key: notes say not checked again" "$(note "S01E02 - Bad")" "not checked"
check "no key: no lookups" "$(vtstats | jq '.lookups | length')" 4

# ---- material added to an existing course, without a scan (folders watched) ---------------------
check "real-time monitoring off: no folders watched" "$(docker logs "$NAME" 2>&1 | grep -c 'watching [0-9]* folder(s) for changed material')" 0
curl "${A[@]}" "$BASE/Library/VirtualFolders" | jq --arg id "$TRAINING" '{Id: $id, LibraryOptions: (.[] | select(.ItemId==$id) | .LibraryOptions | .EnableRealtimeMonitor=true)}' |
  curl "${A[@]}" -X POST "$BASE/Library/VirtualFolders/LibraryOptions" -d @- >/dev/null
got=false; for _ in $(seq 50); do docker logs "$NAME" 2>&1 | grep -q 'watching 1 folder(s) for changed material: /media/training$' && { got=true; break; }; sleep 2; done
check "real-time monitoring on: the enabled library's folder is watched (the other is not enabled)" "$got" true
full_plans=$(docker logs "$NAME" 2>&1 | grep -c "Additional Material: planned [0-9]* archives in")
replans=$(docker logs "$NAME" 2>&1 | grep -c "planned /media/training/Course C again")
zentries() { 7z l -ba -slt "$ZC" 2>/dev/null | sed -n 's/^Path = //p' | sort | tr '\n' '|'; }
printf '%%PDF-1.4\nadded later\n' > "$TC/Season 1/S01E02 - Two Files.pdf"
got=""; for _ in $(seq 45); do got=$(zentries); [[ $got == *"Two Files.pdf"* ]] && break; sleep 2; done
check "added material: in its lesson's archive within a minute, no re-read" "$got" "S01E02 - Two Files.docm.REMOVED.txt|S01E02 - Two Files.pdf|S01E02 - Two Files.txt|"
check "added material: offered in the contents view" "$(cont "$ADMIN" "$EC2" | jq -c '[.Entries[] | select(.LeftOut|not) | .Path] | sort')" '["S01E02 - Two Files.docm.REMOVED.txt","S01E02 - Two Files.pdf","S01E02 - Two Files.txt"]'
check "added material: only that course planned again" "$(( $(docker logs "$NAME" 2>&1 | grep -c "planned /media/training/Course C again") > replans ))" 1
check "added material: no full re-plan" "$(docker logs "$NAME" 2>&1 | grep -c "Additional Material: planned [0-9]* archives in")" "$full_plans"
rm "$TC/Season 1/S01E02 - Two Files.pdf"
got=""; for _ in $(seq 45); do got=$(zentries); [[ $got != *"Two Files.pdf"* ]] && break; sleep 2; done
check "removed material: gone from the archive within a minute" "$got" "S01E02 - Two Files.docm.REMOVED.txt|S01E02 - Two Files.txt|"
m1=$(stat -c %Y "$ZC"); touch "$TC/Season 1/.hidden-note.txt" "$TC/Season 1/S01E02 - Two Files.nfo"; sleep 40
check "hidden files and .nfo writes: archive left alone" "$(stat -c %Y "$ZC")" "$m1"
rm -f "$TC/Season 1/.hidden-note.txt" "$TC/Season 1/S01E02 - Two Files.nfo"

cfg '.BuildArchives=false'
check "building off: built archives still served as they are" "$(info "$ADMIN" "$EC2" | jq -r .FileName)" "S01E02 - Two Files.material.zip"
cfg '.BuildArchives=true'

# ---- web client ----------------------------------------------------------------
check "script served" "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/web/additional-material.js")" 200
check "script carries the display settings" "$(curl -s "$BASE/AdditionalMaterial/web/additional-material.js" | grep -c 'var embedded = {"ButtonStyle"')" 1
check "strings: English"           "$(curl -s "$BASE/AdditionalMaterial/web/strings" | jq -r '."config.save"')" Save
check "strings: unknown language falls back" "$(curl -s "$BASE/AdditionalMaterial/web/strings?lang=xx-YY" | jq -r '."config.save"')" Save
check "strings: bad tag ignored"   "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/AdditionalMaterial/web/strings?lang=../../etc")" 200
if [[ -n $FT ]]; then
  sleep 5
  curl -s "$BASE/web/index.html" | grep -q 'plugin="AdditionalMaterial"' && ok "index.html carries the script (File Transformation)" || bad "index.html carries the script"
fi

echo; echo "passed $pass, failed $fail"; echo "test server left running on $BASE; '$0 --cleanup' removes it"
[[ $fail -eq 0 ]]
