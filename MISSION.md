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
