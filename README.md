# Lilac Audio Tool

<p>
  <img alt=".NET 9" src="https://img.shields.io/badge/.NET-9-512BD4">
  <img alt="Windows, macOS and Linux" src="https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-blue">
</p>

**Lilac Audio Tool (LilacAT)** is a desktop application for working with ACB and AWB audio banks, built with **INAZUMA ELEVEN: Victory Road** and its newer v3 audio codec in mind.

It lets you browse and preview the audio stored in a bank, add, replace or remove entries, adjust loop points, and export the modified ACB/AWB files. You can also queue multiple replacements before exporting, making it easier to prepare audio mods.

## How to use

### 1. Open an audio bank

Click **Browse** in the **1. Bank** section and select the ACB or AWB file you want to edit.

In **INAZUMA ELEVEN: Victory Road**, these files are located in:

```text
/data/common/sound_asset/
```

Other Level-5 games may use different directories, such as:

```text
/data/nx/sound/
```

Once the bank has loaded, LilacAT displays the audio entries it contains.

### 2. Find and preview an entry

Select the entry you want to edit from the list. If you're not sure which one it is, click **Play** at the top of the window to listen to it first.

Previewing the audio is a good way to make sure you're replacing the right entry.

### 3. Replace the audio

In the **2. Entry** section, click **Replace** and choose the audio file you want to use. **WAV is recommended** for replacements.

You can repeat this for other entries and export your changes together.

### 4. Adjust the loop (optional)

If the replacement needs to loop, open the **3. Loop** section and select **Manual** from the loop mode dropdown.

- Drag the markers on the grey timeline to set the approximate start and end of the loop.
- Use the fields below the timeline to fine-tune the exact sample positions.

If you don't need a custom loop, you can skip this step.

### 5. Export your changes

Click **Export ACB/AWB**, choose an output folder, and LilacAT will generate the modified ACB/AWB files.

## Dependencies
LilacAudioTool uses [ffmpeg](https://ffmpeg.org/ffmpeg.html), [vgmstream](https://github.com/vgmstream/vgmstream) and [Python 3](https://www.python.org/downloads/). If any of these tools aren't installed (except Python), the app will prompt you to install them.

## Special thanks
- **DaRk_Proaso** — testing, bug reports, and improvement suggestions.

## License
G4_Blender is licensed under the **GNU General Public License v3.0 or later**. See [LICENSE.MD](LICENSE.MD) for details.
