![Build](https://github.com/romangobb/nothing-buds-windows/actions/workflows/build.yml/badge.svg)
![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)
![Stars](https://img.shields.io/badge/stars-1-ff69b4?logo=github)
![Platform: Windows 10/11](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4?logo=windows)
![Release: v1.0.0-beta](https://img.shields.io/badge/release-v1.0.0--beta-orange)

# NothingBuds for Windows

A small desktop app that controls Nothing and CMF earbuds straight from your
Windows PC — battery, noise cancelling, EQ and more, with no phone, no account
and no internet connection.

![Unofficial](https://img.shields.io/badge/unofficial-not%20affiliated%20with%20Nothing-informational)

## What it does

- See the battery of both earbuds and the case, right on your desktop.
- Turn noise cancelling on and off, or switch to Transparency, and pick how
  strong it is.
- Change the sound: pick an EQ preset, or move the bass / mid / treble faders
  yourself.
- Ring the left or right earbud to find it, and switch between two pairs of
  buds if you own two.
- Stays out of your way: lives in the tray, starts with Windows if you want,
  reconnects on its own if the connection drops.

> **Unofficial.** This is a community project. It is not affiliated with,
> sponsored by or endorsed by Nothing Technology Limited. It is not a
> firmware updater — use the official app for that.

## Requirements

- Windows 10 or 11 with Bluetooth
- Your earbuds, paired once in Windows Bluetooth settings (case open, hold the
  setup button ~2 s until it blinks white, then *Add device*)
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)

## Install

Grab the latest build from the
[Releases page](https://github.com/romangobb/nothing-buds-windows/releases),
unpack it anywhere, and run `NothingBuds.exe`.

Or build it yourself — the app has no third-party libraries at all:

```powershell
git clone https://github.com/romangobb/nothing-buds-windows
cd nothing-buds-windows
dotnet publish NothingBuds.csproj -c Release --no-self-contained -o publish
powershell -ExecutionPolicy Bypass -File install.ps1 -Autostart
```

## Usage

1. Launch the app, press **Add device**, wait for your earbuds to show up and
   double-click it. That is all — every launch after that connects
   automatically.
2. Set your ANC level and EQ from the dashboard. Custom EQ sliders appear when
   you pick the *Custom* preset.
3. Lost a bud? Press **Ring left** or **Ring right**. **Stop** cancels.

The app lives in the system tray: the window is centred when you click the tray
icon, and closing it just hides it. Use **Quit** in the tray menu to exit.

## Supported devices

Detected automatically by Bluetooth name — no model picker. Profiles for
Nothing Ear (1) / (stick) / (2) / (a) / (3) / (3a) / Ear / Ear (open), plus CMF
Buds, Buds Pro, Buds 2, Buds 2a, Buds 2 Plus, Buds Neo, Buds Pro 2, Neckband Pro,
Clip Pro and the Nothing headphones.

**Status:** v1.0.0-beta. Live-tested on **CMF Buds Pro 2 (B172, firmware
1.0.1.74)**. Other models use the same packet family and are expected to work,
but are not individually verified — please open an issue with your model if
something misbehaves. If a feature is missing for your model, the app hides it
rather than failing.

**Not included:** firmware updates, and phone-side spatial audio / LDAC.

## Troubleshooting

- *No device found* — the buds must be paired and awake: out of the case, or in
  the case with the lid open. Re-pair them in Windows settings if you carried
  them over from the web app.
- *Connect fails* — only one control connection at a time. Close the other
  Nothing app (or a second copy of this one). Pausing audio on your phone helps.
- *Nothing happens on a COM port* — expected. This app does not use serial COM
  ports at all.

Logs are written to `%AppData%\NothingBuds\app.log` — attach them to bug
reports.

## Contributing

Issues and pull requests are welcome, especially reports from people whose model
is not CMF Buds Pro 2. Protocol notes live in [PROTOCOL.md](PROTOCOL.md) if you
want to help with new models or features.

The app deliberately has **zero third-party dependencies** — please keep it that
way, and keep the build working offline.

## License

MIT — see [LICENSE](LICENSE).

## Credits and assets

The product renders and the Ndot typeface bundled in `Assets/` were taken from
the official Nothing app, and are the part of this project I am least happy
about. Nothing has every right to ask for them to be removed, and they will be
if they do. Everything else here — the protocol implementation and the app — was
written from scratch, with reference to the open-source `ear-web` and `ear-pc`
projects and to traffic observed on my own earbuds.