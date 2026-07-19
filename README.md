![Eggman's LaserForge](assets/Image_banner_full.png)

# Eggman's LaserForge

**A game studio for Hypseus Singe.** Mark video scenes with frame-exact
precision, storyboard the game's flow as a node graph, choreograph the
player's moves, and export a complete, ready-to-run Singe game — no
hand-editing of LUA.

A native C#/Avalonia application, fully portable and self-contained.

## Why frame-exact matters

Singe games live and die by frame numbers: `discSearch(13145)` must land on
the picture the author intended. Generic video players can't do this on
MPEG-2 elementary streams. This app can, because its frame engine is built
from the emulator's own playbook:

- The M2V scanner is a faithful port of the `mpegscan` state machine in
  hypseus-singe's VLDP, producing **byte-identical `.dat` frame index files**
  (verified against Hypseus output on real game videos).
- Seeking replicates VLDP's exact algorithm: walk back to the anchor I-frame
  (including VLDP's back-up-one-more-GOP quirk), feed the decoder the cached
  sequence header plus the stream from the anchor, and count decoded frames.
- Frame `N` in this app is therefore *provably* the picture Hypseus displays
  for frame `N`.

## What it does

- **Frame-exact viewer** — jog, shuttle, and scrub M2V video with a live
  global-frame counter and matching OGG audio on playback.
- **Scenes** — mark in/out points and store named, described scenes with
  thumbnails, across multiple videos in one global frame space.
- **Storyboard** — a ComfyUI-style node graph: wire scenes into the game's
  flow with success / death / timeout branches, and play any scene or the
  whole flow to test it.
- **Moves** — place player inputs (4-way + buttons + skip) with
  difficulty-aware timing windows and back-to-back spacing validation.
- **Game Setup** — attract/title videos, menus, system videos, difficulty
  frames, scoring, language tracks, and the framework choice.
- **Export & Test** — generate the full `.singe` script and frame file from a
  known-good template (every framework element and helper comment preserved),
  drop the global frameworks into place, and launch straight into Hypseus.

## Layout

| Path | What it is |
|---|---|
| `src/Ldp.FrameEngine` | Frame-exact engine: M2V scanner, VLDP `.dat` I/O, seek policy, FFmpeg decoder, OGG audio |
| `src/Ldp.Project` | Project model, Singe import/export, template engine |
| `src/Ldp.App` | Avalonia GUI |
| `tests/Ldp.EngineProbe` | CLI harness + model test suite |
| `assets/Frameworks` | The global Singe frameworks the app installs for the user |

Video/audio media (`*.m2v`, `*.ogg`) and generated `*.dat` / `*.ldpidx`
files are not tracked in git.

## Building

```
dotnet build
dotnet run --project src/Ldp.App
```

Requires the .NET 10 SDK on Windows x64 (FFmpeg native libraries come in via
the `Sdcb.FFmpeg.runtime.windows-x64` NuGet package).

## License

Source code and documentation are MIT-licensed. The bundled Singe frameworks,
and any game video, audio, artwork, or scripts you author or import, remain
the property of their respective copyright holders. See `LICENSE` and
`NOTICE`.

*Built by Eggman for the laserdisc game and Hypseus Singe community, and
pair-programmed with Claude (Anthropic).*
