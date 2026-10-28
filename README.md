# BatteryTray

**English** · [Türkçe](README.tr.md)

A small Windows tray app that shows the battery level of your wireless headset, mouse,
keyboard and AirPods. No vendor software needed (iCUE, Armoury Crate, GG …) — it talks to
the devices directly over USB HID and Bluetooth LE.

## Features

- **Tray icon:** the headset's battery percentage, drawn with pixel-perfect digits that stay
  readable even at 16 px. While a device is charging, the icon alternates between the
  percentage and a green icon of the charging device.
- **One tray icon per device:** right-click → **Show in tray** and tick the devices you want.
  Each ticked device gets its own icon with its own percentage (like separate sensors in
  HWiNFO); hover an icon for the device name. Untick everything to go back to the single
  headset icon. On Windows 11 new tray icons start in the `^` overflow: drag them onto the taskbar.
- **Battery panel:** click the icon to open a dark panel right above it.
  - *My devices:* name, connection type, percentage and a battery bar for each connected device.
    Devices that aren't reporting (a receiver plugged in while the device is off) are not listed,
    except an HS80 receiver without its headset, which says so. A device that fell asleep keeps
    showing its last reading.
  - *Nearby:* Bluetooth LE devices around you; left, right and case battery for AirPods/Beats
  - *Mouse details:* ATK/VXE mice also show the active DPI stage and the polling rate
- **Keyboard lighting (ROG Azoth):** right-click → **Keyboard lighting…** to set the effect, color,
  brightness and speed without Armoury Crate. Nothing is sent until you press **Apply**; with
  *Save on the keyboard* ticked the setting stays after BatteryTray is closed.
- **Low battery alerts:** Windows notification below 10 % and 5 % (once per device).
- **Start with Windows:** toggle from the right-click menu, no admin rights required.
- **English / Turkish UI:** English by default; switch to Turkish (or follow the Windows
  display language) from right-click → **Language**.

## Supported devices

| Type | Devices | Notes |
|---|---|---|
| Headset | Corsair HS80 MAX, Virtuoso MAX, Void Wireless V2 | 2.4 GHz receiver or USB cable |
| Mouse | ASUS ROG wireless mice (Gladius III, Harpe, Keris, Chakram, Spatha …) | Receiver or USB cable |
| Mouse | ATK VXE Dragonfly R1 series (R1 Pro Max, R1 SE+ …) | *Experimental.* Receiver or USB cable; battery, active DPI and polling rate. DPI / polling rate are only shown when the mouse's settings can be read and verified, see below |
| Keyboard | Keychron K2 Max | **Requires patched firmware**, see [`firmware/keychron-k2-max`](firmware/keychron-k2-max) |
| Keyboard | ASUS ROG wireless keyboards: ROG Falchion, and keyboards on the Omni Receiver (e.g. Strix Scope II 96 Wireless) | Receiver or USB cable; other ROG keyboards untested |
| Keyboard lighting | ASUS ROG Azoth, Azoth Extreme, Azoth Extreme SE, Azoth X | *Experimental.* USB cable or Omni receiver (not Bluetooth); see below |
| Earbuds | AirPods, AirPods Pro, Beats | From Bluetooth LE advertisements, 10 % steps |
| Mouse / keyboard | Logitech wireless devices via Lightspeed / Unifying / Bolt (e.g. G305) | *Experimental.* Devices that only report a voltage or level are shown as approximate (~) |

Brands not listed (Razer, SteelSeries, HyperX …) each use their own protocol and
won't show up yet. Contributions welcome.

**ATK / VXE mice:** the battery query is the one other projects have verified on real hardware (VXE R1 Pro Max).
The mouse stores every setting together with a checksum, so BatteryTray only shows DPI and polling rate when the
values read back valid; on a model whose memory layout differs they are simply left out rather than guessed.
If yours shows a battery but no DPI / polling rate, run **Diagnostics** and attach the log (it contains a raw,
read-only dump of the mouse's settings memory) to an issue. BatteryTray never writes to the mouse.

**ROG Azoth lighting:** the one thing BatteryTray ever writes to a device is the Azoth's lighting, and only when you
press **Apply** or **Lights off** (a lighting command, plus a "save" command if *Save on the keyboard* is ticked;
nothing else is sent). The protocol comes from G-Helper and has not been checked by this project on every Azoth model.
Wave, Ripple and Quicksand aren't offered because their packet layout isn't known. Bluetooth can't be controlled,
since Windows exposes no control channel for it. Armoury Crate / Aura Sync running at the same time can interfere:
close them if **Apply** reports no answer. A keyboard on the Omni receiver may be asleep; press a key and try again.
Other ROG keyboards on the Omni receiver use the same command and will probably work, but are untested.

## Installation

1. Download `BatteryTray.exe` from the [Releases](../../releases) page.
2. Run it. The icon appears in the bottom-right corner (if you don't see it, click the `^`
   arrow and drag it onto the taskbar).
3. Optionally: right-click → **Start with Windows**.

No .NET install needed; the exe is self-contained.

> On first launch Windows SmartScreen may show an "unrecognized app" warning:
> **More info → Run anyway**. The exe isn't signed; the source code is in this repo.

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o out
.\out\BatteryTray.exe
```

## Troubleshooting

From the right-click menu:

- **Diagnostics:** writes every connected HID device (vendor, product, interfaces) and the
  responses of supported brands to the log, then opens it.
- **Open log:** opens `batterytray_log.txt` — device discovery and error details.

To request support for a new device, run **Diagnostics** with the device connected and
attach the log to an issue.

## Credits

Protocols were learned from these open-source projects:

- [HeadsetControl](https://github.com/Sapd/HeadsetControl) — Corsair wireless headset protocol
- [hs80-max-control](https://github.com/jacohallum/hs80-max-control) — HS80 MAX battery and charge properties
- [BatteryTool](https://github.com/1gcat/BatteryTool) and [G-Helper](https://github.com/seerge/g-helper) — ASUS ROG mouse battery query; G-Helper also for the ROG Azoth lighting protocol
- [battery-tray](https://github.com/mikgangal/battery-tray) — ASUS ROG keyboard (Omni Receiver) battery query
- [k10he-battery-firmware](https://github.com/OutersGIT/k10he-battery-firmware) — Keychron 2.4 GHz battery patch
- [OpenPods](https://github.com/adolfintel/OpenPods) — AirPods Bluetooth LE advertisement format
- [Solaar](https://github.com/pwr-Solaar/Solaar) — Logitech HID++ battery features
- [HaloBattery](https://github.com/HeyOkay/HaloBattery) — VXE R1 Pro Max battery query and USB IDs
- [python-pulsar-mouse-tool](https://github.com/andrewrabert/python-pulsar-mouse-tool) — Compx frame format and settings memory map (DPI, polling rate)
- [OpenMouse](https://github.com/OpenMouse-Project/mouse-protocol) — ATK/VXE protocol notes used as a cross-check

## License

[GPL-3.0](LICENSE). The Keychron firmware patch is a QMK derivative and is GPL-2.0-or-later.
This project is not affiliated with Corsair, ASUS, Keychron, Logitech, Apple, ATK, VXE or SteelSeries.
