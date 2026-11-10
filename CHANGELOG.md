# LilacAudioTool changelog

## v1.1.0

- Removed the Python requirement for inspecting banks, looking up cues, extracting WAV files, previewing audio and exporting changes.
- Moved ACB/AWB editing to C#, including queued replacements, adding entries, renaming or deleting cues and redirecting cues to new audio.
- Moved audio analysis, clipping and loop previews to C# and kept the existing FFmpeg, vgmstream and HCA/ADX codec tools.
- Fixed failed preview decodes leaving incomplete WAV files in the cache.
- Packaged the HCA/ADX helper and GPL license with the self-contained Windows x64 and macOS ARM64 builds.
