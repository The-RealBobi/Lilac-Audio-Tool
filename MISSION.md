# Mission: safer replacement queue and resizable columns

## Context

Reported issue: dropping or adding an audio file whose name starts with a number can silently target that numeric AWB ID. Example: `1.wav` becomes a replacement for ID `1`, and `24.wav` can be queued even when the loaded ACB/AWB only has 22 entries.

This is risky because a normal filename can be interpreted as user intent. The previous owner was the GUI queue path:

- `OnDrop(...)` calls `GuessEntryFromFileName(path)`.
- `GuessEntryFromFileName(...)` reads leading digits from any filename.
- `AddReplacementToQueue(...)` clamps negative values but does not check whether the target entry exists in the loaded list.

User also requested resizable table columns. The affected tables are:

- `AwbEntriesGrid`
- `ReplacementQueueGrid`

## Goals

1. Stop accidental numeric filename targeting.
2. Prevent queue entries that point outside the loaded AWB/ACB entries.
3. Keep the intended drag-and-drop flow simple for normal users.
4. Allow users to resize table columns manually.
5. Avoid large UI rewrites or changes to export/encoding behavior.

## Proposed Behavior

### Replacement target selection

- Dropping audio files must never infer the target from the filename.
- Dropped audio files should use the first available target in the loaded entries list.
- Multiple dropped audio files should fill subsequent available entries.
- Plain filenames like `1.wav`, `24.wav`, or `001_bgm.wav` are treated only as filenames.
- The queued target entry should be editable from the queue table itself.

### Validation before queueing

- `AddReplacementToQueue(...)` should verify that the target entry exists in `_awbEntries`.
- Validation should respect the current selector mode:
  - ID mode checks against `AwbEntryViewModel.Id`.
  - Index mode checks against `AwbEntryViewModel.Index`.
- If no entries are loaded yet, keep the current manual flow conservative and allow the action only where the existing UI already depends on a selected value.
- If the target does not exist, do not add it to the queue and write a clear log message.

### Column resizing

- Enable user resizing on both `DataGrid`s.
- Set practical minimum widths so columns do not collapse into unreadable fragments.
- Keep current bindings and column order.

## Implementation Steps

1. Add a small helper that checks whether a queue target exists for the current selector mode.
2. Update `AddReplacementToQueue(...)` to reject missing targets before mutating `_replacementQueue`.
3. Remove filename-based target parsing from drag-and-drop.
4. Update drag-and-drop flow to use the first available entry in the loaded list, then advance through available entries.
5. Add localized log text for invalid replacement targets in Spanish and English.
6. Make the queue entry cell editable and validate edits against loaded entries.
7. Enable resizing/min widths in `AwbEntriesGrid` and `ReplacementQueueGrid`.
8. Bump build version after verification.
9. Run focused validation:
   - `dotnet test AudioTool.Tests/AudioTool.Tests.csproj`
   - `dotnet build AudioTool.Gui/AudioTool.Gui.csproj`

## Risks

- Some users may currently rely on `1.wav` automatically targeting ID `1`. This behavior is unsafe, so files now target the first available entry instead and can be adjusted in the queue.
- If no entries are loaded, validation cannot know whether an ID exists. The UI should continue nudging users to load/inspect the bank first.

## Implementation Notes

- Filename-based target parsing was removed from drag-and-drop.
- Drag-and-drop now assigns the first available entry from the loaded list.
- Queue targets are validated against loaded entries and cannot reuse an already queued target.
- The queue entry cell is editable so users can adjust the chosen target.
- Both entry and queue grids have user-resizable columns with minimum widths.

## Current Status

Implemented and verified.

Build artifact:

- `dist/LilacAudioTool-v0.2.29-osx-arm64.zip`

---

# Mission: investigate adding new dialogue audio entries

## Context

New request: analyze whether Lilac Audio Tool can add and play new audio entries, not only replace existing ACB/AWB entries.

