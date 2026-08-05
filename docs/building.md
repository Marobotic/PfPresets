# Building, and the traps in it

Two builds come out of this repo and they are not interchangeable. Most of the time you want the
first one.

```bash
bash build.sh              # third-party build: community ratings COMPILED IN  -> bin/Release
bash build.sh --no-ratings # official-repo build: ratings omitted entirely     -> bin/ReleaseNoRatings
```

`bin/Release/PfPresets.dll` is the file the in-game dev plugin loads. Nothing else is.

## The no-ratings build used to eat the dev plugin

Until 2026-08-04 both builds wrote `bin/Release/PfPresets.dll`. Verifying the official-repo build -
a natural thing to do right after changing shared code - therefore replaced the running plugin with
one compiled without `PFP_RATINGS`. That build is missing, silently:

- the whole tab strip and the rail's navigation (`TabList`, `DrawNavStrip`)
- the My Profile tab and the profile card
- the party panel, including while recruiting
- the Settings tab (`PluginUI.RatingSettings.cs` is `#if PFP_RATINGS` end to end)

In game that reads as "the UI is broken", not as "you loaded the wrong build", and it cost several
rounds of hunting a rendering bug that did not exist. The two outputs are now separate directories
and cannot overwrite each other.

**Still finish a session with a plain `bash build.sh`.** The last build wins, and it is the one that
gets tested.

## When UI disappears wholesale, check the DLL before the code

Whole features vanishing at once is a build symptom far more often than a layout one. Two checks,
both seconds:

```bash
ls -la bin/Release/PfPresets.dll
# ratings build ~900KB, no-ratings build ~730KB

strings -a -el bin/Release/PfPresets.dll | grep "My Profile"
```

`-el` is not optional: .NET stores string literals as UTF-16, so a plain `strings` finds nothing in
either build and will tell you the feature is missing when it is present.

## Reloading

The dev plugin path points straight at `bin/Release`, so there is no deploy step - build, then
**Scan Dev Plugins** in Dalamud. `build.sh` passes `--no-incremental` because Dalamud only reloads
when the DLL's timestamp changes.

## Diagnosing layout from inside the game

`/pfpdebug chrome` arms a one-shot report from the main window: which layout it chose, the window
and rail dimensions, how many navigation rows were built, and each row's position, width and whether
ImGui considers it clipped. It prints to chat and turns itself off. Reach for it before theorising
about ImGui - every number the layout depends on is knowable at draw time, and none of it is
knowable by reading the source.
