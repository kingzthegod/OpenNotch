# OpenNotch

A Windows notch-style desktop overlay with a compact status bar and an expandable dashboard for music, system usage, focus sessions, and quick notifications.

![Platform](https://img.shields.io/badge/platform-Windows%2010%20(19041%2B)%20%7C%20Windows%2011-0078D4)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![License](https://img.shields.io/badge/license-MIT-green)

> **Status:** Early development. The project is actively being built; features, appearance, and setup instructions may change.

## Screenshots

![OpenNotch expanded dashboard](docs/screenshots/open-notch-dashboard.png)

The screenshot above shows the current expanded dashboard, including the media player, CPU and RAM meters, focus timer controls, and animated cat.

## Features

- **Compact notch overlay** — a small always-on-top window with an expandable dashboard.
- **Hover and pin controls** — expand on hover and keep the dashboard open with the pin button.
- **Position controls** — drag the notch to move it, double-click to reset its position, and save its location between launches.
- **Windows media controls** — show the current media title, artist, and album artwork; previous, play/pause, and next controls use the active Windows media session.
- **Album-art styling** — use the artwork's dominant colour for the notch border and glow while media is playing.
- **CPU and memory monitor** — display CPU usage and physical memory usage.
- **Focus timer** — start 15-, 25-, or 45-minute sessions, stop a session, and receive an audible completion notification.
- **Quick notifications** — temporary feedback for volume changes and clipboard copies, plus a focus-complete message.
- **Animated cat** — a small animated companion in the expanded dashboard.
- **System tray menu** — start a 25-minute focus session, stop the timer, toggle clipboard notifications, reset the position, or exit.

Some features depend on Windows services or compatible applications. For example, media information and playback controls appear when an application publishes a Windows media session.

## Requirements

- Windows 10 version 2004 (build 19041) or later, or Windows 11.
- .NET 8 SDK.
- Git, to clone the repository.
- Internet access for the first restore of NuGet packages.

The project is a Windows-only WPF application and targets `net8.0-windows10.0.19041.0`.

### Dependencies

NuGet dependencies are declared in `OpenNotch.csproj`. The current project references:

- [NAudio 2.2.1](https://www.nuget.org/packages/NAudio/2.2.1) — Windows audio endpoint notifications.

Windows media controls use the Windows media session APIs.

## Get started

### 1. Clone the repository

Open PowerShell:

```powershell
git clone https://github.com/kingzthegod/OpenNotch.git
cd OpenNotch
```

### 2. Restore dependencies

```powershell
dotnet restore
```

### 3. Build

```powershell
dotnet build -c Release
```

### 4. Run

```powershell
dotnet run
```

To launch the Release build directly after building:

```powershell
.\bin\Release\net8.0-windows10.0.19041.0\OpenNotch.exe
```

## Build with Visual Studio

You can also open `OpenNotch.csproj` in Visual Studio 2022 with the .NET desktop development workload installed. Restore NuGet packages, select a Windows build configuration, and build the project.

Visual Studio Code can be used with the C# tooling and the .NET 8 SDK installed.

## Configuration and local data

OpenNotch stores its window position in:

```text
%AppData%\OpenNotch\settings.json
```

Build output and local-only files are excluded through `.gitignore`.

## Troubleshooting

**`dotnet` is not recognized**

Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), open a new terminal, and run `dotnet --info`.

**NuGet restore fails**

Check your internet connection and run:

```powershell
dotnet restore
```

**No track appears in the player**

Start playback in an application that publishes a Windows media session. Not every media player exposes one.

**Windows blocks the executable**

A locally built or self-signed executable may be blocked by Windows application-control policies. This repository does not currently provide a Microsoft-trusted signed installer or release binary. Do not disable Windows security protections just to run an untrusted build.

## Contributing

Issues and pull requests are welcome. Before submitting a change:

1. Keep changes focused and describe the user-visible effect.
2. Build the project with `dotnet build -c Release`.
3. Include screenshots for UI changes when possible.
4. Never commit private keys, signing certificates, passwords, or other secrets.

## License

OpenNotch is licensed under the [MIT License](LICENSE).