The key unknown is not the AWB append itself. The risky part is discovering what game data manifests the dialogue lines for a character: which table, script, event, or runtime lookup maps a character/situation to an ACB cue / AWB ID / cue name. Adding an HCA to an AWB will not be enough if the game never references that cue.

Data source for this investigation:

- `/Volumes/BOBI/Proyectos Personales/VictoryRoad/DUMP_712/._work/readable/data/`

Ghidra is available if static data inspection does not reveal the owner.

## Questions To Answer

1. Which files list dialogue line identifiers for characters?
2. Do those identifiers point to ACB cue names, cue IDs, AWB IDs, or another intermediate voice bank table?
3. Are character dialogue lines embedded in event/script data, common character data, or sound metadata?
4. Does adding a new playable line require:
   - adding an ACB cue,
   - adding an AWB entry,
   - extending a character/dialogue manifest,
   - and/or patching a runtime table/check?
5. Can this be implemented safely in the GUI as "add new line", or should the tool first expose only an analysis/report mode?

## Investigation Plan

1. Inventory likely data areas:
   - `common/sound_asset`
   - character folders under `common/chr`
   - event/script/message folders if present
   - any readable tables with names containing `voice`, `dialog`, `talk`, `serif`, `cue`, `sound`, `awb`, or `acb`
2. Search readable text/binary strings for known ACB names and cue-like names.
3. Compare a known character voice bank with surrounding character data.
4. Use the existing ACB inspector to list cue names and look for matching references elsewhere in `data`.
5. If no direct data reference is found, use Ghidra to inspect runtime code paths that load ACB/AWB and request cue playback.
6. Produce a short technical conclusion:
   - current evidence,
   - likely owner file(s),
   - minimum data that must be patched,
   - unknowns requiring runtime/Ghidra confirmation.

## Current Status

Initial static investigation completed. No source-code implementation has been started for this feature.

## Findings

The readable dump does not include `common/sound_asset`; it includes the converted game-data side. That is enough to identify the likely manifest owners.

Relevant files found:

- `common/gamedata/event/event_bustup_talk_sound_data_config_1.02.92.cfg.bin.*`
  - Contains `EV_BUSTUP_TALK_SOUND_DATA_BODY_LIST_BEG 3137`.
  - Body rows contain a stable key/hash in column 0, sound cue strings in several columns, and float delay/fade values in paired columns.
  - Examples include BGM-like values (`bg90080`, `bg10020`), event cue values (`ev21_01100_1`, `ev23_02000_1`), stop cues (`bgm_stop_2000ms`), and loop stop names.
  - This is the strongest owner candidate for event bustup dialogue sound.
- `common/gamedata/event/event_bustup_talk_data_config_c*.cfg.bin.*`
  - Contains the per-scene/per-character bustup talk bodies and character slots.
  - Rows contain hashed dialogue/event keys plus portrait/model paths such as `common/chr/c000101/...`.
  - This likely owns which character/portrait/body is active for a dialogue line, while the sound-data table owns the sounds played around that line.
- `common/gamedata/event/event_general_bustup_talk_data_config_c40_1.03.32.cfg.bin.*`
  - Contains general bustup talk configs and team side data.
- `common/sound/chara_mot_sound_1.02.91.00.cfg.bin.*`
  - Declares `SOUND_BASE_PATH "common/sound_asset/<VLG>/"`.
  - This confirms character/motion audio resolves under the language-dependent sound asset path.
- `common/sound/chara_sound_resource.cfg.bin.*`
  - Contains `CHARA_SOUND_RESOURCE_INFO_LIST_BEG 669`.
  - Rows map hashed resource IDs to strings like `c00000110_<VOVAL>`.
  - This appears to register character voice resources, but not every probed bank was present by plain name.
- `common/sound/chara_sound_trigger_0.00.64.cfg.bin.*`
  - Contains `CHARA_SE_TRIGGER_INFO_LIST_BEG 78`.
  - Defines suffixes such as `bt020`, `sh010`, `kp020`, `sp015`.
  - This explains cue names built as `character + "_" + trigger`, for example `c05021500_bt020`.
