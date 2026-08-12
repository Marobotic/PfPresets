#!/usr/bin/env bash
# package.sh - build the zip that goes to users.
#
#   bash package.sh
#
# WHAT IS IN THE BUILD AND WHY IT IS NOT IN THE REPOSITORY.
#
# Two sets of files live on this machine and are gitignored:
#
#   Core/VoteEvidence.cs                    what proves a vote came out of a real duty
#   Core/Admin*.cs, UI/PluginUI.Admin*.cs   the moderator tools
#
# BOTH SHIP. That is a deliberate decision, taken on 2026-08-10 once the moderation authority had
# moved server-side: the client half is a panel and a key, and every action it can take is checked
# against a server-issued enrolment and a signed challenge before anything happens. A copy of the
# panel with no enrolment is a window that returns 403.
#
# What is still worth keeping out of the repository is the SOURCE. Reading it tells somebody the
# shape of the enrolment, the wording of the challenge, and which endpoints exist; getting the same
# from the assembly means decompiling it first. That is the same trade the evidence key is under,
# and it is the only one on offer - a symmetric secret shipped to thirty thousand people has never
# been a secret from anybody willing to work for it.
#
# So this script does not hold anything back. It builds, checks that what came out is what we meant
# to ship, and packages it.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

OUT="$SCRIPT_DIR/repo/latest.zip"

for required in Core/VoteEvidence.cs Core/AdminAccess.cs UI/PluginUI.AdminPanel.cs; do
  if [ ! -f "$required" ]; then
    echo "!! $required is missing." >&2
    echo "   These files are gitignored and exist only on the machines that hold a key." >&2
    echo "   A build without VoteEvidence.cs ships a plugin that cannot vote; one without the" >&2
    echo "   admin files ships without the moderator panel. Neither is what a release is." >&2
    exit 1
  fi
done

echo "== building =="
bash build.sh >/dev/null

DLL="$SCRIPT_DIR/bin/Release/PfPresets.dll"
[ -f "$DLL" ] || { echo "!! no DLL at $DLL" >&2; exit 1; }

echo
echo "== checking what came out =="

# Reads the binary rather than trusting the build. In Python because .NET writes string literals as
# UTF-16, so grep finds nothing and will tell you a DLL is clean when it is not - which it did, once.
python3 - "$DLL" <<'CHECK' || exit 1
import sys, pathlib

data = pathlib.Path(sys.argv[1]).read_bytes()

def present(needle):
    return any(needle.encode(enc) in data for enc in ("utf-16-le", "latin-1"))

# `admin/screen` rather than `admin/leaderboard`: the leaderboard and the action log are described
# by the server now and the plugin has no name for either. See src/screens.js - the whole point is
# that a released DLL no longer says what moderation can do, so this check has to name the two
# generic endpoints that remain instead of the surface that used to leak.
missing = [n for n in ("panels/hello", "panels", "panels/act", "achievements/feed", "clears")
           if not present(n)]

# And the reverse: the converted surface must NOT come back. A build that still carries these is one
# where the old panel was resurrected or the deletion was reverted, which is exactly the regression
# this change exists to prevent, and it would ship unnoticed.
# THE WHOLE LIST, not a sample. Every one of these was in a shipped build and each is a word that
# tells a reader what the panel is for. A build carrying any of them is one where the conversion was
# reverted or a new tool was written the old way.
leaked = [n for n in ("admin/", "moderat", "enrol", "leaderboard", "##modrows", "##auditrows",
                      "MODERATION", "held vote", "Unban") if present(n)]

if leaked:
    print("!! this build leaks the moderation surface again: " + ", ".join(leaked), file=sys.stderr)
    print("   the leaderboard and action log are server-described - see src/screens.js", file=sys.stderr)
    raise SystemExit(1)

if missing:
    print("!! this build is missing things a release needs: " + ", ".join(missing), file=sys.stderr)
    print("   built without ratings, or without the admin files?", file=sys.stderr)
    raise SystemExit(1)

print("   ratings, achievements and the moderator panel are present, and the")
print("   panel vocabulary is not in the binary")
CHECK

echo
echo "== packaging =="

# DalamudPackager writes the zip itself as part of an ordinary build, in the layout Dalamud expects.
# Use that rather than assembling one by hand - a zip that is nearly right is a plugin that nearly
# installs, and the packager is the thing that knows what "right" means here.
PACKED="$SCRIPT_DIR/bin/Release/PfPresets/latest.zip"
[ -f "$PACKED" ] || { echo "!! DalamudPackager produced no zip at $PACKED" >&2; exit 1; }

cp "$PACKED" "$OUT"

python3 - "$OUT" <<'ZIPCHECK' || exit 1
import sys, zipfile

with zipfile.ZipFile(sys.argv[1]) as z:
    names = z.namelist()

# The moderator panel signs its challenges with Ed25519 through BouncyCastle, because .NET's own
# ECDsa goes to Windows CNG and Wine does not implement it - a Linux user got 0x80090029 and a
# perfectly mysterious "couldn't reach the server". Shipping the panel means shipping the library.
if not any("BouncyCastle" in n for n in names):
    print("!! no BouncyCastle in the zip - the moderator panel cannot sign without it", file=sys.stderr)
    raise SystemExit(1)

if "PfPresets.dll" not in names:
    print("!! no plugin in the zip", file=sys.stderr)
    raise SystemExit(1)

print("   zip contents: " + ", ".join(names))
ZIPCHECK

echo
echo "== repo manifest =="
python3 - "$SCRIPT_DIR" <<'MANIFEST'
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
MANIFEST

echo
echo "[OK] $OUT"
unzip -l "$OUT" | tail -n +4 | head -n -2
echo
echo "     repo/repo.json updated to match PfPresets.json."
