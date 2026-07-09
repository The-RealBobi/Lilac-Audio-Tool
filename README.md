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

## Research Notes

Long-form reverse-engineering notes and probes live under `research/`. They are useful for development, but the root of the repo is kept focused on the tool itself.
