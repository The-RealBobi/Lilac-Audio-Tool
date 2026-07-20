# Lilac Audio Tool

Desktop tool for replacing audio inside ACB/AWB banks.

It lets you open a bank pair, preview entries, queue one or more replacements, adjust loops, and export a patched pair ready to package as a mod.

Local dumps, generated banks, SDK files, caches, and research outputs are not part of the repository.

## Build

```bash
dotnet build Lilac.AudioTool.slnx
```

Requirements for a source checkout:

- .NET 9 SDK.
- Python 3 available as `python3` on macOS/Linux or `python`/`py` on Windows.
- Internet access on first use if FFmpeg or vgmstream are not already installed.

Common first-run issues:

- If Python is installed in a non-standard location, set `L5_AUDIO_PYTHON`.
- If Homebrew/portable tools are not visible to GUI apps, set `L5_AUDIO_FFMPEG_PATH` and/or `L5_AUDIO_VGMSTREAM_PATH`.
- The app checks PATH and its `PlugIns/` folders, including nested plugin folders.
- On Windows, vgmstream must keep `vgmstream-cli.exe` together with its DLLs. If playback fails after an old install, delete `LilacAudioTool/PlugIns/Windows/` and launch the app again.

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

The output is written to `dist/<runtime>/`.

Runtime notes:

- Python 3 must be available as `python3` on macOS/Linux or `python` on Windows.
- The app can download FFmpeg and vgmstream into the user data folder.
- Preferences, caches, downloaded tools, and default work output are stored outside the install directory under the OS application-data folder, in `LilacAudioTool/`.
- Set `L5_AUDIO_PYTHON` if Python is installed in a non-standard location.
- Set `L5_AUDIO_DATA_ROOT` to force a portable data folder next to the app or inside a modding toolkit.
- Set `L5_AUDIO_ROOT` only when running from an unusual folder layout.
