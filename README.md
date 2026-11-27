<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="brand/fluxgpu-logo-for-dark-backgrounds.svg">
    <img src="brand/fluxgpu-logo-for-light-backgrounds.svg" alt="FluxGPU" width="520">
  </picture>
</p>

A small floating GPU monitor for Windows 11 that shows an NVIDIA compute card and AMD integrated graphics side by side.

It was made for a Tesla P40, which runs in TCC mode and so normally does not appear in Task Manager.

## What it shows

| NVIDIA card (read through `nvidia-smi`) | AMD graphics (read from Windows) |
|---|---|
| Load with a 60-sample graph | Load with a 60-sample graph |
| Memory used and total | Dedicated memory used and total |
| Temperature | Temperature, if the driver reports it |
| Power draw in watts | Driver power reading (percent) |
| Core clock | Shared memory used |
| PCIe generation and width | Busiest engine |

- Stays on top of other windows (the pin button turns this off)
- Full and compact views
- Follows the Windows light or dark mode and accent color
- Remembers its position
- Temperature turns amber at 75°C and red at 85°C

## Build and run

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

## Known limits

- Tested on one PC only: Windows 11 Pro with a Tesla P40 and AMD Radeon integrated graphics.
- Only the first NVIDIA GPU (index 0) and the first AMD adapter are shown.
- AMD load and memory come from Windows performance counters, whose names the app expects in English.
- The AMD "Driver power" value is shown as the driver reports it. Its meaning has not been verified.
- Dark mode and the window buttons have had little testing.

## Project layout

| Path | Contents |
|---|---|
| `FluxGPU.cs` | The whole application (C#, WPF) |
| `build.cmd` | Build script |
| `brand/BrandGen.cs` | Draws the logo and writes the SVG, PNG and ICO files |
| `brand/` | Logo and icon files |

## Brand colors

| Use | Color |
|---|---|
| Cyan on dark backgrounds | `#12E9FF` |
| Cyan on light backgrounds | `#007F96` |
| Text on light backgrounds | `#141924` |
