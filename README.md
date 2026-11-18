# MouseBatteryTray

Tiny Windows tray app that shows the battery level of an Attack Shark X8 Ultimate (and likely other CompX-based mice: VXE, VGN, ATK, Redragon, Zaopin...) as a number right in the tray. No vendor software needed.

- Pixel-art digits, crisp at 16 px; integer-scaled on high DPI
- Color: white/black (follows taskbar theme), orange ≤30%, red at the warning threshold, green while charging, gray when the mouse is asleep
- Instant update when charging starts or stops (receiver push report)
- Tooltip: device name, percent and estimated hours left
- Low battery and "fully charged" notifications, configurable warning threshold
- Autostart toggle, diagnostics dump
- English and Ukrainian UI (follows the Windows language, switchable in the menu)

## Compatibility

The app is not tied to Attack Shark. It talks to any mouse whose receiver uses the CompX chipset: USB vendor ID `0x3554` with a vendor HID collection on usage page `0xFF02` and 17-byte reports. The device name in the tooltip is read from the device itself.

| Mouse | Status |
|---|---|
| Attack Shark X8 Ultimate | Tested (receiver `F517`, cable `F515`) |
| Attack Shark X8 Ultra | Should work, same hardware and same official web driver as the Ultimate |
| VGN Y2 Ultra | Should work, same protocol confirmed by other projects |
| Other VGN / VXE / ATK mice on CompX receivers | Likely, untested. Some models only push battery on their own and may not answer the query |
| Redragon M916 ULT / M916 PRO 1K | Should work, same protocol confirmed by other projects |
| Scyrox V6, Zaopin Z4D | Should work, same protocol confirmed by other projects |
| Attack Shark X8 SE / Plus / Pro | Not supported: different chip (Beken), the mouse pushes battery on its own in another format |
| Attack Shark X11 | Not supported: different vendor ID (`1d57`), no output reports |

The percent is calculated from voltage with the Attack Shark battery curve. For other brands the number may differ a few percent from their own software.

If your mouse is not listed, run the app and open **Diagnostics** from the tray menu. If you see a `0xFF02` interface with `in=17 out=17` and a `RESULT` line with a percent, it works. Feel free to open an issue with the diagnostics output either way.

## Build

.NET Framework 4.8 (ships with Windows 10/11), no NuGet runtime dependencies.

```
dotnet build -c Release
```

Output: `bin\Release\net48\MouseBatteryTray.exe`

## Protocol notes

The receiver (VID `0x3554`, PID `F517`) and the mouse on a cable (PID `F515`) expose a vendor HID collection: usage page `0xFF02`, usage `0x02`, 17-byte reports with report ID `0x08`. The last byte is a checksum so that all 17 bytes sum to `0x55`.

| Direction | Bytes | Meaning |
|---|---|---|
| Request | `08 04 00 .. 00 49` | Battery level |
| Reply | `08 04 <status> 00 00 <len> <percent> <charging> <mV hi> <mV lo> ..` | status != 0 means the mouse is asleep or offline |
| Push | `08 0A 00 00 00 0A 40 ..` | StatusChanged, sent by the receiver on its own, e.g. when the cable is plugged in |

Percent is computed the same way as the official Attack Shark hub: millivolts mapped through a 21-point curve (3050 to 4110 mV, 5% steps), then rate-limited (up to 0.028 %/s rise, 0.014 %/s fall) so the value doesn't jump when the charger inflates the voltage.
