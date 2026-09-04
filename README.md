# SpectroMark

A tiny Windows tool for dropping markers on WAV files while watching a full Adobe-Audition-style spectrogram.

Open a file, press **Space** to play, press **M** to drop a marker. That's it.

## Features
- Detailed heat-map spectrogram (2048-point FFT), one band per channel, so you can see at a glance whether a file is mono or stereo
- Overview waveform across the top with a draggable view window
- Scrub with the left mouse button, zoom with the mouse wheel, pan with Shift+wheel or middle-drag
- Bright flashing marker line when you hit **M**; right-click a marker and drag to move it
- Marker list with time positions; **Delete** removes the selected marker
- Markers are read from and written into the WAV file itself (standard RIFF `cue` + `LIST/adtl` chunks), so they show up in Adobe Audition, Reaper, Sound Forge and any other editor, and markers set in those tools show up here
- Log/linear frequency axis, adjustable dB floor
- Open or drag in many files at once; they appear in a resizable file list on the left. Click to switch between them, unsaved files are marked with *, and Save / Save All / Close work per file
- Opens WAV, FLAC, MP3, AIFF; drag-and-drop files or folders, or pass files on the command line

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
