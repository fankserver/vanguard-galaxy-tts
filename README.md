# TTS

A BepInEx mod that voices every dialogue line in [Vanguard Galaxy](https://store.steampowered.com/app/2395170/Vanguard_Galaxy/) using neural text-to-speech.

- **2831 pre-rendered OGG clips** cover every story and side-quest dialogue line the game currently ships with
- **42+ NPCs get distinct voices** from Kokoro v1.0's multilingual speaker bank — Japanese, Hindi, Spanish, French, Portuguese accents where the character calls for it
- **ECHO** (your ship AI) and the **player captain** get hand-picked voices, with 6 captain presets (3 male + 3 female) selectable in config
- **Live fallback** via the bundled Kokoro engine catches anything the pack misses (dynamic commander-name lines, post-patch additions, procedural station names)

## Install

1. **Install BepInEx 5.x** into the game folder — grab the `BepInEx_win_x64_5.4.x.zip` from the [BepInEx releases](https://github.com/BepInEx/BepInEx/releases) and unzip it into your Vanguard Galaxy install folder (next to `VanguardGalaxy.exe`).
2. **Launch the game once** so BepInEx creates its `BepInEx/plugins/` and `BepInEx/config/` subfolders, then close it.
3. **Download the VGTTS release** from Nexus Mods.
4. **Unzip** into `BepInEx/plugins/`. The zip contains a single `VGTTS/` folder that drops in cleanly:
   ```
   VanguardGalaxy/BepInEx/plugins/
     VGTTS/
       VGTTS.dll
       prerender/...     (2831 OGG files + manifest)
       tools/kokoro/...  (neural TTS model)
       tools/sherpa/...  (inference binary)
   ```
5. **Launch the game.** Dialogue lines should start speaking.

## Config

BepInEx writes the config to `BepInEx/config/vgtts.cfg` after the first launch. Key knobs:

```ini
[General]
Enabled = true                  # master on/off
DialogueTTS = true              # speak NPC dialogue
EchoTTS = true                  # speak ECHO's ambient HUD hints

[Voice]
CaptainPreset = auto            # auto | m1 | m2 | m3 | f1 | f2 | f3
```

**Captain voice presets:**

| Preset | Voice | Character |
|---|---|---|
| `m1` | Kokoro am_fenrir | rugged American explorer |
| `m2` | Kokoro am_onyx | deep, confident |
| `m3` | Kokoro bm_fable | British rogue |
| `f1` | Kokoro af_alloy | calm, measured |
| `f2` | Kokoro bf_alice | British female |
| `f3` | Kokoro af_heart | warm flagship |
| `auto` | picks `m1` or `f1` based on your commander's gender |

The `[Voices]` section lists every known NPC with its default Kokoro speaker ID. Override any NPC by changing the value — e.g. `Arle = kokoro:3` would swap Arle to the warm flagship female voice. See the full Kokoro speaker list at [hexgrad/Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M).

## Troubleshooting

**No audio at all**
- Check the BepInEx console (enable via `BepInEx/config/BepInEx.cfg` → `[Logging.Console]` → `Enabled = true`). You should see `Kokoro TTS loaded. NPC profiles seeded: 49, prerender entries: 2831, ...` on startup.
- If instead you see `Kokoro bundle missing, TTS disabled`, the `VGTTS/tools/kokoro/` or `VGTTS/tools/sherpa/` folder didn't unpack correctly. Re-download and re-extract.

**Wrong voice / wrong gender for a specific NPC**
- Edit the `[Voices]` section in `vgtts.cfg` and set the speaker to a different Kokoro SID (e.g. `Arle = kokoro:3`). Delete the entry entirely and relaunch to restore the default.

**Some dialogue lines play with a noticeable delay on first utterance**
- That's the live-TTS fallback synthesizing a line the pack doesn't cover. Once synthesized it's cached — the same line plays instantly the second time. Common causes: game patches added new text, commander-name lines during the first ~20 seconds of a save load before the warm-up finishes, station names in procedurally-generated systems.

**"Captain Fank" etc. lines don't match your callsign**
- The plugin reads your commander's callsign at save load and pre-warms those lines. If you rename mid-save, re-load the save to trigger a fresh warm-up.

## Known limitations

- **Procedural station names** (e.g. "Welcome to The Oracle Spire") are built from random word lists at runtime and can't be pre-rendered. These always use live TTS.
- **Conquest rank titles** vary with your progression and also stay on live TTS.
- **Scottish accent** — Kokoro has no Scottish speaker, so Elias McIntire uses a plain American male voice. Tracked as an open gap.
- **Game version drift** — when the game patches add new dialogue, those lines will miss the pack and fall through to live TTS. Sounds fine but uses the English-Kokoro voice for accent NPCs. To contribute a delta for a new patch, see "Contributing" below.

## Releasing (for maintainers)

Creating a GitHub Release auto-builds and uploads the zip via `.github/workflows/release.yml`. CI compiles `VGTTS.dll` against a **publicized stub** of `Assembly-CSharp.dll` committed at `VGTTS/lib/Assembly-CSharp.dll` (method signatures only — no game code). The real game assembly takes over at runtime via Mono's late binding.

```bash
# One-time per game update — regenerate the publicized stub from your
# current install and commit it. BepInEx-standard tool, MIT-licensed.
# The --strip flag is mandatory: it replaces every method body with
# `throw null;` so the committed file contains only public signatures,
# not the game's IL. Running without --strip reintroduces the copyright
# leak that was purged in the 2026-04-23 history rewrite.
dotnet tool install -g BepInEx.AssemblyPublicizer.Cli
assembly-publicizer --strip \
  "$GAME_DIR/VanguardGalaxy_Data/Managed/Assembly-CSharp.dll" \
  -o VGTTS/lib/Assembly-CSharp.dll
git add VGTTS/lib/Assembly-CSharp.dll && git commit -m "chore: refresh publicized stub for game vX.Y"

# Tag + release
gh release create v1.2.0 --title "v1.2.0 …" --notes-file CHANGELOG-v1.2.0.md
```

GitHub Actions then: checks out the tag, verifies the stub is present, runs `dotnet build`, fetches the Kokoro + sherpa-onnx bundles, stages the shipping layout, and uploads `VGTTS-v1.2.0.zip` back to the release.

To re-run on a failed release without recreating it: `gh workflow run release.yml -f tag=v1.2.0`.

## Contributing

The dialogue pack is built from the game's decompiled source, so when a patch adds new lines, someone has to re-extract and re-render. Workflow:

```bash
# 1. Harvest your session's misses
cp "$GAME/BepInEx/cache/VGTTS/unprerendered.tsv" .

# 2. Render them (needs ~400 MB Kokoro bundle)
make download-kokoro
python tools/prerender/render_missing.py --input unprerendered.tsv

# 3. Commit + PR
git add prerender/ && git commit -m "chore: delta for patch X.Y"
```

Full rebuild from scratch is also supported — see `tools/prerender/extract_dialogue.py` + `tools/prerender/render_missing.py`.

## Credits

- **Vanguard Galaxy** by [Bat Roost Games](https://store.steampowered.com/developer/BatRoostGames/) — the game all this dialogue comes from
- **Kokoro v1.0** ([hexgrad/Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M)) — the TTS model voicing every line
- **sherpa-onnx** ([k2-fsa/sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx)) — runtime inference
- **BepInEx 5** — mod loader
- **HarmonyX** — runtime patching

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for full attribution.

## License

MIT. See [LICENSE](LICENSE).