- `common/gamedata/rpg_battle/rpg_battle_cmd_config_1.02.82.00.cfg.bin.*`
  - Contains 962 `RPG_BTL_CMD_PART_VOICE_TABLE_PLAY_INFO` rows.
  - Example evidence: `RPG_BTL_CMD_PART_VOICE_TABLE_PLAY_INFO 0, "c11010020_bt020", 1;`
  - Battle voice playback can reference a cue name directly.

## Current Interpretation

Adding an AWB entry alone is not enough. The game appears to request audio by cue name or by a hashed data key that eventually resolves to cue names. Therefore "add new playable line" likely means:

1. Add HCA data to the AWB.
2. Add a matching ACB cue/name/waveform row, not only a raw AWB item.
3. Add or update the relevant game-data row that points to that cue:
   - event bustup dialogue: `event_bustup_talk_sound_data_config` plus possibly the matching `event_bustup_talk_data_config_c*` row,
   - battle command voice: `rpg_battle_cmd_config`,
   - character-trigger voice: `chara_sound_resource` / `chara_sound_trigger` depending on whether the target is a new character resource or a new trigger suffix.
4. Repack the modified cfg.bin file(s) and place them in the mod CPK alongside the modified ACB/AWB.

## Open Questions

## True new-entry implementation

The previous `whs02280` and `whs02640` proof-of-concepts were not valid additions: they renamed and reused the existing `c01000010_whs02070` cue and AWB slot. They must not be treated as evidence that new cues work.

### Required outcome

Create a separate, non-destructive output pair where `c01000010_whs02070` remains intact and a real `c01000010_whs02640` cue is appended with its own HCA/AWB entry. The original source pair must remain byte-for-byte unchanged.

### Implementation plan

1. Extend the CRI `@UTF` reader/model with enough column metadata to rebuild tables while preserving constant fields, row fields, types, string/data pools, alignment, and nested table payloads.
2. Add a focused ACB authoring operation that clones the existing one-waveform cue chain and remaps its sequence, synth, track, track-event, waveform, cue, and cue-name references to appended rows.
3. Append the encoded HCA to the AWB using a new `StreamAwbId`, then rebuild the ACB nested tables and update `StreamAwbHash` and `StreamAwbAfs2Header`.
4. Validate row counts, cue-name coexistence, reference resolution, HCA metadata, AFS2 alignment, MD5/header consistency, and source immutability before exposing the operation to the GUI.
5. Keep the first implementation as a CLI/POC until runtime playback confirms the exact `CueId` and external manifest requirements; only then integrate it into the user workflow.

### Current status

- [x] Confirmed the earlier POCs reused an existing cue and AWB slot.
- [x] Identified the table chain that must be duplicated.
- [x] Implement a lossless-enough `@UTF` serializer for the modified ACB.
- [x] Produce and validate a true appended-entry pair.
- [ ] Test the new cue from the game's external event/resource references.

### Regression found and fixed

The first appended ACBs had valid-looking nested tables but an invalid `@UTF` size field at offset `0x06` (zero instead of `table_size - 8`). The tool's parser ignored this field; the game runtime did not. The serializer now writes the size for every rebuilt table, and the focused output validates its root/nested sizes, cue routing, AWB count, MD5, and AFS2 header prefix.

The game executable analysis found a second concrete issue: the original `StreamAwbAfs2Header.Header` field is 520 bytes for 62 AWB entries, while the AFS2 header for 63 entries is 524 bytes. The previous implementation copied only the old fixed-length field and truncated the new header. The authoring command now replaces that field with the complete final AFS2 header before serializing the ACB.

