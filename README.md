# CLCKTY

CLCKTY is a Windows desktop utility that plays keyboard and mouse sounds globally.
It is built with .NET 8, WPF, and NAudio, and designed as a lightweight background app with low-latency playback.

## What CLCKTY Does

- Captures global keyboard and mouse input (without storing typed text)
- Plays cached sounds with low-latency routing
- Supports separate keyboard and mouse profiles
- Supports per-input custom mappings (key/mouse to specific sound)
- Supports custom pack import and per-mapping audio upload
- Runs in tray mode with quick toggle/open/exit actions
- Supports startup with Windows
- Includes an in-app updater (GitHub release scan/download/apply)

## Inspiration and Credits

This project is inspired by:

- Keeby
- MechVibes

Important attribution:

- Stock sounds and bundled soundpacks are sourced from MechVibes and other community soundpacks.
- I do not claim ownership of those sound assets.
- The CLCKTY application code/implementation is created by me.

## Safety

CLCKTY does not record, store, or transmit your keystrokes.
Input events are used only to trigger local sound playback.

## Build and Run

1. Open solution: `CLCKTY.slnx`
2. Build in Visual Studio 2022+ targeting `net8.0-windows`
3. Run `CLCKTY.App`

Terminal commands:

- `dotnet build CLCKTY.slnx -c Debug`
- `dotnet run --project src/CLCKTY.App/CLCKTY.App.csproj`

## Auto Update (GitHub Releases)

Settings -> Scan for Updates checks the latest GitHub release and can download + stage install automatically.

Default updater target:

- `https://github.com/GlytheHorizon/CLCKTY`

Optional updater overrides:

- `CLCKTY_GITHUB_OWNER` (use a different owner)
- `CLCKTY_GITHUB_REPO` (use a different repository)
- `CLCKTY_GITHUB_TOKEN` (needed for private repositories)

Release requirement:

- Publish release assets as `.zip` packages containing the app files.

Update flow:

1. Scan for updates
2. If newer version found, app asks to download
3. App downloads and stages update
4. App asks for restart
5. On restart, updater script applies files and relaunches CLCKTY

## License

This project is open source under the MIT License.
See `LICENSE` for details.
