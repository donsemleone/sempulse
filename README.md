<div align="center">

<img src="src/Sempulse/Assets/app-icon.svg" width="96" alt="Sempulse icon" />

# Sempulse

**A lightweight system and media bar for Windows, with a pixel cat that lives on your desktop.**

[![Latest release](https://img.shields.io/github/v/release/donsemleone/sempulse?label=release)](https://github.com/donsemleone/sempulse/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/donsemleone/sempulse/total)](https://github.com/donsemleone/sempulse/releases)
[![CI](https://github.com/donsemleone/sempulse/actions/workflows/ci.yml/badge.svg)](https://github.com/donsemleone/sempulse/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)
![Windows 10 and 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)

**[⬇ Download for Windows](https://github.com/donsemleone/sempulse/releases/latest)** · English · [Türkçe](README.tr.md)

<img src="docs/media/widget.png" width="640" alt="Sempulse floating panel showing CPU, memory, GPU, VRAM, network, two media players, desktop buttons, weather and clock" />

</div>

Sempulse sits on the edge of your screen (or floats wherever you like) and shows what your PC is doing, what is playing, and what time it is. A small yellow cat walks along your monitors, climbs the walls, naps on your windows and chases your mouse pointer.

It is a single native app: no browser engine, no background services, no account, and nothing is collected.

## Features

### System at a glance
CPU, memory, GPU and VRAM meters plus live download and upload speed. Hover the VRAM meter to see used and total gigabytes. The GPU meter follows your busiest graphics engine, and VRAM follows the card with dedicated memory, so laptops with two GPUs show the one that is working.

### Two media players in one place
Spotify gets its own controls, and everything else (a YouTube tab, a podcast app, a video player) gets a second set. Prefer one control for whatever is playing? Switch to **single active media**. Long titles slide back and forth so you can read them.

### Edge bar or floating panel
Dock Sempulse to the top, bottom, left or right edge as a thin bar. Windows treats it like the taskbar, so maximized windows stop at its edge instead of hiding it. Or switch to the floating panel and drag it anywhere.

<img src="docs/media/bar-horizontal.png" alt="Sempulse as a horizontal bar at the top of the screen" />

<table><tr>
<td><img src="docs/media/bar-vertical.png" width="44" alt="Sempulse as a vertical bar" /></td>
<td>

### Gets out of the way of games and videos
When a game or video goes fullscreen on your main monitor, Sempulse moves to the lower left of your second monitor as a floating panel, and the cat hides. With one monitor it simply hides until you leave fullscreen. Screenshot tools are ignored, so taking a capture does not move anything.

### Clean up your desktop in one click
**Move** sends the files, folders and shortcuts on your main monitor's desktop to free spots on your second monitor. **Undo** puts them back where they were. Only the icon positions change; your files never move. If desktop icons are set to arrange automatically, Sempulse tells you what to switch off first.

### Weather and clock
The current temperature and conditions sit above the clock (currently for Istanbul). Hover for today's low, high and forecast.

</td>
</tr></table>

### The desktop cat

<img src="docs/media/cat-poses.png" alt="The pixel cat walking, sitting, yawning, sleeping, puffing up and jumping" />

The cat lives along the bottom of your monitors and has a mind of its own:

- walks, climbs up the screen edge, walks upside down along the top and comes down the other side
- travels between monitors, including monitors stacked on top of each other
- jumps onto the top edge of your windows and sits there
- sits, blinks, yawns, curls up to sleep and puffs up its fur
- sleeps more and walks less between 23:00 and 06:00
- notices your mouse pointer, chases it and pounces when it gets close
- can be picked up with the mouse and dropped onto any window

Seven buttons on the panel choose its mood: let it decide, walk, sit, sleep, jump, puff up, or play with the pointer. Clicks pass through everything except the cat's own pixels, and it can be turned off in the settings.

### Settings

<img src="docs/media/settings.png" width="320" alt="Sempulse settings window" />

Choose the layout and edge, media mode, cat on or off, start with Windows, bar thickness, floating width and background opacity (all the way down to fully transparent). Changes apply immediately and are remembered.

## Light on your PC

Sempulse is one process. On a typical desktop it uses about **100 MB of RAM** and well under **1% of total CPU** while idle (about 3% of a single core, most of it the sliding song titles and the cat's walk).

## Install

1. Download `Sempulse-<version>-win-x64.zip` from the [latest release](https://github.com/donsemleone/sempulse/releases/latest).
2. Unzip it anywhere, for example `C:\Apps\Sempulse`, and run `Sempulse.exe`. Nothing needs to be installed; .NET is included.
3. Optional: open the settings (gear icon) and turn on **Start with Windows**.

**Windows SmartScreen** may warn that the app is from an unknown publisher, because the download is not code signed. Choose **More info → Run anyway**. You can compare the zip with `SHA256SUMS.txt` from the same release, or build it yourself from source (below).

**Requirements:** Windows 10 (version 2004 or later) or Windows 11, 64-bit. The desktop icon buttons and the fullscreen move need a second monitor.

To uninstall, turn off **Start with Windows**, close the app from its settings, and delete the folder. Settings live in `%LOCALAPPDATA%\Sempulse`.

## Privacy and security

- **No data collection.** No telemetry, analytics, accounts or crash reporting.
- **One network request:** the weather, from [Open-Meteo](https://open-meteo.com), with fixed Istanbul coordinates and no API key. Nothing about you or your PC is sent.
- **Nothing listens.** Sempulse opens no ports and accepts no connections.
- **Local only.** Song titles, window positions and desktop icon positions are read on your PC and never leave it.
- **Small footprint on your system.** It writes only its own settings folder and, if you ask for it, one "start with Windows" entry under your user account. It never needs administrator rights.

Found a security issue? Please open a [private security advisory](https://github.com/donsemleone/sempulse/security/advisories/new) instead of a public issue.

## FAQ

**The panel does not move during fullscreen.** Fullscreen detection watches the primary monitor only. A window that covers the whole monitor (or a borderless game window) counts; a regular maximized window does not.

**A strip of the screen stays empty after Sempulse was force-closed.** The bar reserves that strip the way the taskbar does. A normal exit always gives it back; if the process was killed (for example from Task Manager), Windows frees it once Explorer notices, at the latest after you sign out and back in.

**Can I change the weather location or the language?** Not yet. The interface is in Turkish and the weather is for Istanbul; both are planned settings.

**How do I close it?** Settings → **Close app**, or run `Sempulse.exe --quit`.

## Build from source

You need Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
git clone https://github.com/donsemleone/sempulse.git
cd sempulse
dotnet test
dotnet run --project src/Sempulse
```

To produce the same single-file app as the releases:

```powershell
dotnet publish src/Sempulse -c Release -r win-x64 -o out
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for how the code is organised.

## License and credits

Sempulse is released under the [MIT License](LICENSE).

Icons are from [Lucide](https://lucide.dev) (ISC) and weather data from [Open-Meteo](https://open-meteo.com) (CC BY 4.0). See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
