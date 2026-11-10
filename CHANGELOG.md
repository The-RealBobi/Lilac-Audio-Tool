# LilacAudioTool changelog

## v1.1.0

- Removed the Python installation requirement. Bank inspection, cue lookup, WAV extraction, previews and export now run from the C# app.
- Ported the full ACB/AWB editing workflow, including queued replacements, adding entries, renaming and deleting cues, redirecting cues to new audio, and updating the paired bank metadata.
- Ported audio analysis, clipping and loop previews. FFmpeg, vgmstream and the bundled HCA/ADX helper continue to handle audio conversion and decoding.
- Validate a decoded preview before putting it in the preview cache. Failed decodes can be retried without reusing a partial WAV.
- Build self-contained packages for Windows x64 and macOS ARM64 with the .NET SDK, including the HCA/ADX helper and GPL license.
