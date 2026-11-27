<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="brand/fluxgpu-logo-for-dark-backgrounds.svg">
    <img src="brand/fluxgpu-logo-for-light-backgrounds.svg" alt="FluxGPU" width="520">
  </picture>
</p>

A small floating GPU monitor for Windows 11 that shows an NVIDIA compute card and AMD integrated graphics side by side.

It was made for a Tesla P40, which runs in TCC mode and so normally does not appear in Task Manager.

Version 0.2.0, with the setup wizard from 0.3.0-dev. See [CHANGELOG.md](CHANGELOG.md).

## What it shows

| NVIDIA card (read through `nvidia-smi`) | AMD graphics (read from Windows) |
|---|---|
| Load with a 60-sample graph (gaps where data was missing) | Load with a 60-sample graph |
| Memory used and total | Shared memory used and total (what an iGPU really uses) |
| Temperature, and degrees left before the GPU's own slowdown temperature | Temperature, if the driver reports it |
| Power draw against the power limit, in watts | Dedicated memory (the BIOS carve-out) |
| Core clock | "Drv power": the raw driver percentage (meaning unverified, never alerted on) |
| PCIe link now and its maximum generation, and TCC or WDDM mode | Busiest engine |
| Throttle badges: HW THERMAL, SW THERMAL, POWER BRAKE, HW SLOWDOWN | |

- Stays on top of other windows (the pin button turns this off)
- Full and compact views
- Follows the Windows light or dark mode and accent color
- Remembers its position, pin and view
- Runs without admin

## Alerts

FluxGPU alerts with a sound, a flashing taskbar button for critical alerts, a coloured window border and a line at the top of the window. Every alert is also written to the log. There are no toast notifications yet; they need a tray icon, which is planned for a later version.

| Alert | When | Clears |
|---|---|---|
| NVIDIA temperature warning (amber) | Temperature reaches the warn threshold | 3 °C below the warn threshold |
| NVIDIA temperature critical (red) | Temperature reaches the critical threshold | 3 °C below the critical threshold (drops to warning) |
| P40 telemetry lost (red) | No good NVIDIA sample for 30 s, for any reason: nvidia-smi missing, stalled, crashing, or the GPU lost | The next good sample |

Thresholds come from the card itself: FluxGPU reads the GPU slowdown temperature (`nvidia-smi -q -d TEMPERATURE`) and sets warn = slowdown − 10 °C and critical = slowdown − 3 °C. If the card does not report it, the defaults are 75 °C and 85 °C. You can set your own values in the settings file (below).

Throttle badges only show real slowdowns. "Power cap" is shown as information because it is normal under load, and "idle" is never shown.

## What each card can show