- The readable CFG dumps do not expose column names, so column meaning must be inferred or recovered from the cfg schema/editor code.
- Some rows use hashes rather than readable strings. Before writing new rows, we need the exact hash source string and CRC settings for each key column.
- Need to confirm whether ACB cue insertion is enough for CRI lookup, or whether cue IDs/cue name tables require additional ordering constraints.
- Need runtime validation for one small controlled addition before exposing this in the GUI.

## Proposed Next Step

Build a tiny proof-of-concept outside the GUI:

1. Choose an existing cue-trigger path that already plays.
2. Append one new cue to the same ACB/AWB using a conservative generated name.
3. Duplicate one known-good game-data row and point it to the new cue name.
4. Rebuild the relevant cfg.bin and CPK.
5. Test in-game.

If that works, the GUI can expose this as a guided "Add line" workflow. If it does not, use Ghidra to confirm runtime lookup rules for the specific table owner.

## Active POC: add `c01000010_whs02280`

Request: create modified copies of the original `c01000010.acb/.awb` pair and add a new cue/entry named `c01000010_whs02280`, using `/Users/bobi/Downloads/souund.mp3`. Originals under the dump must remain untouched.

Planned output:

- A staging folder under the AUDIO workspace, not inside the source dump.
- `c01000010.acb` and `c01000010.awb` modified copies.
- A short inspection report outside any final mod `data` folder.

Implementation plan:

1. Inspect source ACB/AWB and confirm the existing cue layout:
   - current cue count,
   - current stream AWB entry count,
   - `c01000010_whs02070` row as the closest template.
2. Convert `souund.mp3` with FFmpeg to the source bank's expected format, then encode to HCA using the existing Python encoder path.
3. Try to append a new AWB item with the next unused AWB ID.
4. If appending ACB UTF rows is not available yet, use an in-place proof by repurposing the closest same-length cue name in a copy only.
5. Duplicate the closest ACB cue/waveform metadata row and adjust:
   - cue name to `c01000010_whs02280`,
   - cue index/id references,
   - stream AWB ID,
   - sample count/channels/sample rate/loop flag,
   - StreamAwbHash and StreamAwbAfs2Header.
6. Validate with the existing inspector and direct table checks.
7. If the ACB writer cannot safely expand UTF tables yet, stop at a clear blocker rather than corrupting the bank.

Result:

- The current Python ACB patcher can modify existing UTF fields but cannot yet grow nested UTF tables safely.
- The POC therefore uses an in-place copy-only transformation:
  - `c01000010_whs02070` was renamed to `c01000010_whs02280`.
  - The name length is identical, so no table growth was required.
  - Waveform index 31 points to `StreamAwbId = 4`; the replacement must target AWB ID 4, not ID 31.
- `souund.mp3` was converted to HCA v3.00, mono, 48 kHz, no loop, 569261 samples.
- Modified clean pair:
  - `/Users/bobi/Documents/LEVEL 5 ENGINE/AUDIO/work/poc_whs02280_export/data/common/sound_asset/ja/c01000010.acb`
  - `/Users/bobi/Documents/LEVEL 5 ENGINE/AUDIO/work/poc_whs02280_export/data/common/sound_asset/ja/c01000010.awb`
- Validation:
  - AWB still has 62 entries, IDs 0..61.
  - Cue `c01000010_whs02280` resolves to waveform 31.
  - Waveform 31 resolves to `StreamAwbId = 4`.
  - Waveform metadata now matches the generated HCA: 569261 samples, 48000 Hz, 1 channel, no loop.
  - ACB StreamAwbHash and StreamAwbAfs2Header were updated from the final AWB.

Important caveat:

- This POC tests whether the game will request and play `c01000010_whs02280` when the cue exists in the character bank.
- It does not yet prove general-purpose ACB cue insertion, because it reuses one existing cue slot in a copied bank.

## Follow-up POC: `c01000010_whs02640`

Reason: `c01000010_whs02280` produced a valid ACB/AWB pair and did not break other sounds, but the target cue did not play in-game. To separate "cue missing" from "skill/event does not request voice", test `whs02640`.

