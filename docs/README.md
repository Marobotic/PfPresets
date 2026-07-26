# docs/

## version-history.json

The complete release history of PF Presets in structured form, kept for the planned in-plugin
**What's New** viewer — so the plugin can show users what changed after an update without
shipping a second copy of the text.

`CHANGELOG.md` at the repo root stays the human-readable copy (it's what people read on GitHub).
This file is the machine-readable one. **Update both when cutting a release** — they'll drift
otherwise, and the viewer is the one that ends up wrong.

### Shape

```jsonc
{
  "schemaVersion": 1,          // bump if the shape below changes
  "releases": [                // newest first
    {
      "version": "3.0.0.1",
      "date": "2026-07-25",    // null when unknown
      "published": true,       // false = built but never served from repo.json
      "headline": "…",         // one line, safe to use as a window title
      "notes": "…",            // optional context about the release itself
      "changes": [
        { "type": "new", "title": "…", "body": "…" }
      ]
    }
  ]
}
```

`type` is one of `new`, `changed`, `fixed`, `removed`, `internal` — enough to group or colour-code
entries in the viewer, and to let it hide `internal` from players.

### Notes for whoever builds the viewer

- **`published: false` releases exist.** 3.0.0 was built but never served from `repo.json`; its
  features reached users inside 3.0.0.1. A viewer showing "what changed since your last version"
  should either skip unpublished entries or fold them into the release that shipped them,
  otherwise users see release notes for a version they never ran.
- **Versions are four-part** (`3.0.0.1`) and should be compared with `System.Version`, not string
  ordering — `"3.0.0.10"` sorts before `"3.0.0.2"` as text.
- **Track the last-seen version in `Configuration`** so the viewer only appears once per update.
  That's a config schema bump; see `Configuration.Migrate`.
- **The oldest entry has a `null` date** — don't assume every release has one.
- Bodies are plain prose with no markup, sized for an ImGui text block. Keep them that way; the
  viewer shouldn't need a Markdown parser.
