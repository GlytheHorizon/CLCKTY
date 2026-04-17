# CLCKTY

CLCKTY is a lightweight global keyboard sound engine for Windows built with .NET 8, WPF, and NAudio.

## Features

- Global keyboard hook with low overhead (no key logging)
- Low-latency sound playback with preloaded/cached clips
- Built-in sound profiles plus custom sound pack import
- Optional per-key sound mapping
- System tray behavior:
  - Open panel
  - Toggle sounds on/off
  - Exit
- Start with Windows option
- Compact floating dark UI panel with fade-in animation

## Architecture

- Core
  - KeyboardHookService: Global key detection via SetWindowsHookEx
  - SoundEngine: NAudio playback, profile management, key mapping
- Services
  - TrayService: NotifyIcon and tray menu behavior
  - StartupService: Startup registration via HKCU Run key
- UI
  - MainWindow: Floating panel
  - MainViewModel: Binding and commands

## Safety

CLCKTY does not store or transmit keystrokes.
Key events only trigger sound playback.

## Build and Run

1. Open the solution: CLCKTY.slnx
2. Build in Visual Studio 2022 (or newer) targeting net8.0-windows.
3. Run the CLCKTY.App project.

From terminal:

- dotnet build CLCKTY.slnx -c Debug
- dotnet run --project src/CLCKTY.App/CLCKTY.App.csproj

## Custom Sound Packs

Use Import Pack in the UI and select a folder containing WAV files.

Examples:

- default.wav
- accent.wav
- heavy.wav

The file name becomes the clip label in the mapping dropdown.
If default.wav exists, it is used as the default clip for that profile.
