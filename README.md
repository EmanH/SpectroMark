# SpectroMark

A tiny Windows tool for dropping markers on WAV files while watching a full Adobe-Audition-style spectrogram.

Open a file, press **Space** to play, press **M** to drop a marker. That's it.

## Features
- Detailed heat-map spectrogram (2048-point FFT), one band per channel, so you can see at a glance whether a file is mono or stereo
- Overview waveform across the top with a draggable view window
- Scrub with the left mouse button, zoom with the mouse wheel, pan with Shift+wheel or middle-drag
- Bright flashing marker line when you hit **M**; right-click a marker and drag to move it
- Marker list with time positions; **Delete** removes the selected marker
- Markers are saved next to the WAV as `<name>_markers.csv` in Adobe Audition's tab-separated marker format (importable in Audition's Markers panel) and reloaded automatically next time you open the file
- Log/linear frequency axis, adjustable dB floor
- Opens WAV, FLAC, MP3, AIFF; drag-and-drop or pass a file on the command line

## Keys
| Key | Action |
|---|---|
| Space | Play / pause |
| M | Drop marker at playhead |
| Home / End | Jump to start / end |
| ← / → | Nudge 1 s (Shift: 0.1 s) |
| + / - | Zoom in / out |
| Delete | Remove selected marker |
| Ctrl+S | Save markers |

## Build
Requires the .NET 10 SDK.

```
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The single-file exe lands in `bin/Release/net10.0-windows/win-x64/publish/`.
