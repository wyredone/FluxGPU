# Changelog

All notable changes to FluxGPU. Dates are in MM-DD-YY format.

## 0.3.0-dev (unreleased)

The app now reports version 0.3.0-dev. This version has been compile-checked (C# 5, .NET Framework 4.8 reference assemblies, 0 warnings) and its logic tested on Linux against a fake nvidia-smi. It has **not yet been run on Windows**. See the Windows test checklist in the README (steps 11–14 are new).

### Added
- **"Using this GPU"** (P2-1): the NVIDIA card's full view lists the top 3 compute processes on the card by GPU memory, with "+N more" when there are more.
  - Read with a separate `nvidia-smi --id=0 --query-compute-apps=pid,process_name,used_memory --format=csv,noheader,nounits` call about every 5 s, not inside the `-l 1` stream.
  - Each call has a 5 s timeout. A hung call is stopped and retried with backoff, reusing the stall supervisor's process code and the job object.
  - Polling pauses in compact view, while the window is minimized, while the NVIDIA card isn't live, and when `show_processes=0`.
  - Full paths are shown as the file name, with the full path, PID and memory in a tooltip. Long names end in "…".
  - `[N/A]` memory shows "n/a" with a note (normal in WDDM mode), sorts after known values, and is never shown as 0.
  - If nvidia-smi can't read a name, FluxGPU asks Windows. If Windows refuses too, it shows "PID n (access denied)". No admin is needed.
  - No processes shows "No compute processes (idle)". Errors show the real message and retry time, never an empty or zero list.
- `show_processes` setting (`1` by default).
- Log lines when polling starts or pauses (with the reason), and when the list changes between active, idle and error.
- Tests for parsing (normal, `[N/A]`, empty, error output, access denied, commas in paths, duplicate PIDs), sorting, display names and the setting.

### Changed
- Version 0.3.0-dev in the assembly, the log and the title-bar tooltip.
- nvidia-smi timeouts are now rate-limited in the log per query, so one slow query can't hide another.

### Merging with the setup wizard branch
The setup wizard (PR #2) also adds a "0.3.0-dev (unreleased)" section. Whichever merges second should merge the two sections under one heading, and add `show_processes` to the wizard's known settings keys (`$script:KnownSettingKeys` and the 1/0 check in `tools\Setup-FluxGPU.ps1`), or the wizard will call it an unknown key.

## 0.2.0 (11-26-26): trustworthy and safe

This version has been compile-checked (C# 5, .NET Framework 4.8 reference assemblies) and its logic tested on Linux. It has **not yet been run on Windows**. See the Windows test checklist in the README.

### Fixed
- **The real NVIDIA error is shown** (P0-1). FluxGPU no longer replaces it with "nvidia-smi stopped, retrying". You see the error text, the exit code and when it will retry.
  - stderr is drained asynchronously into a bounded buffer, so a chatty nvidia-smi can't deadlock.
  - Errors are classified. "GPU is lost", "No devices were found" and "couldn't communicate with the NVIDIA driver" show "Reboot required" and retry every 60 s. Other errors back off from 3 s up to 60 s.
- **Stall supervisor** (P0-8). If nvidia-smi prints nothing for 5 s (10 s on startup), FluxGPU stops it and starts it again, with backoff. Before, a hung nvidia-smi left the card blind until FluxGPU was restarted.
- **The AMD adapter is found again** after a driver update or a disable/enable (P0-2). This is triggered when no counters match for 3 cycles, or when the driver reports the device as removed. Every D3DKMT adapter handle is now closed; non-matching adapters used to leak a handle on every search.
- **nvidia-smi is found** in `System32` or `Program Files\NVIDIA Corporation\NVSMI`, or at a full path you set (P0-3).
  - It's never looked up through PATH.
  - "Not found" says where it looked.
  - The path can be set with `nvidia_smi_path` or `--nvidia-smi`.
- **No fake zeros** (P0-6):
  - Missing load is a gap in the graph, not a drop to 0, and the graph never receives NaN.
  - The parser rejects NaN, Infinity and out-of-range values.
  - Error and connecting states clear the memory bar and dim the card.
  - The first AMD sample after a (re)start is "Connecting…", not "0 % idle".
- **AMD memory** (P0-6):
  - The iGPU card's main bar now shows shared memory used against the shared limit, with dedicated memory as a tile.
  - A total of 0 or unknown shows "n/a".
  - NVIDIA watts and AMD driver percent no longer share one field.
- A missing embedded logo resource no longer crashes startup (P0-7). `BrandGen.exe` checks its argument.

### Added
- **Crash handling and a log file** (P0-4):
  - Handlers for UI, unhandled and task exceptions.
  - A guarded card update, so one display error can't take down the window.
  - A single locked log writer in `%LOCALAPPDATA%\FluxGPU\logs`, with `MM-DD-YY HH:MM AM/PM` timestamps, 14 days kept, about 5 MB per file, and rate limiting by error class.
  - Logging of source state changes, alerts and restarts.
- **Settings in `%LOCALAPPDATA%\FluxGPU\settings.ini`** (P0-5):
  - Plain key=value text, written atomically (temp file, then replace).
  - The old `settings.txt` is migrated once and left in place.
  - Invalid values are ignored, logged and backed up.
  - Settings are saved after a drag, on pin or compact changes, on close and at Windows sign-out.
- **Power vs. limit and throttle badges** (P1-5):
  - Each optional nvidia-smi field is probed on its own (`clocks_event_reasons.active`, falling back to `clocks_throttle_reasons.active`, plus `pcie.link.gen.max` and `driver_model.current`), so an older driver can't reject the whole query.
  - Output is parsed by header name.
  - The throttle bitmask is parsed as hex. Only HW thermal, SW thermal, power brake and HW slowdown are badged, power cap is shown as information, and idle is never shown.
  - TCC or WDDM mode and the maximum PCIe generation are shown.
- **Temperature alerts** (P1-6):
  - Warn and critical thresholds come from the GPU's own slowdown temperature (slowdown − 10 / − 3), with 75/85 °C as the fallback and settings overrides.
  - Alerts clear with 3 °C of hysteresis.
  - Each alert plays a sound, flashes the taskbar button (critical) and colours the window border, with a line at the top of the window and a log entry.
- **"P40 telemetry lost" alert** when there's been no good NVIDIA sample for 30 s.
- **Job object** (P1-11): nvidia-smi runs in a Win32 job object that kills it when FluxGPU exits for any reason, including a crash or Task Manager. The crash handler also kills it.
- **Threading hardening** (P1-11b):
  - Cancellable waits instead of `Thread.Sleep`.
  - A latest-sample slot rendered on a 500 ms UI tick, so the UI can't build up a backlog.
  - `Stopwatch`-based staleness, so clock changes don't matter.
  - Shutdown order: cancel, kill, join with timeout, then save.
- Version 0.2.0 in the assembly, the log and the title-bar tooltip.
- Pure-logic tests (`tests/FluxGPU.Tests.cs`, `build.cmd test`), with no project file and no downloads.
- README sections: alerts, card states, settings and logs, troubleshooting, and a 5-minute Windows test checklist.

### Changed
- **`build.cmd` no longer regenerates or overwrites the committed logo files** (P1-11). Use `build.cmd brand` to regenerate them.
- The app is now built with `/warnaserror+` (escape hatch: `FLUXGPU_ALLOW_WARNINGS=1`).
- All P/Invokes (gdi32, dwmapi, kernel32, user32) load only from System32.

## 0.1.0 (11-26-26)
- First version: a floating WPF monitor for a Tesla P40 (nvidia-smi) and AMD integrated graphics (Windows performance counters).
