# Contributing

Thanks for helping. Bug reports, ideas and pull requests are all welcome.

## Getting started

You need Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet test                         # unit tests
dotnet run --project src/Sempulse   # run the app
Sempulse.exe --quit                 # close a running instance cleanly
```

Only one instance runs at a time, so close the installed copy before running a development build.

## How the code is organised

| Folder | What lives there |
|---|---|
| `src/Sempulse/App.xaml.cs` | Starts the services, places the windows, reacts to fullscreen and display changes. |
| `src/Sempulse/Services/` | Everything that talks to Windows: performance counters, GPU/VRAM, media sessions (GSMTC), weather, fullscreen detection, the appbar that reserves the screen edge, desktop icon moves, settings, start with Windows. |
| `src/Sempulse/Layout/` | Pure functions that compute where the panel and settings window go. |
| `src/Sempulse/Views/` | The panel (built in code, one shape per layout) and the settings window. |
| `src/Sempulse/Controls/` | Small building blocks: meters, sliding text, Lucide icons. |
| `src/Sempulse/Pet/` | The desktop cat: a layered WinForms window on its own UI thread. |
| `tests/Sempulse.Tests/` | xUnit tests for everything that does not need a live desktop. |
| `tools/Sempulse.Screenshots/` | Renders the README images from the real panel code with demo data. |

Some guidelines:

- Keep Windows calls inside `Services/` and put the decisions they feed into small pure functions that can be tested.
- The panel window is transparent, so every animation frame repaints the whole window. Keep animations rare and cap their frame rate.
- The app must never need administrator rights, and must not send anything about the user over the network.
- The interface text is Turkish for now. If you add strings, keep them short and in the same style.

## Updating the README images

```powershell
dotnet run --project tools/Sempulse.Screenshots
```

This rewrites the PNG files in `docs/media/`.

## Releases

Releases are built by GitHub Actions. Pushing a tag such as `v2.0.1` runs the tests, publishes a self-contained `Sempulse.exe`, zips it with the license files and attaches it and its SHA-256 checksum to a new GitHub release.
