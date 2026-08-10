#!/usr/bin/env bash
# package.sh - build the zip that goes to users, and prove it is safe before it does.
#
#   bash package.sh
#
# WHY THIS EXISTS RATHER THAN "build.sh and zip bin/Release".
#
# The dev build on this machine is not the build users get, and the difference is not cosmetic.
# Two sets of files live here that are not in the repository:
#
#   Core/Admin*.cs, UI/PluginUI.Admin*.cs   moderator tools. Present here, so they compile in.
#   Core/VoteEvidence.cs                    what proves a vote came out of a real duty.
#
# The second MUST ship - without it the plugin builds a vote with no evidence and the server
# refuses every one of them, so a release missing it is a release where nobody can vote. The first
# MUST NOT. It is guarded by a server-issued key and a challenge, so shipping it would not hand
# anybody moderator powers, but thirty thousand people should not be carrying code that can ban
# players; the safest version of that code is the one that was never in the download.
#
# The hooks are partial methods (see UI/PluginUI.AdminHooks.cs), so simply moving the files aside
# erases them and every call to them. This script does that, builds, checks the result actually
# came out clean, and puts them back whatever happens.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

HELD="$(mktemp -d)"
OUT="$SCRIPT_DIR/repo/latest.zip"

# Restore the held-back files however this exits - including a failed build or a Ctrl-C. Leaving
# somebody's moderator files in a temp directory would be a poor way to end an afternoon.
restore() {
  shopt -s nullglob
  for f in "$HELD"/*; do
    mv "$f" "$SCRIPT_DIR/$(basename "$f" | tr '@' '/')"
  done
  rm -rf "$HELD"
}
trap restore EXIT

echo "== holding back the moderator build =="
shopt -s nullglob
for f in Core/Admin*.cs UI/PluginUI.Admin*.cs; do
  # PluginUI.AdminHooks.cs is the DECLARATIONS and has to stay - it is what the ordinary build
  # compiles to nothing. Removing it is a build error, not a smaller binary.
  [ "$f" = "UI/PluginUI.AdminHooks.cs" ] && continue
  echo "   $f"
  mv "$f" "$HELD/$(echo "$f" | tr '/' '@')"
done

if [ ! -f Core/VoteEvidence.cs ]; then
  echo "!! Core/VoteEvidence.cs is missing. A build without it ships a plugin that cannot vote." >&2
  exit 1
fi

echo
echo "== building =="
bash build.sh >/dev/null

DLL="$SCRIPT_DIR/bin/Release/PfPresets.dll"
[ -f "$DLL" ] || { echo "!! no DLL at $DLL" >&2; exit 1; }

echo
echo "== checking what came out =="

# Look for strings only the moderator build contains. In Python because .NET writes its string
# literals as UTF-16 and grep does not find them - the first version of this check used grep, found
# nothing, and said so confidently about a DLL that did contain them.
#
# Crude on purpose: it does not depend on the build having done what it was told, only on what came
# out the other end.
python3 - "$DLL" <<'CHECK' || exit 1
import sys, pathlib

data = pathlib.Path(sys.argv[1]).read_bytes()
leaked = []

for needle in ("admin/enrol", "admin/leaderboard", "admin/challenge", "admin/ban", "BouncyCastle"):
    for enc in ("utf-16-le", "latin-1"):
        if needle.encode(enc) in data:
            leaked.append(needle)
            break

if leaked:
    print("!! the moderator build leaked into this package: " + ", ".join(leaked), file=sys.stderr)
    raise SystemExit(1)

# And the other way round: a release that cannot vote is worse than no release, and it would look
# like a server fault rather than a packaging one.
for needle in ("achievements/feed", "clears", "progress"):
    if needle.encode("utf-16-le") in data:
        break
else:
    print("!! this DLL has no API routes in it at all - built without ratings?", file=sys.stderr)
    raise SystemExit(1)
CHECK

echo "   no moderator strings found"

echo
echo "== packaging =="

# DalamudPackager writes the zip itself as part of an ordinary build, in the layout Dalamud expects.
# Use that rather than assembling one by hand - a zip that is nearly right is a plugin that nearly
# installs, and the packager is the thing that knows what "right" means here.
PACKED="$SCRIPT_DIR/bin/Release/PfPresets/latest.zip"
[ -f "$PACKED" ] || { echo "!! DalamudPackager produced no zip at $PACKED" >&2; exit 1; }

cp "$PACKED" "$OUT"

# And check the ZIP, not just the DLL that went into it. This is the file people actually download,
# and the packager decides what goes in it - including project references. Running this on the dev
# build's zip today would have found BouncyCastle and the moderator assembly sitting in it, which is
# the whole reason to look at the artifact rather than at the intent.
python3 - "$OUT" <<'ZIPCHECK' || exit 1
import sys, zipfile

bad = []
with zipfile.ZipFile(sys.argv[1]) as z:
    names = z.namelist()

    for name in names:
        if 'BouncyCastle' in name:
            bad.append(name)

    for name in names:
        if not name.endswith('.dll'):
            continue
        data = z.read(name)
        for needle in ('admin/enrol', 'admin/leaderboard', 'admin/ban'):
            if needle.encode('utf-16-le') in data or needle.encode('latin-1') in data:
                bad.append(f"{name} contains {needle}")

if bad:
    print("!! this zip must not ship: " + "; ".join(bad), file=sys.stderr)
    raise SystemExit(1)

print("   zip contents: " + ", ".join(names))
ZIPCHECK

echo
echo "== repo manifest =="
python3 - "$SCRIPT_DIR" <<'PY'
import json, sys, pathlib, collections

root = pathlib.Path(sys.argv[1])
manifest = json.loads((root / 'PfPresets.json').read_text(), object_pairs_hook=collections.OrderedDict)
repo_path = root / 'repo' / 'repo.json'
repo = json.loads(repo_path.read_text(), object_pairs_hook=collections.OrderedDict)

# Only the fields the repo entry shares with the manifest. Everything else in there - the download
# links, DutyDM's whole entry - is the repo's own business and is left exactly as it was.
carried = ['AssemblyVersion', 'Description', 'Punchline', 'Changelog', 'DalamudApiLevel',
           'ApplicableVersion', 'Tags', 'AcceptsFeedback', 'IconUrl', 'RepoUrl', 'Author']

for entry in repo:
    if entry.get('InternalName') != 'PfPresets':
        continue
    for field in carried:
        if field in manifest:
            entry[field] = manifest[field]
    print(f"   PfPresets -> {entry['AssemblyVersion']}")

repo_path.write_text(json.dumps(repo, indent=4) + "\n")
PY

echo
echo "[OK] $OUT"
unzip -l "$OUT" | tail -n +4 | head -n -2
echo
echo "     repo/repo.json updated to match PfPresets.json."
echo "     The dev build in bin/Release is now the RELEASE build - no moderator tools."
echo "     Run 'bash build.sh' before going back to testing so the dev plugin has them again."
