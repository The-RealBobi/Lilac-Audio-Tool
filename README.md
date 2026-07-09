# Lilac Audio Tool

Avalonia/C# tool for preparing CRI ACB/AWB audio mods for LEVEL-5 titles.

Current state:

- `AudioTool.Gui`: desktop UI for inspecting AWB entries and preparing replacements.
- `tools/CriHcaTool`: C# helper for HCA inspection, ADX/HCA support through VGAudio, and HCA type-1 unciphering.
- `tools/cri_audio_tool.py`: legacy backend kept temporarily while the remaining ACB/AWB/CPK logic is ported to C#.

The repository intentionally excludes local dumps, generated banks, Ghidra projects, SDK binaries, FFmpeg caches, and experimental work outputs.

## Build

```bash
dotnet build Lilac.AudioTool.slnx
```

## Run GUI

```bash
dotnet run --project AudioTool.Gui/AudioTool.Gui.csproj
```

## Distribution

Publish a redistributable folder with:

```bash
python3 tools/package.py --runtime osx-arm64
python3 tools/package.py --runtime win-x64
python3 tools/package.py --runtime linux-x64
```

The output is written to `dist/<runtime>/`. It includes the Avalonia GUI, the Python backend script, and the C# helper project used by the backend.

Runtime notes:

- Python 3 must be available as `python3` on macOS/Linux or `python` on Windows.
- The app auto-detects or downloads FFmpeg and vgmstream into the user data folder.
- Preferences, caches, downloaded tools, and default work output are stored outside the install directory under the OS application-data folder, in `LilacAudioTool/`.
- Set `L5_AUDIO_PYTHON` if Python is installed in a non-standard location.
- Set `L5_AUDIO_DATA_ROOT` to force a portable data folder next to the app or inside a modding toolkit.
- Set `L5_AUDIO_ROOT` only when running from an unusual layout where `tools/cri_audio_tool.py` is not beside the GUI publish output.

## Research Notes

Long-form reverse-engineering notes and probes live under `research/`. They are useful for development, but the root of the repo is kept focused on the tool itself.