Findings before generating:

- `whs02640` is present in `item_config_7.00.25.00`.
- `whs02640` has a concrete sound event file:
  - `common/event_cfg/snd/ev60_02640_snd.cfg.bin`
- That sound event requests:
  - `waza_vc1_852`, suffix `whs02640`, template `c00<TYPE><NO>_s00_p<VARIATION>`
  - `waza_vc2_856`, suffix `whs02640`, template `c00<TYPE><NO>_s01_p<VARIATION>`
- Existing banks with this cue include:
  - `c06032120.acb -> c06032120_whs02640`
  - `c06032130.acb -> c06032130_whs02640`

Generated output:

- Clean pair:
  - `/Users/bobi/Documents/LEVEL 5 ENGINE/AUDIO/work/poc_whs02640_export/data/common/sound_asset/ja/c01000010.acb`
  - `/Users/bobi/Documents/LEVEL 5 ENGINE/AUDIO/work/poc_whs02640_export/data/common/sound_asset/ja/c01000010.awb`

Implementation notes:

- Reused the copied `c01000010_whs02070` cue slot because `c01000010_whs02640` has the same string length.
- Replaced AWB ID 4, because `c01000010_whs02070` resolves to waveform index 31 and waveform 31 has `StreamAwbId = 4`.
- Patched waveform 31 to match the generated HCA: 569261 samples, 48000 Hz, mono, no loop.
- Updated StreamAwbHash and StreamAwbAfs2Header.

Hash / ID observation:

- `CueTable.CueId` does not appear to be CRC32 of the cue name.
- Examples:
  - `c01000010_whs02070` has `CueId = 107`.
  - `c06032120_whs02640` has `CueId = 48`.
  - `c06032130_whs02640` has `CueId = 48`.
- For this POC, `CueId` was left as the original slot value `107` instead of inventing a new hash.

If this does not play:

- Check whether the game is requesting the `s00` or `s01` variation and whether `c01000010` resolves to this voice bank for that variation.
- Check whether this specific skill only enables voices for certain character/costume resources.
- Then use Ghidra/runtime tracing for `waza_vc1` / `waza_vc2` command resolution, not generic ACB checksum work.

## Correction: previous POCs were not true additions

The `whs02280` and `whs02640` POCs reused the existing `c01000010_whs02070` cue slot in a copied ACB. They did not add a new CueNameTable row, CueTable row, Sequence/Synth/Track row, WaveformTable row, or a new AWB item.

That means the failed in-game tests do not yet answer whether true added entries work. They only show that repurposing this existing slot does not make those skill voice calls resolve.

Next objective:

1. Build or add a real ACB UTF table writer that can grow nested tables safely.
2. Append one new AWB item with a new AWB ID.
3. Add a new waveform row pointing to that new AWB ID.
4. Add matching Synth/Track/TrackEvent/Sequence/Cue/CueName rows using the simple one-waveform pattern from an existing `whs` cue.
5. Keep original cue rows intact.
6. Generate a new clean pair and validate that both the old `c01000010_whs02070` and the new cue coexist.
# GUI 0.3.0 - Nueva entrada y preferencias de columnas

- La interfaz incorpora `Nueva entrada`, separada de `Sustituir`.
- La entrada seleccionada se usa como plantilla de encaminamiento; el usuario elige el audio y escribe el nombre exacto del nuevo cue.
- La GUI genera un HCA temporal mediante `replace-awb-wav`, llama a `add-acb-awb-hca` y actualiza el hash/header AFS2 con `patch-acb-stream-awb`.
- Se parte siempre del ACB/AWB original cargado y se publica únicamente el par exportado. La carpeta temporal se elimina al terminar.
- La primera versión de la operación no combina una nueva entrada con una cola de sustituciones: la GUI lo comunica y exige exportar por separado.
- Los anchos absolutos de las columnas de AWB y de la cola se guardan en `config/user_preferences.json` y se restauran al arrancar.
