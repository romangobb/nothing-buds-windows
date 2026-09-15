# NothingBuds (unofficial)

A fast, **fully local** Windows companion for Nothing / CMF earbuds. No servers, no websites:
talks to the buds directly over Bluetooth Classic RFCOMM.

Primary target (live-verified): **CMF Buds Pro 2** (B172, firmware 1.0.1.74).

> Unofficial project. Not affiliated with, sponsored by, or endorsed by
> Nothing Technology Limited. Protocol knowledge was rebuilt from the open-source
> [ear-web](https://github.com/radiance-project/ear-web) and
> [ear-pc](https://github.com/radiance-project/ear-pc) projects plus live traffic
> observation. No vendor code or assets are included.

## Features (MVP)

- **System tray app**: runs in the background; click the tray icon to open the
  main window **centered** on your screen. Closing the window hides it to the tray
  (only *Quit* in the tray menu exits).
- **Auto-connection, no manual selection**: pair once in Windows Bluetooth settings,
  press *Add device* once in the app (remembered list in `%AppData%\NothingBuds\devices.json`).
  Every later launch connects automatically.
- **Multi-device toggle**: with two pairs connected, a device switcher appears
  (mobile-app style) to flip the active pair whose settings are shown.
- **Device dashboard**: battery (L/R/Case + charging), ANC circles +
  strength bar, EQ presets, Ultra bass stepped switch (tap = exact level),
  in-ear detection, low latency, personalized ANC (Ear 2), firmware,
  ring-to-find (L/R/stop), adaptive Refresh (top-right). Device toggles sit
  beside the Equaliser; app options (start with Windows) live in a Controls
  card at the bottom.
- **Start with Windows** toggle (HKCU Run key, `--minimized`).

## Requirements

- Windows 10/11 with Bluetooth, buds **paired** in Windows settings
  (case open, hold setup button ~2 s until white blink, then *Add device*).
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
  (only needed for the framework-dependent build in `publish/`).
- No NuGet packages, no network access — builds fully offline with the .NET 9 SDK.

## Install / run

```powershell
# build
dotnet build NothingBuds.csproj -c Release
# publish (framework-dependent, no network needed)
dotnet publish NothingBuds.csproj -c Release --no-self-contained -o publish
# install (copies to %LocalAppData%\Programs\NothingBuds + Start Menu shortcut)
powershell -ExecutionPolicy Bypass -File install.ps1 -Autostart
```

Or just run the dev build: `bin\Release\net9.0-windows\NothingBuds.exe`.
Logs: `%AppData%\NothingBuds\app.log`.

First launch: window opens (nothing remembered yet) → *Add device…* → wait for
your buds to appear → double-click to remember + connect. Done — next launches
are automatic.

## Design

Dark Nothing device-screen theme rebuilt from the official app: pure-black
background (incl. dark title bar), `#1B1D1F` cards with 24dp radius, white
selected states, white toggles, signal red `#D71921` as accent only.
ANC is mode circles (Noise Cancellation / Transparency / Off) plus a
Low·Mid·High·Adaptive level bar — the same two-dimensional model as the mobile
app. Hero card uses the official buds renders (`res/drawable/espeon_*`),
Georgia serif for the device name, NDot 55 for the wordmark and digits.
Text color is inherited from the window (white), never forced globally, so
labels stay readable on every surface — including ComboBox dropdowns, list
selections, and tooltips.

## Supported devices

All 22 audio products from the official app's catalog (`devices_info_list.json`),
identified automatically by Bluetooth name — no manual model picking:

Nothing Ear (1) · Ear (stick) · Ear (2) · Ear (a) · Ear · Ear (open) · Ear (3) ·
Ear (3a) · CMF Buds Pro · CMF Buds · CMF Buds 2 · CMF Buds 2 Plus · CMF Buds 2a ·
CMF Buds Pro 2 · CMF Buds Neo · CMF Neckband Pro · CMF Clip Pro ·
Nothing Headphone (1) · Headphone (a) · CMF Headphone Pro.

Per-model capability profiles (`Bluetooth/DeviceCatalog.cs`, flags mined from
the official `ear_white_list.json`) gate ANC / in-ear / Ultra bass / ear-tip
test / personalized-ANC UI, switch EQ preset lists, and pick bundled product
renders with colorways. The EQ wire flavor (Dirac listening vs legacy presets)
is **auto-detected at runtime** — whichever of `0x4050` / `0x401F` answers wins —
so new firmware and refresh models keep working. Unknown names fall back to a
generic TWS profile; the battery handshake remains the real gatekeeper.
Out of scope: firmware updates, spatial-audio/LDAC phone-side toggles.

Colorways are picked once via the hero dots and remembered per device
(\SelectedColor\ in \devices.json\). Auto-detection is not possible: the buds
serial reply identifies old models only (live-tested: a Buds Pro 2 reports a
serial the old ear-web table misreads as Ear (2)), and newer serials carry no
color code - Bluetooth names do not either.

## Link supervision

A 5 s watchdog re-dials remembered devices whenever the RFCOMM link drops
(buds taken by the phone, case closed, radio hiccup): controls grey out with
a "Disconnected — retrying…" status instead of silently dying, failed sends
flip the device into reconnecting rather than throwing, and Refresh on a dead
link reconnects immediately. Drop/reconnect events go to `app.log`.

## How it works (protocol notes)

- Transport: Bluetooth Classic **RFCOMM, channel 15** (NOT the generic `COMx`
  Serial-Port Profile — that port stays silent). Opened via raw Winsock `AF_BTH`
  (`ws2_32.dll`, no dependencies); CPython's `socket(AF_BLUETOOTH, SOCK_STREAM,
  BTPROTO_RFCOMM)` + `connect((mac, 15))` behaves identically.
- Windows shows the buds' address **byte-reversed** in `BTHENUM\DEV_…` PnP ids
  (e.g. `DEV_A73F70EEBE2C`); the real MAC is `2C:BE:EE:70:3F:A7`
  (as in the COM-port HWID and `HKLM\…\BTHPORT\Parameters\Devices`, whose `Name`
  value gives the friendly name). Stale reversed/LE entries exist — the app
  confirms candidates with a live battery handshake.
- Packet: `55 60 01 <cmdLo> <cmdHi> <len> 00 <opId> <payload> <crcLo> <crcHi>`,
  CRC16-Modbus (`init 0xFFFF`, `poly 0xA001`), `opId` 1…249 then wraps.
- **CRC asymmetry** (found live, ear-web never checks RX CRC):
  requests cover *header+payload*, responses cover *payload only*
  (empty payload ⇒ `FFFF`). Example: battery request
  `55600107C0000001ACDF` → response `556001074005000102025503504CA4`
  (`02` buds: L `0x55`=85 %, R `0x50`=80 %).
- Command families: `GET=0xC0xx`, `SET=0xF0xx`, `RESP=0x40xx`, `EVENT=0xE0xx`.
  Key IDs: battery `C007`, listening/EQ `C050`→`4050`, firmware `C042`,
  ANC `C01E`→`401E` (wire `05/07/03/01/02/04` = Off/Transp/Low/High/Mid/Adapt),
  bass `C04E/F051` (`[en, level*2]`), gestures `C018`, in-ear `C00E`
  (status at payload index 2), latency `C041/F040`, ring `F002`
  (`[02=L|03=R, 01|00]`), ear-fit test `F014`→event `E00D`.
- Init order (100–120 ms gaps): battery → listening → firmware → in-ear →
  latency → gestures → ANC → advanced-EQ → bass.

## Layout

```text
NothingBuds/
  Bluetooth/  Protocol.cs (framing, CRC, command + ANC/EQ enums)
              RfcommClient.cs (raw Winsock AF_BTH + reader/resync)
              EarDevice.cs (B172 state, init sequence, setters)
  Services/   DeviceManager.cs (remembered list + auto-connect + active pair)
              PairedScanner.cs (BTHPORT registry enumeration)
              NothingProbe.cs (battery handshake = "is a Nothing device")
              AutoStart.cs (HKCU Run)  Log.cs (file log)
  App.xaml(.cs)        tray icon, centered window, lifecycle
  MainWindow.xaml(.cs) dashboard + device switcher
  install.ps1 / uninstall.ps1 / installer.iss (Inno, optional)
```

## Roadmap

- Gesture editor UI (protocol mapped, read-only summary today)
- Custom EQ curves (`F041` 53-byte float payload, see ear-web `formatFloatForEQ`)
- More models (B155/B162/B168/B171 share the same packet family)
- MSIX packaging, richer tray flyout, per-device ANC memory
- What we will NOT do: firmware updates (brick risk — use the official mobile app)

## Troubleshooting

- *Add device finds nothing*: buds must be paired **and awake** (out of case or
  case open). Re-pair in Windows settings if the web app era left stale entries.
- *Connect fails*: only one control connection at a time — close ear-web/ear-pc
  or a second copy of this app. Dual-point to a phone can also refuse (pause phone audio).
- *No response on COMx*: expected — this app does not use SPP COM ports.