| State | What you see |
|---|---|
| Connecting | "Connecting…" while waiting for the first sample (never a fake 0 %) |
| Live | Current data |
| Not found | "nvidia-smi not found. Looked in: …" or "No AMD adapter found", with the next check time |
| Error | The real error message, for example from nvidia-smi or the driver, and when it will retry. Ordinary errors retry after 3 s, then 6, 12, 24, 48 and every 60 s. "GPU is lost", "No devices were found" and "couldn't communicate with the NVIDIA driver" say "Reboot required" and retry every 60 s |
| Stale | No new sample for 8 s: the last values are dimmed, with the time of the last sample |
| Alert | See [Alerts](#alerts) |

If nvidia-smi stops responding (no output for 5 s), FluxGPU stops it and starts it again.

## Easy setup (recommended)

```powershell
git clone https://github.com/wyredone/FluxGPU.git
```

Then double-click **`setup.cmd`** in the FluxGPU folder (or run `.\FluxGPU\setup.cmd`). The wizard asks one numbered question at a time; press Enter to take the default.

| Step | What it does |
|---|---|
| 1. Check Windows | Windows 11, 64-bit. Windows 10 gets a warning. Files marked "downloaded from the internet" in the FluxGPU folder are unblocked |
| 2. Find the C# compiler | `csc.exe` from .NET Framework 4.x, and which version is installed |
| 3. Find the GPUs | Looks for nvidia-smi in the same places as FluxGPU and runs one test query (10 s limit). Lets you type the path if it isn't found. Finds the AMD adapter and checks the Windows GPU counters |
| 4. Build and test | Runs `build.cmd`, then `build.cmd test`, and shows the result |
| 5. Shortcuts (optional) | Start menu shortcut, desktop shortcut, and start FluxGPU when you sign in |
| 6. Settings (optional) | nvidia-smi path, your own temperature thresholds, alert sound. Written to `settings.ini` safely; the old file is kept as `settings.ini.bak` |
| 7. Launch | Starts FluxGPU and checks it stays running |

At the end it shows a PASS / WARN / FAIL summary. The setup log is `%LOCALAPPDATA%\FluxGPU\logs\fluxgpu-setup-YYYY-MM-DD.log`.

The first menu also has:

- **Repair**: checks again, rebuilds, points moved shortcuts and the startup entry at this folder, and offers to reset a damaged `settings.ini` (keeping the window position).
- **Uninstall**: removes the shortcuts and the start-with-Windows entry, and **asks** before deleting `%LOCALAPPDATA%\FluxGPU` (settings and logs). The FluxGPU folder itself is never deleted.

Safety:

- **No admin needed.** Everything is per user: shortcuts in your own Start menu and desktop folders, start-with-Windows in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (value `FluxGPU`), and settings in `%LOCALAPPDATA%\FluxGPU`.
- `setup.cmd` uses `-ExecutionPolicy Bypass` for this one script only. Your PowerShell execution policy is not changed.
- Setup never changes anything outside your account. The one fix that needs admin, `lodctr /R` for broken GPU counters, is shown for you to run yourself.
- Works with folder paths that contain spaces.

Options: `setup.cmd -Action Repair`, `setup.cmd -Action Uninstall`, and `setup.cmd -DryRun`, which shows what would change without changing anything.

The setup wizard has its own tests, which change nothing on the PC:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\FluxGPU\tools\Test-Setup.ps1
```

## Build and run by hand

Requirements:

- Windows 11, 64-bit
- An NVIDIA driver that provides `nvidia-smi`, for the NVIDIA card
- .NET Framework 4.x, which is included in Windows

No SDK or package downloads are needed.

```powershell
git clone https://github.com/wyredone/FluxGPU.git
```

```powershell
.\FluxGPU\build.cmd
```

```powershell
.\FluxGPU\FluxGPU.exe
```

Other build options:

| Command | What it does |
|---|---|
| `build.cmd test` | Builds and runs the pure-logic tests. Exit code 0 means every check passed |
| `build.cmd brand` | Regenerates the logo and icon files in `brand\` (this overwrites them), then builds the app. A normal build no longer touches them |

Command-line options for `FluxGPU.exe`:

| Option | What it does |
|---|---|
| `--compact` | Starts in compact view |
| `--nvidia-smi <full path>` | Uses this nvidia-smi.exe instead of searching for it |
| `--snapshot <file.png>` | Runs for 9 seconds, saves a picture of the window and exits (no settings saved, no alert sounds) |

## Settings and logs

| What | Where |
|---|---|
| Settings | `%LOCALAPPDATA%\FluxGPU\settings.ini` |
| Logs | `%LOCALAPPDATA%\FluxGPU\logs\fluxgpu-YYYY-MM-DD.log` (14 days kept, about 5 MB per file) |

Hover over the FluxGPU name in the title bar to see both paths.

The first time version 0.2 starts, it copies the position and view from the old `settings.txt` next to the exe into `settings.ini`. The old file is left where it is and is not read again.

`settings.ini` is plain `key=value` text. Edit it only while FluxGPU is closed. Leave a value blank for the automatic setting.

| Key | Meaning |
|---|---|
| `temp_warn_c`, `temp_crit_c` | Your own NVIDIA alert thresholds in °C (30–120). Blank means automatic. Warn must be lower than critical |
| `temp_hysteresis_c` | How far below a threshold the temperature must drop before the alert clears (default 3) |
| `alert_sound` | `1` plays a sound on alerts, `0` keeps them silent |
| `nvidia_smi_path` | Full path to nvidia-smi.exe. Blank searches `C:\Windows\System32`, then `C:\Program Files\NVIDIA Corporation\NVSMI` |
| `left`, `top`, `topmost`, `compact` | Window position and view, saved automatically |

If a value is invalid, FluxGPU ignores it, logs why and keeps a copy of the file as `settings.ini.bad-<date>`.

Log lines look like `11-26-26 05:41 PM [WARN] NVIDIA source state -> error: …`. Log entries cover:

- source state changes (connecting, live, stale, error, recovered)
- every alert raised and cleared
- nvidia-smi restarts by the stall supervisor
- crashes

## Troubleshooting

| Symptom | What to do |
|---|---|
| "nvidia-smi not found. Looked in: …" | Run `nvidia-smi` in a terminal. If it works from another folder, put that full path in `nvidia_smi_path` in settings.ini, or start FluxGPU with `--nvidia-smi "<full path>"` |
| "… Reboot required" (GPU is lost, no devices found, couldn't communicate with the driver) | Usually a driver crash or the card overheating. Check cooling, then reboot. FluxGPU retries every 60 s |
| "nvidia-smi stopped responding … restarted" over and over | The driver is hanging. Check the log for how often. Check cooling, and update or reinstall the driver |
| NVIDIA card shows data, but the P40 doesn't appear in Task Manager | That is normal in TCC mode. The info line shows "TCC mode" or "WDDM mode". Changing it needs admin and a reboot: `nvidia-smi -i 0 -dm 1` (TCC) or `-dm 0` (WDDM) |
| PCIe shows Gen1 when idle | Normal power saving. The info line shows the maximum generation the link supports |
| AMD card says "No AMD adapter found" | Check that the AMD graphics are enabled in Device Manager and in the BIOS |
| AMD card shows an error about performance counters, or stays empty | The Windows performance counters may be damaged. In an admin terminal run `lodctr /R`, then restart FluxGPU. Counter names are only matched in English, so non-English Windows is not supported yet |
| "AMD adapter changed, reconnecting…" | Expected after a driver update or after disabling and enabling the device. It clears by itself |
| Build fails with a warning | Run `set FLUXGPU_ALLOW_WARNINGS=1` then `build.cmd`, and report the warning |
| Setup says "does not look like the FluxGPU folder" | Run `setup.cmd` from the FluxGPU folder itself, next to `build.cmd` |
| Setup: "The folder path contains a %" | Move the FluxGPU folder to a path without `%`, for example `C:\FluxGPU` |
| Shortcuts stopped working after moving the folder | Run `setup.cmd -Action Repair` from the new location |
| Settings you typed into settings.ini were lost | FluxGPU saves its settings when it closes. Close it first, or use setup step 6, which closes it for you |
| Anything else | Open the newest file in `%LOCALAPPDATA%\FluxGPU\logs` (`fluxgpu-setup-*.log` for setup) |

## Windows test checklist (about 5 minutes)

Run this on the P40 PC before accepting a version. Write down pass or fail for each step.

1. Run `build.cmd test`. Every line says PASS and the last line says "0 failed".
2. Run `build.cmd` to build, then start `FluxGPU.exe`. Both cards say "Connecting…", then show live data within about 10 s. The NVIDIA card shows:
   - power as "draw/limit W"
   - an info line with TCC or WDDM mode, the maximum PCIe generation and "°C to slowdown"
3. Check that the build left the logo files alone: `git status` shows no changes under `brand\`.
4. **Overheat test:** close FluxGPU. Set `temp_warn_c` a few degrees below the current temperature and `temp_crit_c` 2 degrees above `temp_warn_c` in settings.ini, then start FluxGPU.
   - The border turns amber or red and the alert line appears. You hear a sound, and for critical the taskbar button flashes.
   - Remove the two values afterwards.
5. **Stall test:** in Task Manager, end `nvidia-smi.exe`. Within about 5 s the NVIDIA card shows the error and the retry time, and live data comes back.
6. **Telemetry-lost test:** close FluxGPU and set `nvidia_smi_path` to `C:\nope\nvidia-smi.exe`, then start it. The card shows "nvidia-smi not found. Looked in: …", and after 30 s the red "P40 telemetry lost" alert appears. Clear the value afterwards.
7. **Driver restart test** (optional): disable and enable the AMD display adapter in Device Manager. The AMD card shows "reconnecting" and recovers.
8. **Settings survive restart:** move the window, toggle the pin and compact view, close FluxGPU and start it again. Everything is as you left it. `%LOCALAPPDATA%\FluxGPU\settings.ini` exists.
9. **No orphans:** with FluxGPU running, end `FluxGPU.exe` in Task Manager. No `nvidia-smi.exe` stays running.
10. Open the newest log in `%LOCALAPPDATA%\FluxGPU\logs`. The lines use the `MM-DD-YY HH:MM AM/PM` time format and record the steps above.

## Known limits

- Tested on one PC only: Windows 11 Pro with a Tesla P40 and AMD Radeon integrated graphics. Version 0.2 was compile-checked and its logic tested on Linux, but has not yet been run on Windows. Use the checklist above.
- Only the first NVIDIA GPU (index 0) and the first AMD adapter are shown.
- AMD load and memory come from Windows performance counters, whose names the app expects in English.
- The AMD "Drv power" value is shown as the driver reports it. Its meaning has not been verified, so it is not shown in compact view and never alerted on.
- No toast notifications or tray icon yet.
- Dark mode and the window buttons have had little testing.

## Project layout

| Path | Contents |
|---|---|
| `FluxGPU.cs` | The whole application (C# 5, WPF) |
| `tests/FluxGPU.Tests.cs` | Pure-logic tests, built and run by `build.cmd test` |
| `build.cmd` | Build script (app, tests and optional logo regeneration) |
| `setup.cmd` | Setup wizard launcher (setup, repair, uninstall) |
| `tools/Setup-FluxGPU.ps1` | The setup wizard (Windows PowerShell 5.1 or PowerShell 7) |
| `tools/Test-Setup.ps1` | Setup wizard tests (mocked; changes nothing) |
| `brand/BrandGen.cs` | Draws the logo and writes the SVG, PNG and ICO files |
| `brand/` | Logo and icon files |
| `CHANGELOG.md` | Version history |

## Brand colors

| Use | Color |
|---|---|
| Cyan on dark backgrounds | `#12E9FF` |
| Cyan on light backgrounds | `#007F96` |
| Text on light backgrounds | `#141924` |

## License

[MIT](LICENSE)
