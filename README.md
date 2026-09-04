<p align="center"><img src="assets/logo.png" width="160" alt="SpectroMark logo"></p>

# SpectroMark

**[Download the latest release](https://github.com/EmanH/SpectroMark/releases/latest)** (Windows x64, no install)

A tiny Windows tool for dropping markers on WAV files while watching a full Adobe-Audition-style spectrogram.

Open a file, press **Space** to play, press **M** to drop a marker. That's it.

## Features
- Detailed heat-map spectrogram (2048-point FFT), one band per channel, so you can see at a glance whether a file is mono or stereo
- Overview waveform across the top with a draggable view window
- Scrub with the left mouse button, zoom with the mouse wheel, pan with Shift+wheel or middle-drag
- Bright flashing marker line when you hit **M**; click and drag a marker to move it
- Marker list with time positions; **Delete** removes the selected marker
- Markers are read from and written into the WAV file itself (standard RIFF `cue` + `LIST/adtl` chunks), so they show up in Adobe Audition, Reaper, Sound Forge and any other editor, and markers set in those tools show up here
- **AutoMark**: one click detects word starts for you. It combines a consonant detector (S, T, D, K bursts in the 3.5 to 11 kHz band, marker placed at the centre of the burst), a vibrato-tolerant spectral-flux onset detector, and a pitch-step detector that catches a held vowel changing note. Results are deterministic, so the same file always gives the same markers. Undo removes them all in one step.
- Playback speed slider from 1.0x to 2.0x in 0.1 steps, pitch preserved (SoundTouch time-stretch)
- Log/linear frequency axis, adjustable dB floor
- Open or drag in many files at once; they appear in a resizable file list on the left. Click to switch between them, unsaved files are marked with *, and Save / Save All / Close work per file
- Opens WAV, FLAC, MP3, AIFF; drag-and-drop files or folders, or pass files on the command line

## Sync mode

Switch to **Sync** in the toolbar to line up several takes against each other.

- Add or drag in the clips. Each becomes a lane showing its waveform and the markers embedded in the file.
- Drag a lane sideways to rough-align it. Click a lane to seek, Space to play the mix, M / S buttons mute and solo.
- Hold **Ctrl** and click one marker per lane. The group's sync time is the centre of all clicked markers (trimmed mean, one clear outlier dropped) and it is recomputed on every click, so every lane moves toward the middle of the group rather than to whichever you clicked first. Release Ctrl and repeat for the next group. One Ctrl+Z undoes a whole group.
- The first sync point on a lane moves the clip; later ones time-stretch the audio between sync points (Reaper-style stretch markers). Outside the sync points the audio is untouched. Alt+click a synced marker to remove its sync point.
- Stretch engine presets: Balanced, Tonal (default, best for voices), Transient, Smooth. Only the segments a new sync point touches are re-rendered, in the background.
- **Export Synced WAVs** writes a 24-bit WAV per lane aligned on the common timeline, with markers carried over, plus a stereo mix.
- Sessions (offsets and sync points) can be saved and reopened as `.spectrosync.json`.

## Keys
| Key | Action |
|---|---|
| Space | Play / pause |
| M | Drop marker at playhead |
| Home / End | Jump to start / end |
| ← / → | Nudge 1 s (Shift: 0.1 s) |
| + / - | Zoom in / out |
| Delete | Remove selected marker |
| Ctrl+Z / Ctrl+Shift+Z (or Ctrl+Y) | Undo / redo marker changes (per file) |
| Ctrl+S | Save markers into the WAV |

The published exe is compressed (about 70 MB); it bundles the whole .NET runtime so nothing needs installing.

## Build
Requires the .NET 10 SDK.

```
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The single-file exe lands in `bin/Release/net10.0-windows/win-x64/publish/`.
