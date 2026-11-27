<#
.SYNOPSIS
    FluxGPU setup wizard: check the PC, build, add shortcuts, set options, launch. Also repair and uninstall.

.DESCRIPTION
    Run it with setup.cmd (double-click), which starts Windows PowerShell with -ExecutionPolicy Bypass for this
    one script only. Nothing system-wide is changed and admin is not needed:
      - shortcuts go in your own Start menu and desktop folders
      - start-with-Windows is a per-user Run entry (HKCU)
      - settings and logs live in %LOCALAPPDATA%\FluxGPU
    Works with Windows PowerShell 5.1 (built into Windows 10/11) and PowerShell 7.

.PARAMETER Action
    Menu (default), Install, Repair or Uninstall.

.PARAMETER DryRun
    Show what would change without changing anything (no build, no shortcuts, no settings, no launch).

.PARAMETER RepoRoot
    The FluxGPU folder (where build.cmd is). Defaults to the folder above tools\.
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Interactive console wizard: colored, numbered steps are the user interface.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Changes go through Invoke-Change, which honors -DryRun.')]
[CmdletBinding()]
param(
    [ValidateSet('Menu', 'Install', 'Repair', 'Uninstall')]
    [string]$Action = 'Menu',
    [switch]$DryRun,
    [string]$RepoRoot
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:SetupVersion = '0.3.0-dev'
$script:IsDryRun = [bool]$DryRun
$script:Results = New-Object System.Collections.ArrayList
$script:LogFile = $null
$script:ChosenSmiPath = $null      # a path the user typed in step 3, offered again in step 6
$script:FoundSmiPath = $null
$script:TotalSteps = 7
$script:RunValueName = 'FluxGPU'
$script:RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$script:Inv = [Globalization.CultureInfo]::InvariantCulture

# ---------------------------------------------------------------------------------------------------
# Logging: %LOCALAPPDATA%\FluxGPU\logs\fluxgpu-setup-YYYY-MM-DD.log, MM-DD-YY HH:MM AM/PM timestamps.
# (The app deletes fluxgpu-*.log files older than 14 days, so setup logs are cleaned up the same way.)
# ---------------------------------------------------------------------------------------------------

function Format-LogStamp([datetime]$Time) {
    return $Time.ToString('MM-dd-yy hh:mm tt', $script:Inv)
}

function Get-FluxDataFolder {
    $base = [Environment]::GetEnvironmentVariable('LOCALAPPDATA')
    if ([string]::IsNullOrEmpty($base)) { throw 'The LOCALAPPDATA folder is not set for this user.' }
    return (Join-Path $base 'FluxGPU')
}

function Initialize-SetupLog {
    try {
        $folder = Join-Path (Get-FluxDataFolder) 'logs'
        if (-not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }
        $script:LogFile = Join-Path $folder ('fluxgpu-setup-' + (Get-Date).ToString('yyyy-MM-dd', $script:Inv) + '.log')
    }
    catch {
        $script:LogFile = $null
        Write-Host ('  Note: could not create the setup log (' + $_.Exception.Message + '). Continuing without it.') -ForegroundColor Yellow
    }
}

function Write-SetupLog([string]$Level, [string]$Message) {
    if (-not $script:LogFile) { return }
    $line = (Format-LogStamp (Get-Date)) + ' [' + $Level + '] ' + ($(if ($script:IsDryRun) { '(dry run) ' } else { '' })) + $Message
    try { [IO.File]::AppendAllText($script:LogFile, $line + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false))) }
    catch { $script:LogFile = $null }   # a log failure must never stop the wizard
}

# ---------------------------------------------------------------------------------------------------
# Console output and prompts. Every question has numbered options; Enter picks the default.
# ---------------------------------------------------------------------------------------------------

function Write-Title([string]$Text) {
    Write-Host ''
    Write-Host ('=' * 64) -ForegroundColor DarkCyan
    Write-Host ('  ' + $Text) -ForegroundColor Cyan
    Write-Host ('=' * 64) -ForegroundColor DarkCyan
}

function Write-Step([int]$Number, [string]$Title) {
    Write-Host ''
    Write-Host ('Step ' + $Number + ' of ' + $script:TotalSteps + ': ' + $Title) -ForegroundColor Cyan
    Write-Host ('-' * 64) -ForegroundColor DarkGray
    Write-SetupLog 'INFO' ('Step ' + $Number + ': ' + $Title)
}

function Write-Info([string]$Text) { Write-Host ('  ' + $Text) }

function Add-Result([string]$Step, [ValidateSet('PASS', 'WARN', 'FAIL', 'SKIP')] [string]$Status, [string]$Detail) {
    [void]$script:Results.Add([pscustomobject]@{ Step = $Step; Status = $Status; Detail = $Detail })
    $color = @{ PASS = 'Green'; WARN = 'Yellow'; FAIL = 'Red'; SKIP = 'DarkGray' }[$Status]
    Write-Host ('  [' + $Status + '] ' + $Detail) -ForegroundColor $color
    $level = @{ PASS = 'INFO'; WARN = 'WARN'; FAIL = 'ERROR'; SKIP = 'INFO' }[$Status]
    Write-SetupLog $level ($Step + ': ' + $Status + ' - ' + $Detail)
}

function Read-Choice([string]$Question, [string[]]$Options, [int]$Default = 1) {
    Write-Host ''
    Write-Host ('  ' + $Question) -ForegroundColor White
    for ($i = 0; $i -lt $Options.Count; $i++) {
        $mark = ''
        if (($i + 1) -eq $Default) { $mark = '   (default)' }
        Write-Host ('    ' + ($i + 1) + ') ' + $Options[$i] + $mark)
    }
    while ($true) {
        $answer = Read-Host ('  Type a number and press Enter [' + $Default + ']')
        if ([string]::IsNullOrWhiteSpace($answer)) { $n = $Default }
        else {
            $n = 0
            if (-not [int]::TryParse($answer.Trim(), [ref]$n)) { $n = 0 }
        }
        if ($n -ge 1 -and $n -le $Options.Count) {
            Write-SetupLog 'INFO' ('Question "' + $Question + '" -> ' + $n + ') ' + $Options[$n - 1])
            return $n
        }
        Write-Host ('  Please type a number from 1 to ' + $Options.Count + '.') -ForegroundColor Yellow
    }
}

function Read-Value([string]$Prompt) {
    $v = Read-Host ('  ' + $Prompt)
    if ($null -eq $v) { return '' }
    return $v.Trim().Trim('"')
}

# Runs a change, or in dry-run mode only describes it.
function Invoke-Change([string]$Description, [scriptblock]$Change) {
    if ($script:IsDryRun) {
        Write-Host ('  (dry run) would: ' + $Description) -ForegroundColor DarkYellow
        Write-SetupLog 'INFO' ('would: ' + $Description)
        return $true
    }
    try {
        & $Change | Out-Null
        Write-SetupLog 'INFO' ('done: ' + $Description)
        return $true
    }
    catch {
        Write-SetupLog 'ERROR' ('failed: ' + $Description + ': ' + $_.Exception.Message)
        Write-Host ('  Could not ' + $Description + ': ' + $_.Exception.Message) -ForegroundColor Red
        return $false
    }
}

# ---------------------------------------------------------------------------------------------------
# Environment probes (kept small so the tests can replace them with mocks)
# ---------------------------------------------------------------------------------------------------

function Test-PathExists([string]$Path) {
    if ([string]::IsNullOrEmpty($Path)) { return $false }
    return (Test-Path -LiteralPath $Path -PathType Leaf)
}

function Test-FolderExists([string]$Path) {
    if ([string]::IsNullOrEmpty($Path)) { return $false }
    return (Test-Path -LiteralPath $Path -PathType Container)
}

function Get-WindowsInfo {
    $build = [Environment]::OSVersion.Version.Build
    $name = 'Windows'
    try {
        $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
        $name = [string]$os.Caption
        $build = [int]$os.BuildNumber
    }
    catch { Write-SetupLog 'WARN' ('Could not read the Windows edition: ' + $_.Exception.Message) }
    return [pscustomobject]@{
        IsWindows = ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT)
        Is64      = [Environment]::Is64BitOperatingSystem
        Build     = $build
        Name      = $name
    }
}

function Test-IsAdmin {
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    catch { return $false }
}

# The real System32 even from a 32-bit PowerShell (where System32 is redirected to SysWOW64).
function Get-SystemFolder {
    if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
        return (Join-Path $env:SystemRoot 'Sysnative')
    }
    return [Environment]::SystemDirectory
}

function Get-FrameworkFolder {
    return (Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319')
}

function Get-NetFrameworkRelease {
    try {
        $v = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release -ErrorAction Stop
        return [int]$v.Release
    }
    catch { return $null }
}

function Get-AmdAdapters {
    $list = @()
    foreach ($vc in @(Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop)) {
        $n = [string]$vc.Name
        if ($n -match 'AMD|Radeon') {
            $list += [pscustomobject]@{ Name = $n; ErrorCode = [int]$vc.ConfigManagerErrorCode }
        }
    }
    return , $list
}

function Test-GpuCountersPresent {
    try { return [Diagnostics.PerformanceCounterCategory]::Exists('GPU Engine') }
    catch { return $false }
}

function Get-FluxProcesses([string]$ExePath) {
    $found = @()
    foreach ($p in @(Get-Process -Name FluxGPU -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $p.Path } catch { $path = $null }
        if ($null -eq $path -or [string]::Equals($path, $ExePath, [StringComparison]::OrdinalIgnoreCase)) { $found += $p }
    }
    return , $found
}

function Get-SpecialFolder([string]$Name) {
    return [Environment]::GetFolderPath($Name)
}

function Get-ShortcutTarget([string]$LinkPath) {
    if (-not (Test-PathExists $LinkPath)) { return $null }
    try {
        $shell = New-Object -ComObject WScript.Shell
        return [string]$shell.CreateShortcut($LinkPath).TargetPath
    }
    catch { return $null }
}

function New-FluxShortcut([string]$LinkPath, [string]$ExePath, [string]$WorkingFolder) {
    $shell = New-Object -ComObject WScript.Shell
    $s = $shell.CreateShortcut($LinkPath)
    $s.TargetPath = $ExePath
    $s.WorkingDirectory = $WorkingFolder
    $s.IconLocation = $ExePath + ',0'
    $s.Description = 'FluxGPU GPU monitor'
    $s.Save()
}

function Get-RunValue {
    try {
        $v = Get-ItemProperty -LiteralPath $script:RunKey -Name $script:RunValueName -ErrorAction Stop
        return [string]$v.($script:RunValueName)
    }
    catch { return $null }
}

function Set-RunValue([string]$Command) {
    if (-not (Test-Path -LiteralPath $script:RunKey)) { New-Item -Path $script:RunKey -Force | Out-Null }
    Set-ItemProperty -LiteralPath $script:RunKey -Name $script:RunValueName -Value $Command
}

function Remove-RunValue {
    Remove-ItemProperty -LiteralPath $script:RunKey -Name $script:RunValueName -ErrorAction Stop
}

# Runs a program with a time limit and captures its output. Both streams are read asynchronously so a chatty
# program cannot fill a pipe and hang (the same lesson as FluxGPU P0-1). Kills it if the time runs out.
function Invoke-ProcessCapture([string]$FilePath, [string]$Arguments, [int]$TimeoutMs, [string]$WorkingFolder) {
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = $Arguments
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    if ($WorkingFolder) { $psi.WorkingDirectory = $WorkingFolder }
    $p = New-Object Diagnostics.Process
    $p.StartInfo = $psi
    try {
        [void]$p.Start()
        $out = $p.StandardOutput.ReadToEndAsync()
        $err = $p.StandardError.ReadToEndAsync()
        $timedOut = -not $p.WaitForExit($TimeoutMs)
        if ($timedOut) {
            # Stop the whole tree (build.cmd starts csc.exe): taskkill /T on Windows, then Kill as a fallback.
            try {
                $sys = [Environment]::SystemDirectory
                if (-not [string]::IsNullOrEmpty($sys)) {
                    $tk = Join-Path $sys 'taskkill.exe'
                    if (Test-Path -LiteralPath $tk) { & $tk /T /F /PID $p.Id 2>&1 | Out-Null }
                }
                if (-not $p.HasExited) { $p.Kill() }
            }
            catch { Write-SetupLog 'WARN' ('Could not stop ' + $FilePath + ': ' + $_.Exception.Message) }
            [void]$p.WaitForExit(3000)
        }
        else { $p.WaitForExit() }
        $code = $null
        if (-not $timedOut) { $code = $p.ExitCode }
        $o = ''; $e = ''
        if ($out.Wait(3000)) { $o = $out.Result }
        if ($err.Wait(3000)) { $e = $err.Result }
        return [pscustomobject]@{ ExitCode = $code; StdOut = $o; StdErr = $e; TimedOut = $timedOut }
    }
    finally { $p.Dispose() }
}

# ---------------------------------------------------------------------------------------------------
# Pure logic (no side effects; covered by tools\Test-Setup.ps1)
# ---------------------------------------------------------------------------------------------------

# Joins Windows path parts with backslashes. Same result as Join-Path on Windows, and testable anywhere.
function Join-WinPath([string]$Base) {
    $p = $Base.TrimEnd('\')
    foreach ($part in $args) { $p = $p + '\' + ([string]$part).Trim('\') }
    return $p
}

# Same rule the app uses for nvidia_smi_path: a full, already-normalized Windows path (C:\... or \\server\...).
function Test-AbsoluteWinPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    if ($Path -ne $Path.Trim()) { return $false }
    if ($Path -match '[/<>"|?*]') { return $false }
    if ($Path -notmatch '^([A-Za-z]:\\|\\\\[^\\]+\\[^\\]+)') { return $false }
    if ($Path.Substring(2) -match '\\\\') { return $false }
    if ($Path -match '\\\.{1,2}(\\|$)') { return $false }
    if ($Path.IndexOf(':', 2) -ge 0) { return $false }
    return $true
}

# The same search order as FluxGPU (NvidiaSmiLocator): settings path, System32, then the NVSMI folder.
function Get-NvidiaSmiCandidates([string]$Override, [string]$SystemFolder, [string]$ProgramW6432, [string]$ProgramFiles) {
    $list = New-Object System.Collections.Generic.List[string]
    if (Test-AbsoluteWinPath $Override) { $list.Add($Override) }
    if (-not [string]::IsNullOrEmpty($SystemFolder)) { $list.Add((Join-WinPath $SystemFolder 'nvidia-smi.exe')) }
    foreach ($pf in @($ProgramW6432, $ProgramFiles)) {
        if ([string]::IsNullOrEmpty($pf) -or -not (Test-AbsoluteWinPath $pf)) { continue }
        $c = Join-WinPath $pf 'NVIDIA Corporation' 'NVSMI' 'nvidia-smi.exe'
        if (-not $list.Contains($c)) { $list.Add($c) }
    }
    return , $list.ToArray()
}

function Get-WindowsStatus($Info) {
    if (-not $Info.IsWindows) { return [pscustomobject]@{ Status = 'FAIL'; Detail = 'This is not Windows. FluxGPU needs Windows 10 or 11, 64-bit.' } }
    if (-not $Info.Is64) { return [pscustomobject]@{ Status = 'FAIL'; Detail = ($Info.Name + ' is 32-bit. FluxGPU needs 64-bit Windows.') } }
    if ($Info.Build -ge 22000) { return [pscustomobject]@{ Status = 'PASS'; Detail = ($Info.Name + ', 64-bit (build ' + $Info.Build + ')') } }
    if ($Info.Build -ge 10240) { return [pscustomobject]@{ Status = 'WARN'; Detail = ($Info.Name + ' (build ' + $Info.Build + '). FluxGPU is made for Windows 11; Windows 10 should work but has not been tested.') } }
    return [pscustomobject]@{ Status = 'FAIL'; Detail = ($Info.Name + ' (build ' + $Info.Build + ') is too old. FluxGPU needs Windows 10 or 11.') }
}

function ConvertTo-NetFrameworkName($Release) {
    if ($null -eq $Release) { return $null }
    $r = [int]$Release
    if ($r -ge 533320) { return '4.8.1' }
    if ($r -ge 528040) { return '4.8' }
    if ($r -ge 461808) { return '4.7.2' }
    if ($r -ge 461308) { return '4.7.1' }
    if ($r -ge 460798) { return '4.7' }
    if ($r -ge 394802) { return '4.6.2' }
    if ($r -ge 378389) { return '4.5 to 4.6.1' }
    return 'older than 4.5'
}

# cmd.exe /s /c strips one pair of outer quotes, so a script path with spaces keeps its own quotes.
function Get-CmdArguments([string]$ScriptPath, [string]$Argument) {
    $inner = '"' + $ScriptPath + '"'
    if (-not [string]::IsNullOrEmpty($Argument)) { $inner = $inner + ' ' + $Argument }
    return '/d /s /c "' + $inner + '"'
}

# cmd.exe expands %NAME% even inside quotes, so a folder name with % cannot be passed through it safely.
function Test-PathSafeForCmd([string]$Path) {
    return -not ($Path.Contains('%') -or $Path.Contains('"'))
}

function Get-SmiTestArguments {
    # Only fields every driver supports, so an older driver cannot reject the test query.
    return '--query-gpu=name,driver_version,temperature.gpu --format=csv,noheader'
}

# Returns a number from 30 to 120, or $null if the text is not one.
function ConvertTo-TemperatureC([string]$Text) {
    $v = 0.0
    if (-not [double]::TryParse(([string]$Text).Trim(), [Globalization.NumberStyles]::Float, $script:Inv, [ref]$v)) { return $null }
    if ([double]::IsNaN($v) -or [double]::IsInfinity($v) -or $v -lt 30 -or $v -gt 120) { return $null }
    return $v
}

# $null when the pair is fine, otherwise a plain explanation.
function Test-Thresholds($WarnC, $CritC) {
    if ($null -eq $WarnC -or $null -eq $CritC) { return 'Both values must be numbers from 30 to 120.' }
    if ($WarnC -ge $CritC) { return 'The warning temperature must be lower than the critical temperature.' }
    return $null
}

function Format-Number([double]$Value) { return $Value.ToString('0.##', $script:Inv) }

# Keys FluxGPU 0.2 reads from settings.ini. Anything else makes the app back the file up as .bad-<date>.
$script:KnownSettingKeys = @('version', 'position_units', 'left', 'top', 'topmost', 'compact', 'alert_sound',
    'migrated_settings_txt', 'temp_warn_c', 'temp_crit_c', 'temp_hysteresis_c', 'nvidia_smi_path')

# Checks settings lines with the same rules as the app. Returns a list of plain-English problems (empty = OK).
function Test-SettingsLines([string[]]$Lines) {
    $problems = New-Object System.Collections.Generic.List[string]
    $warn = $null; $crit = $null
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        $line = ([string]$Lines[$i]).Trim()
        if ($line.Length -eq 0 -or $line.StartsWith('#') -or $line.StartsWith(';')) { continue }
        $eq = $line.IndexOf('=')
        if ($eq -le 0) { $problems.Add('line ' + ($i + 1) + ': not key=value'); continue }
        $key = $line.Substring(0, $eq).Trim().ToLowerInvariant()
        $val = $line.Substring($eq + 1).Trim()
        if ($script:KnownSettingKeys -notcontains $key) { $problems.Add("unknown key '" + $key + "'"); continue }
        switch ($key) {
            { $_ -in @('topmost', 'compact', 'alert_sound', 'migrated_settings_txt') } {
                if ($val -notin @('0', '1', 'true', 'false')) { $problems.Add($key + ': expected 1 or 0') }
            }
            { $_ -in @('temp_warn_c', 'temp_crit_c') } {
                if ($val.Length -gt 0) {
                    $t = ConvertTo-TemperatureC $val
                    if ($null -eq $t) { $problems.Add($key + ': expected 30-120 C or blank') }
                    elseif ($key -eq 'temp_warn_c') { $warn = $t } else { $crit = $t }
                }
            }
            'nvidia_smi_path' {
                if ($val.Length -gt 0 -and -not (Test-AbsoluteWinPath $val)) { $problems.Add($key + ': must be a full path such as C:\Windows\System32\nvidia-smi.exe') }
            }
            { $_ -in @('left', 'top') } {
                $d = 0.0
                if ($val.Length -gt 0 -and -not [double]::TryParse($val, [Globalization.NumberStyles]::Float, $script:Inv, [ref]$d)) { $problems.Add($key + ': not a number') }
            }
        }
    }
    if ($null -ne $warn -and $null -ne $crit -and $warn -ge $crit) { $problems.Add('temp_warn_c must be lower than temp_crit_c') }
    return , $problems.ToArray()
}

# Sets key=value lines, keeping every other line (comments, window position) exactly as it was.
function Set-SettingsValues([string[]]$Lines, [hashtable]$Values) {
    $out = New-Object System.Collections.Generic.List[string]
    $done = @{}
    foreach ($line in $Lines) {
        $t = ([string]$line).Trim()
        $eq = $t.IndexOf('=')
        if ($eq -gt 0 -and -not $t.StartsWith('#') -and -not $t.StartsWith(';')) {
            $key = $t.Substring(0, $eq).Trim().ToLowerInvariant()
            if ($Values.ContainsKey($key)) {
                if (-not $done.ContainsKey($key)) { $out.Add($key + '=' + $Values[$key]); $done[$key] = $true }
                continue   # drop duplicates of a key we set
            }
        }
        $out.Add([string]$line)
    }
    foreach ($key in ($Values.Keys | Sort-Object)) {
        if (-not $done.ContainsKey($key)) { $out.Add($key + '=' + $Values[$key]) }
    }
    return , $out.ToArray()
}

function New-SettingsLines {
    return , @('# FluxGPU settings (key=value). Edit only while FluxGPU is closed. Blank = automatic.', 'version=1', 'position_units=dip-system')
}

# The v0.1 settings.txt ("left|top|topmost|compact") as settings.ini lines, or $null if it is not that format.
# Used only when settings.ini does not exist yet, so the window position is not lost (the app does the same).
function ConvertFrom-LegacySettings([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $f = $Text.Trim().Split('|')
    if ($f.Count -lt 4) { return $null }
    $l = 0.0; $tp = 0.0
    if (-not [double]::TryParse($f[0].Trim(), [Globalization.NumberStyles]::Float, $script:Inv, [ref]$l)) { return $null }
    if (-not [double]::TryParse($f[1].Trim(), [Globalization.NumberStyles]::Float, $script:Inv, [ref]$tp)) { return $null }
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($x in (New-SettingsLines)) { $lines.Add($x) }
    $lines.Add('left=' + (Format-Number $l))
    $lines.Add('top=' + (Format-Number $tp))
    $lines.Add('topmost=' + $(if ($f[2].Trim() -eq '1') { '1' } else { '0' }))
    $lines.Add('compact=' + $(if ($f[3].Trim() -eq '1') { '1' } else { '0' }))
    $lines.Add('migrated_settings_txt=1')
    return , $lines.ToArray()
}

# Safe for paths with spaces; must not be a folder FluxGPU does not own.
function Test-IsFluxDataFolder([string]$Path, [string]$LocalAppData) {
    if ([string]::IsNullOrEmpty($Path) -or [string]::IsNullOrEmpty($LocalAppData)) { return $false }
    $expected = $LocalAppData.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar + 'FluxGPU'
    return [string]::Equals($Path.TrimEnd('\', '/'), $expected, [StringComparison]::OrdinalIgnoreCase)
}

function Get-RunCommand([string]$ExePath) { return '"' + $ExePath + '"' }

# Writes UTF-8 (no BOM) to a temp file, flushes it to disk, then swaps it in (keeps a .bak of the old file).
function Write-SettingsAtomic([string]$Path, [string[]]$Lines) {
    $folder = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }
    $tmp = $Path + '.tmp'
    $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes(($Lines -join "`r`n") + "`r`n")
    $fs = New-Object IO.FileStream($tmp, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $fs.Write($bytes, 0, $bytes.Length); $fs.Flush($true) } finally { $fs.Dispose() }
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($tmp, $Path, $Path + '.bak') }
    else { [IO.File]::Move($tmp, $Path) }
}

function Read-SettingsLines([string]$Path) {
    if (-not (Test-PathExists $Path)) { return $null }
    $text = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
    return , ($text.Replace("`r`n", "`n").TrimEnd("`n").Split("`n"))
}

function Get-SettingValue([string[]]$Lines, [string]$Key) {
    if ($null -eq $Lines) { return $null }
    foreach ($line in $Lines) {
        $t = ([string]$line).Trim()
        $eq = $t.IndexOf('=')
        if ($eq -gt 0 -and -not $t.StartsWith('#') -and $t.Substring(0, $eq).Trim().ToLowerInvariant() -eq $Key) { return $t.Substring($eq + 1).Trim() }
    }
    return $null
}

function Get-BlockedFiles([string]$Root) {
    $blocked = @()
    try {
        foreach ($f in @(Get-ChildItem -LiteralPath $Root -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in @('.cmd', '.ps1', '.cs', '.exe') })) {
            $s = Get-Item -LiteralPath $f.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue
            if ($s) { $blocked += $f.FullName }
        }
    }
    catch { Write-SetupLog 'WARN' ('Could not check for downloaded-file blocks: ' + $_.Exception.Message) }
    return , $blocked
}

# ---------------------------------------------------------------------------------------------------
# Shared step helpers
# ---------------------------------------------------------------------------------------------------

function New-SetupContext([string]$Root) {
    $data = Get-FluxDataFolder
    return [pscustomobject]@{
        Root           = $Root
        Exe            = Join-Path $Root 'FluxGPU.exe'
        BuildCmd       = Join-Path $Root 'build.cmd'
        LegacySettings = Join-Path $Root 'settings.txt'
        DataFolder     = $data
        LogFolder      = Join-Path $data 'logs'
        SettingsPath   = Join-Path $data 'settings.ini'
        CscOk          = $false
    }
}

# FluxGPU saves its settings when it closes, so it must be closed before a rebuild or a settings change.
function Close-FluxIfRunning($Ctx, [string]$Why) {
    $procs = Get-FluxProcesses $Ctx.Exe
    if ($procs.Count -eq 0) { return $true }
    $c = Read-Choice ('FluxGPU is running. It needs to be closed ' + $Why + '.') @('Close FluxGPU now (it saves its window position as it closes)', 'Skip this step') 1
    if ($c -ne 1) { return $false }
    $ok = Invoke-Change 'close FluxGPU' {
        foreach ($p in $procs) {
            [void]$p.CloseMainWindow()
            if (-not $p.WaitForExit(5000)) { $p.Kill(); [void]$p.WaitForExit(3000) }
        }
    }
    return $ok
}

function Show-OutputTail([string]$Text, [int]$Count) {
    $lines = @(([string]$Text).Replace("`r`n", "`n").Split("`n") | Where-Object { $_.Trim().Length -gt 0 })
    $start = [Math]::Max(0, $lines.Count - $Count)
    for ($i = $start; $i -lt $lines.Count; $i++) { Write-Host ('      ' + $lines[$i]) -ForegroundColor DarkGray }
}

# ---------------------------------------------------------------------------------------------------
# Step 1: Windows
# ---------------------------------------------------------------------------------------------------

function Invoke-StepWindows($Ctx) {
    Write-Step 1 'Check Windows'
    $st = Get-WindowsStatus (Get-WindowsInfo)
    Add-Result 'Windows' $st.Status $st.Detail
    if (Test-IsAdmin) {
        Add-Result 'Windows' 'WARN' 'You are running setup as administrator. This is not needed. Shortcuts and start-with-Windows are set for the account running setup.'
    }
    $blocked = Get-BlockedFiles $Ctx.Root
    if ($blocked.Count -gt 0) {
        Write-Info ('' + $blocked.Count + ' file(s) in this folder are marked as downloaded from the internet, which can block the build.')
        $ok = Invoke-Change ('unblock ' + $blocked.Count + ' downloaded file(s) in ' + $Ctx.Root) { foreach ($f in $blocked) { Unblock-File -LiteralPath $f } }
        if ($ok) { Add-Result 'Windows' 'PASS' ('Unblocked ' + $blocked.Count + ' downloaded file(s) in the FluxGPU folder only') }
    }
    return ($st.Status -ne 'FAIL')
}

# ---------------------------------------------------------------------------------------------------
# Step 2: C# compiler (.NET Framework 4.x)
# ---------------------------------------------------------------------------------------------------

function Invoke-StepCompiler($Ctx) {
    Write-Step 2 'Find the C# compiler (.NET Framework 4.x)'
    $fw = Get-FrameworkFolder
    $csc = Join-WinPath $fw 'csc.exe'
    $wpf = Join-WinPath $fw 'WPF'
    $ver = ConvertTo-NetFrameworkName (Get-NetFrameworkRelease)
    if (-not (Test-PathExists $csc)) {
        Add-Result 'Compiler' 'FAIL' ('csc.exe was not found at ' + $csc + '. Turn on .NET Framework 4.8 in Windows Features, or install it from Microsoft, then run setup again.')
        return $false
    }
    if (-not (Test-FolderExists $wpf)) {
        Add-Result 'Compiler' 'FAIL' ('The WPF libraries were not found in ' + $wpf + '. Repair .NET Framework 4.8, then run setup again.')
        return $false
    }
    if ($null -eq $ver) { $ver = 'version unknown' }
    if ($ver -eq 'older than 4.5') {
        Add-Result 'Compiler' 'WARN' ('Found ' + $csc + ', but .NET Framework is ' + $ver + '. Install .NET Framework 4.8 through Windows Update.')
    }
    else { Add-Result 'Compiler' 'PASS' ('Found the C# compiler (.NET Framework ' + $ver + '): ' + $csc) }
    $Ctx.CscOk = $true
    return $true
}

# ---------------------------------------------------------------------------------------------------
# Step 3: GPUs (nvidia-smi with the app's search order, and the AMD adapter)
# ---------------------------------------------------------------------------------------------------

function Test-NvidiaSmiAt([string]$Path) {
    Write-Info ('Testing ' + $Path + ' (up to 10 seconds)...')
    try { $r = Invoke-ProcessCapture $Path (Get-SmiTestArguments) 10000 $null }
    catch {
        Add-Result 'NVIDIA' 'WARN' ('Could not run ' + $Path + ': ' + $_.Exception.Message)
        return $false
    }
    $first = @(([string]$r.StdOut + "`n" + [string]$r.StdErr).Split("`n") | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -First 1)
    $firstText = ''
    if ($first.Count -gt 0) { $firstText = $first[0].Trim() }
    if ($r.TimedOut) {
        Add-Result 'NVIDIA' 'WARN' ('nvidia-smi did not answer within 10 s. The driver may be hung: check cooling and reboot. Path: ' + $Path)
        return $false
    }
    if ($r.ExitCode -ne 0) {
        Add-Result 'NVIDIA' 'WARN' ('nvidia-smi exited with code ' + $r.ExitCode + ': ' + $firstText + '. FluxGPU will show this error on the NVIDIA card. Path: ' + $Path)
        return $false
    }
    $f = $firstText.Split(',')
    $desc = $firstText
    if ($f.Count -ge 3) { $desc = $f[0].Trim() + ', driver ' + $f[1].Trim() + ', ' + $f[2].Trim() + ' C' }
    Add-Result 'NVIDIA' 'PASS' ('nvidia-smi works: ' + $desc + ' (' + $Path + ')')
    return $true
}

function Invoke-StepGpus($Ctx) {
    Write-Step 3 'Find the GPUs (NVIDIA with nvidia-smi, and AMD graphics)'
    $override = Get-SettingValue (Read-SettingsLines $Ctx.SettingsPath) 'nvidia_smi_path'
    $candidates = Get-NvidiaSmiCandidates $override (Get-SystemFolder) $env:ProgramW6432 $env:ProgramFiles
    $found = $null
    foreach ($c in $candidates) { if (Test-PathExists $c) { $found = $c; break } }
    if ($found) {
        $script:FoundSmiPath = $found
        [void](Test-NvidiaSmiAt $found)
    }
    else {
        Add-Result 'NVIDIA' 'WARN' ('nvidia-smi was not found. Looked in: ' + ($candidates -join '; '))
        for ($try = 1; $try -le 3; $try++) {
            $c = Read-Choice 'nvidia-smi comes with the NVIDIA driver. What would you like to do?' @('Type the full path to nvidia-smi.exe', 'Continue without the NVIDIA card') 2
            if ($c -ne 1) { break }
            $p = Read-Value 'Full path to nvidia-smi.exe (for example C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe):'
            if (-not (Test-AbsoluteWinPath $p) -or -not $p.EndsWith('\nvidia-smi.exe', [StringComparison]::OrdinalIgnoreCase)) {
                Write-Info 'That is not a full path ending in \nvidia-smi.exe.'
                continue
            }
            if (-not (Test-PathExists $p)) { Write-Info ('There is no file at ' + $p); continue }
            $script:ChosenSmiPath = $p
            $script:FoundSmiPath = $p
            [void](Test-NvidiaSmiAt $p)
            Write-Info 'Step 6 can save this path so FluxGPU uses it.'
            break
        }
    }

    try {
        $amd = Get-AmdAdapters
        if ($amd.Count -eq 0) {
            Add-Result 'AMD' 'WARN' 'No AMD graphics adapter was found. The AMD card will say "No AMD adapter found". If you have AMD graphics, check it is enabled in Device Manager and in the BIOS.'
        }
        else {
            foreach ($a in $amd) {
                if ($a.ErrorCode -eq 0) { Add-Result 'AMD' 'PASS' ('Found ' + $a.Name) }
                else { Add-Result 'AMD' 'WARN' ($a.Name + ' is disabled or has a problem (Device Manager code ' + $a.ErrorCode + ').') }
            }
        }
    }
    catch { Add-Result 'AMD' 'WARN' ('Could not list display adapters: ' + $_.Exception.Message) }

    if (Test-GpuCountersPresent) { Add-Result 'AMD' 'PASS' 'Windows GPU performance counters are available' }
    else { Add-Result 'AMD' 'WARN' 'Windows GPU performance counters are missing, so the AMD card cannot show load. Fix: open Terminal as administrator, run  lodctr /R , then restart FluxGPU. (This one fix needs admin; setup does not do it for you.)' }
}

# ---------------------------------------------------------------------------------------------------
# Step 4: build and test
# ---------------------------------------------------------------------------------------------------

function Invoke-BuildCmd($Ctx, [string]$Argument, [string]$Label) {
    $cmdArgs = Get-CmdArguments $Ctx.BuildCmd $Argument
    if ($script:IsDryRun) {
        Write-Host ('  (dry run) would run: ' + $env:ComSpec + ' ' + $cmdArgs) -ForegroundColor DarkYellow
        Write-SetupLog 'INFO' ('would run build.cmd ' + $Argument)
        return $null
    }
    Write-Info ($Label + '...')
    $r = Invoke-ProcessCapture $env:ComSpec $cmdArgs 300000 $Ctx.Root
    Write-SetupLog 'INFO' ('build.cmd ' + $Argument + ' exit ' + $r.ExitCode + ' output:' + [Environment]::NewLine + $r.StdOut + $r.StdErr)
    return $r
}

function Invoke-StepBuild($Ctx) {
    Write-Step 4 'Build FluxGPU and run its tests'
    if (-not $Ctx.CscOk) { Add-Result 'Build' 'SKIP' 'Skipped because the C# compiler was not found (step 2).'; return }
    if (-not (Test-PathSafeForCmd $Ctx.Root)) {
        Add-Result 'Build' 'FAIL' ('The folder path contains a % or " character, which the build script cannot handle: ' + $Ctx.Root + '. Move the FluxGPU folder (for example to C:\FluxGPU) and run setup again.')
        return
    }
    if (-not (Close-FluxIfRunning $Ctx 'to rebuild it')) { Add-Result 'Build' 'SKIP' 'Skipped: FluxGPU is still running.'; return }

    $r = Invoke-BuildCmd $Ctx '' 'Building FluxGPU.exe (about 10 seconds)'
    if ($null -ne $r) {
        if ($r.TimedOut -or $r.ExitCode -ne 0 -or -not (Test-PathExists $Ctx.Exe)) {
            Add-Result 'Build' 'FAIL' ('The build failed (exit code ' + $r.ExitCode + '). Last lines:')
            Show-OutputTail ($r.StdOut + "`n" + $r.StdErr) 15
            if (($r.StdOut + $r.StdErr) -match 'warning') { Write-Info 'If the only problem is a warning, run  set FLUXGPU_ALLOW_WARNINGS=1  and then build.cmd, and report the warning.' }
            return
        }
        Add-Result 'Build' 'PASS' ('Built ' + $Ctx.Exe)
    }

    $t = Invoke-BuildCmd $Ctx 'test' 'Running the tests'
    if ($null -ne $t) {
        $summary = @(([string]$t.StdOut).Split("`n") | Where-Object { $_ -match 'checks passed' } | Select-Object -Last 1)
        $sumText = 'see the setup log'
        if ($summary.Count -gt 0) { $sumText = $summary[0].Trim() }
        if (-not $t.TimedOut -and $t.ExitCode -eq 0) { Add-Result 'Tests' 'PASS' ('Tests passed: ' + $sumText) }
        else {
            Add-Result 'Tests' 'FAIL' ('Some tests failed (exit code ' + $t.ExitCode + '): ' + $sumText)
            $fails = @(([string]$t.StdOut).Split("`n") | Where-Object { $_ -match 'FAIL' } | Select-Object -First 10)
            foreach ($f in $fails) { Write-Host ('      ' + $f.Trim()) -ForegroundColor DarkGray }
        }
    }
}

# ---------------------------------------------------------------------------------------------------
# Step 5: shortcuts and start with Windows (all per user, no admin)
# ---------------------------------------------------------------------------------------------------

function Get-ShortcutPlaces {
    return @(
        [pscustomobject]@{ Name = 'Start menu shortcut'; Path = (Join-WinPath (Get-SpecialFolder 'Programs') 'FluxGPU.lnk') },
        [pscustomobject]@{ Name = 'Desktop shortcut'; Path = (Join-WinPath (Get-SpecialFolder 'DesktopDirectory') 'FluxGPU.lnk') }
    )
}

# Only touch a FluxGPU.lnk that really points at a FluxGPU.exe.
function Test-IsFluxShortcutTarget([string]$Target) {
    return (-not [string]::IsNullOrEmpty($Target)) -and $Target.EndsWith('FluxGPU.exe', [StringComparison]::OrdinalIgnoreCase)
}

function Invoke-StepShortcuts($Ctx) {
    Write-Step 5 'Shortcuts and start with Windows (optional, no admin needed)'
    if (-not (Test-PathExists $Ctx.Exe)) { Add-Result 'Shortcuts' 'SKIP' 'Skipped: FluxGPU.exe has not been built yet.'; return }
    foreach ($place in (Get-ShortcutPlaces)) {
        $target = Get-ShortcutTarget $place.Path
        $now = 'not there now'
        if ($target) { $now = 'there now' }
        $c = Read-Choice ('Add a ' + $place.Name + '? (' + $now + ')') @('Yes', 'No') $(if ($target) { 1 } else { 2 })
        if ($c -eq 1) {
            if (Invoke-Change ('create ' + $place.Path) { New-FluxShortcut $place.Path $Ctx.Exe $Ctx.Root }) { Add-Result 'Shortcuts' 'PASS' ($place.Name + ': ' + $place.Path) }
        }
        elseif ($target -and (Test-IsFluxShortcutTarget $target)) {
            if (Invoke-Change ('remove ' + $place.Path) { Remove-Item -LiteralPath $place.Path -Force }) { Add-Result 'Shortcuts' 'PASS' ($place.Name + ' removed') }
        }
        else { Add-Result 'Shortcuts' 'SKIP' ($place.Name + ': not added') }
    }
    $run = Get-RunValue
    $c = Read-Choice ('Start FluxGPU automatically when you sign in to Windows? (' + $(if ($run) { 'on now' } else { 'off now' }) + ')') @('Yes', 'No') $(if ($run) { 1 } else { 2 })
    if ($c -eq 1) {
        $cmd = Get-RunCommand $Ctx.Exe
        if (Invoke-Change ('add the per-user startup entry ' + $script:RunKey + '\' + $script:RunValueName + ' = ' + $cmd) { Set-RunValue $cmd }) { Add-Result 'Startup' 'PASS' 'FluxGPU starts when you sign in (per-user setting, no admin)' }
    }
    elseif ($run) {
        if (Invoke-Change 'remove the per-user startup entry' { Remove-RunValue }) { Add-Result 'Startup' 'PASS' 'FluxGPU no longer starts when you sign in' }
    }
    else { Add-Result 'Startup' 'SKIP' 'Start with Windows: off' }
}

# ---------------------------------------------------------------------------------------------------
# Step 6: settings.ini (optional)
# ---------------------------------------------------------------------------------------------------

function Read-Temperature([string]$Prompt) {
    for ($i = 0; $i -lt 5; $i++) {
        $t = ConvertTo-TemperatureC (Read-Value $Prompt)
        if ($null -ne $t) { return $t }
        Write-Info 'Please type a number from 30 to 120.'
    }
    return $null
}

function Get-StartingSettingsLines($Ctx) {
    $lines = Read-SettingsLines $Ctx.SettingsPath
    if ($null -ne $lines) { return , $lines }
    if (Test-PathExists $Ctx.LegacySettings) {
        $legacy = ConvertFrom-LegacySettings ([IO.File]::ReadAllText($Ctx.LegacySettings))
        if ($null -ne $legacy) { Write-Info 'Bringing over the window position from the old settings.txt.'; return , $legacy }
    }
    return , (New-SettingsLines)
}

function Invoke-StepSettings($Ctx) {
    Write-Step 6 'FluxGPU settings (optional)'
    $lines = Get-StartingSettingsLines $Ctx
    $curPath = Get-SettingValue $lines 'nvidia_smi_path'
    $curWarn = Get-SettingValue $lines 'temp_warn_c'
    $curCrit = Get-SettingValue $lines 'temp_crit_c'
    $curSound = Get-SettingValue $lines 'alert_sound'
    $pathText = 'automatic search'
    if ($curPath) { $pathText = $curPath }
    $tempText = 'automatic'
    if ($curWarn -or $curCrit) { $tempText = 'warning ' + $curWarn + ' C, critical ' + $curCrit + ' C' }
    $soundText = 'on'
    if ($curSound -eq '0' -or $curSound -eq 'false') { $soundText = 'off' }
    Write-Info ('Settings file: ' + $Ctx.SettingsPath)
    Write-Info ('nvidia-smi path: ' + $pathText + ' | temperature alerts: ' + $tempText + ' | alert sound: ' + $soundText)
    $c = Read-Choice 'Change these settings now?' @('No, keep them', 'Yes') 1
    if ($c -ne 2) { Add-Result 'Settings' 'SKIP' 'Settings unchanged'; return }
    if (-not (Close-FluxIfRunning $Ctx 'so it does not overwrite the new settings when it closes')) { Add-Result 'Settings' 'SKIP' 'Skipped: FluxGPU is still running.'; return }

    $changes = @{}
    # nvidia-smi path
    $opts = New-Object System.Collections.Generic.List[string]
    $opts.Add('Keep it (' + $pathText + ')')
    $opts.Add('Automatic: search System32, then Program Files\NVIDIA Corporation\NVSMI')
    $useFound = $null
    if ($script:ChosenSmiPath -and $script:ChosenSmiPath -ne $curPath) { $useFound = $script:ChosenSmiPath; $opts.Add('Use the path from step 3: ' + $useFound) }
    $opts.Add('Type a full path to nvidia-smi.exe')
    $pick = $opts[(Read-Choice 'Where should FluxGPU find nvidia-smi?' $opts.ToArray() 1) - 1]
    if ($pick.StartsWith('Automatic')) { $changes['nvidia_smi_path'] = '' }
    elseif ($pick.StartsWith('Use the path')) { $changes['nvidia_smi_path'] = $useFound }
    elseif ($pick.StartsWith('Type')) {
        $p = Read-Value 'Full path to nvidia-smi.exe:'
        if ((Test-AbsoluteWinPath $p) -and $p.EndsWith('\nvidia-smi.exe', [StringComparison]::OrdinalIgnoreCase)) {
            if (-not (Test-PathExists $p)) { Write-Info ('Note: there is no file at ' + $p + ' right now. Saving it anyway.') }
            $changes['nvidia_smi_path'] = $p
        }
        else { Write-Info 'That is not a full path ending in \nvidia-smi.exe, so the path was not changed.' }
    }

    # temperature alerts
    $t = Read-Choice 'NVIDIA temperature alerts' @(('Keep them (' + $tempText + ')'), 'Automatic: from the GPU slowdown temperature (slowdown - 10 C and - 3 C), or 75 / 85 C', 'Set my own warning and critical temperatures') 1
    if ($t -eq 2) { $changes['temp_warn_c'] = ''; $changes['temp_crit_c'] = '' }
    elseif ($t -eq 3) {
        $w = Read-Temperature 'Warning temperature in C (30-120):'
        $k = $null
        if ($null -ne $w) { $k = Read-Temperature 'Critical temperature in C (30-120, higher than the warning):' }
        $why = Test-Thresholds $w $k
        if ($null -eq $why) { $changes['temp_warn_c'] = Format-Number $w; $changes['temp_crit_c'] = Format-Number $k }
        else { Write-Info ($why + ' The temperatures were not changed.') }
    }

    # alert sound
    $s = Read-Choice 'Alert sound' @(('Keep it (' + $soundText + ')'), 'On', 'Off') 1
    if ($s -eq 2) { $changes['alert_sound'] = '1' }
    elseif ($s -eq 3) { $changes['alert_sound'] = '0' }

    if ($changes.Count -eq 0 -and $null -ne (Read-SettingsLines $Ctx.SettingsPath)) { Add-Result 'Settings' 'SKIP' 'Nothing changed'; return }
    $newLines = Set-SettingsValues $lines $changes
    $problems = Test-SettingsLines $newLines
    if ($problems.Count -gt 0) { Add-Result 'Settings' 'FAIL' ('Not saved, because: ' + ($problems -join '; ')); return }
    if (Invoke-Change ('save ' + $changes.Count + ' setting(s) to ' + $Ctx.SettingsPath + ' (old file kept as settings.ini.bak)') { Write-SettingsAtomic $Ctx.SettingsPath $newLines }) {
        Add-Result 'Settings' 'PASS' ('Saved: ' + ((@($changes.Keys | Sort-Object) | ForEach-Object { $_ + '=' + $changes[$_] }) -join ', '))
    }
}

# ---------------------------------------------------------------------------------------------------
# Step 7: launch
# ---------------------------------------------------------------------------------------------------

function Invoke-StepLaunch($Ctx) {
    Write-Step 7 'Start FluxGPU'
    if (-not (Test-PathExists $Ctx.Exe)) { Add-Result 'Launch' 'SKIP' 'Skipped: FluxGPU.exe has not been built.'; return }
    if ((Get-FluxProcesses $Ctx.Exe).Count -gt 0) { Add-Result 'Launch' 'PASS' 'FluxGPU is already running'; return }
    $c = Read-Choice 'Start FluxGPU now?' @('Yes', 'No') 1
    if ($c -ne 1) { Add-Result 'Launch' 'SKIP' 'Not started'; return }
    if (-not (Invoke-Change ('start ' + $Ctx.Exe) { Start-Process -FilePath $Ctx.Exe -WorkingDirectory $Ctx.Root })) { Add-Result 'Launch' 'FAIL' 'FluxGPU could not be started'; return }
    if ($script:IsDryRun) { return }
    Start-Sleep -Seconds 3
    if ((Get-FluxProcesses $Ctx.Exe).Count -gt 0) { Add-Result 'Launch' 'PASS' 'FluxGPU is running. The cards say "Connecting..." for a few seconds, then show live data.' }
    else { Add-Result 'Launch' 'FAIL' ('FluxGPU closed right after starting. Open the newest log in ' + $Ctx.LogFolder) }
}

# ---------------------------------------------------------------------------------------------------
# Repair: re-check, rebuild, re-point shortcuts and the startup entry, check settings.ini
# ---------------------------------------------------------------------------------------------------

function Invoke-RepairShortcuts($Ctx) {
    Write-Step 5 'Repair shortcuts and the startup entry'
    if (-not (Test-PathExists $Ctx.Exe)) { Add-Result 'Shortcuts' 'SKIP' 'Skipped: FluxGPU.exe has not been built.'; return }
    $any = $false
    foreach ($place in (Get-ShortcutPlaces)) {
        $target = Get-ShortcutTarget $place.Path
        if (-not $target) { continue }
        $any = $true
        if ([string]::Equals($target, $Ctx.Exe, [StringComparison]::OrdinalIgnoreCase)) { Add-Result 'Shortcuts' 'PASS' ($place.Name + ' is correct'); continue }
        if (-not (Test-IsFluxShortcutTarget $target)) { Add-Result 'Shortcuts' 'WARN' ($place.Name + ' at ' + $place.Path + ' points to ' + $target + ', which is not FluxGPU. Left alone.'); continue }
        if (Invoke-Change ('point ' + $place.Path + ' at ' + $Ctx.Exe + ' (was ' + $target + ')') { New-FluxShortcut $place.Path $Ctx.Exe $Ctx.Root }) { Add-Result 'Shortcuts' 'PASS' ($place.Name + ' fixed (it pointed to ' + $target + ')') }
    }
    $run = Get-RunValue
    if ($run) {
        $any = $true
        $want = Get-RunCommand $Ctx.Exe
        if ($run -eq $want) { Add-Result 'Startup' 'PASS' 'The start-with-Windows entry is correct' }
        elseif (Invoke-Change ('update the per-user startup entry to ' + $want + ' (was ' + $run + ')') { Set-RunValue $want }) { Add-Result 'Startup' 'PASS' 'The start-with-Windows entry was fixed' }
    }
    if (-not $any) { Add-Result 'Shortcuts' 'SKIP' 'No FluxGPU shortcuts or startup entry to repair. Use Set up to add them.' }
}

function Invoke-RepairSettings($Ctx) {
    Write-Step 6 'Check settings.ini'
    $lines = Read-SettingsLines $Ctx.SettingsPath
    if ($null -eq $lines) { Add-Result 'Settings' 'PASS' 'No settings.ini yet. FluxGPU creates it the first time it closes.'; return }
    $problems = Test-SettingsLines $lines
    if ($problems.Count -eq 0) { Add-Result 'Settings' 'PASS' ('settings.ini is fine: ' + $Ctx.SettingsPath); return }
    Add-Result 'Settings' 'WARN' ('settings.ini has problems: ' + ($problems -join '; '))
    $c = Read-Choice 'FluxGPU ignores bad values, but they can hide a setting you wanted. What would you like to do?' @('Leave the file as it is', 'Back it up and start fresh (keeps the window position)') 1
    if ($c -ne 2) { return }
    if (-not (Close-FluxIfRunning $Ctx 'so it does not overwrite the repaired file')) { Add-Result 'Settings' 'SKIP' 'Skipped: FluxGPU is still running.'; return }
    $fresh = New-SettingsLines
    $keep = @{}
    foreach ($k in @('left', 'top', 'topmost', 'compact')) {
        $v = Get-SettingValue $lines $k
        if ($null -ne $v -and (Test-SettingsLines @($k + '=' + $v)).Count -eq 0) { $keep[$k] = $v }
    }
    $fresh = Set-SettingsValues $fresh $keep
    $backup = $Ctx.SettingsPath + '.bad-' + (Get-Date).ToString('yyyyMMdd-HHmmss', $script:Inv)
    if (Invoke-Change ('copy settings.ini to ' + $backup + ' and write a fresh settings.ini') {
            Copy-Item -LiteralPath $Ctx.SettingsPath -Destination $backup
            Write-SettingsAtomic $Ctx.SettingsPath $fresh
        }) { Add-Result 'Settings' 'PASS' ('Fresh settings.ini written; the old one is saved as ' + $backup) }
}

# ---------------------------------------------------------------------------------------------------
# Uninstall: shortcuts, the startup entry, and (only if you say so) %LOCALAPPDATA%\FluxGPU
# ---------------------------------------------------------------------------------------------------

function Invoke-Uninstall($Ctx) {
    $script:TotalSteps = 4
    Write-Title 'Uninstall FluxGPU'
    Write-Info 'This removes the shortcuts and the start-with-Windows entry. It asks before deleting your settings and logs.'
    Write-Info ('The FluxGPU folder itself (' + $Ctx.Root + ') is not deleted.')
    if ((Read-Choice 'Continue?' @('Yes, uninstall', 'No, cancel') 2) -ne 1) { Add-Result 'Uninstall' 'SKIP' 'Cancelled'; return }

    Write-Step 1 'Close FluxGPU'
    if ((Get-FluxProcesses $Ctx.Exe).Count -eq 0) { Add-Result 'Close' 'PASS' 'FluxGPU is not running' }
    elseif (Close-FluxIfRunning $Ctx 'before uninstalling') { Add-Result 'Close' 'PASS' 'FluxGPU closed' }
    else { Add-Result 'Close' 'WARN' 'FluxGPU is still running. Close it yourself; everything else continues.' }

    Write-Step 2 'Remove shortcuts'
    foreach ($place in (Get-ShortcutPlaces)) {
        $target = Get-ShortcutTarget $place.Path
        if (-not $target) { Add-Result 'Shortcuts' 'PASS' ($place.Name + ': none'); continue }
        if (-not (Test-IsFluxShortcutTarget $target)) { Add-Result 'Shortcuts' 'WARN' ($place.Path + ' points to ' + $target + ', which is not FluxGPU. Left alone.'); continue }
        if (Invoke-Change ('remove ' + $place.Path) { Remove-Item -LiteralPath $place.Path -Force }) { Add-Result 'Shortcuts' 'PASS' ($place.Name + ' removed') }
    }

    Write-Step 3 'Remove start with Windows'
    if (Get-RunValue) {
        if (Invoke-Change 'remove the per-user startup entry' { Remove-RunValue }) { Add-Result 'Startup' 'PASS' 'Start-with-Windows entry removed' }
    }
    else { Add-Result 'Startup' 'PASS' 'No start-with-Windows entry' }

    Write-Step 4 'Settings and logs'
    if (-not (Test-FolderExists $Ctx.DataFolder)) { Add-Result 'Data' 'PASS' 'No settings or logs folder'; return }
    if (-not (Test-IsFluxDataFolder $Ctx.DataFolder ([Environment]::GetEnvironmentVariable('LOCALAPPDATA')))) {
        Add-Result 'Data' 'FAIL' ('Refusing to delete ' + $Ctx.DataFolder + ' because it is not %LOCALAPPDATA%\FluxGPU.')
        return
    }
    $c = Read-Choice ('Delete your FluxGPU settings and logs in ' + $Ctx.DataFolder + '?') @('No, keep them', 'Yes, delete them') 1
    if ($c -ne 2) { Add-Result 'Data' 'SKIP' ('Kept ' + $Ctx.DataFolder); return }
    Write-SetupLog 'INFO' ('Deleting ' + $Ctx.DataFolder + ' (this log is inside it)')
    $logWas = $script:LogFile
    $script:LogFile = $null
    if (Invoke-Change ('delete ' + $Ctx.DataFolder) { Remove-Item -LiteralPath $Ctx.DataFolder -Recurse -Force }) {
        Add-Result 'Data' 'PASS' ('Deleted ' + $Ctx.DataFolder)
    }
    if ($script:IsDryRun) { $script:LogFile = $logWas }
}

# ---------------------------------------------------------------------------------------------------
# Flows, summary, entry point
# ---------------------------------------------------------------------------------------------------

function Invoke-Install($Ctx, [switch]$Repair) {
    $script:TotalSteps = 7
    if ($Repair) { Write-Title 'Repair FluxGPU' } else { Write-Title 'Set up FluxGPU' }
    Write-Info ('FluxGPU folder: ' + $Ctx.Root)
    if (-not (Invoke-StepWindows $Ctx)) { Write-Info 'Setup cannot continue on this PC.'; return }
    [void](Invoke-StepCompiler $Ctx)
    Invoke-StepGpus $Ctx
    Invoke-StepBuild $Ctx
    if ($Repair) { Invoke-RepairShortcuts $Ctx; Invoke-RepairSettings $Ctx }
    else { Invoke-StepShortcuts $Ctx; Invoke-StepSettings $Ctx }
    Invoke-StepLaunch $Ctx
}

function Write-Summary {
    Write-Title 'Summary'
    foreach ($r in $script:Results) {
        $color = @{ PASS = 'Green'; WARN = 'Yellow'; FAIL = 'Red'; SKIP = 'DarkGray' }[$r.Status]
        Write-Host ('  ' + $r.Status.PadRight(5) + ' ' + $r.Step.PadRight(10) + ' ' + $r.Detail) -ForegroundColor $color
    }
    $fails = @($script:Results | Where-Object { $_.Status -eq 'FAIL' }).Count
    $warns = @($script:Results | Where-Object { $_.Status -eq 'WARN' }).Count
    Write-Host ''
    if ($fails -gt 0) { Write-Host ('  ' + $fails + ' problem(s) need attention. See the FAIL lines above.') -ForegroundColor Red }
    elseif ($warns -gt 0) { Write-Host ('  Done, with ' + $warns + ' warning(s). See the WARN lines above.') -ForegroundColor Yellow }
    else { Write-Host '  All done.' -ForegroundColor Green }
    if ($script:LogFile) { Write-Host ('  Setup log: ' + $script:LogFile) }
    Write-SetupLog 'INFO' ('Finished: ' + $fails + ' fail, ' + $warns + ' warn')
    if ($fails -gt 0) { return 1 }
    return 0
}

function Resolve-RepoRoot([string]$Given) {
    $root = $Given
    if ([string]::IsNullOrEmpty($root)) { $root = Split-Path -Parent $PSScriptRoot }
    return [IO.Path]::GetFullPath($root).TrimEnd('\')
}

function Start-SetupWizard([string]$Mode, [string]$Root) {
    $script:Results.Clear()
    Initialize-SetupLog
    Write-SetupLog 'INFO' ('FluxGPU setup ' + $script:SetupVersion + ' started: ' + $Mode + ', PowerShell ' + $PSVersionTable.PSVersion + ', ' + $(if ([Environment]::Is64BitProcess) { '64-bit' } else { '32-bit' }))
    $resolved = Resolve-RepoRoot $Root
    $ctx = New-SetupContext $resolved
    if (-not (Test-PathExists $ctx.BuildCmd) -or -not (Test-PathExists (Join-Path $resolved 'FluxGPU.cs'))) {
        Write-Host ('  This does not look like the FluxGPU folder (no build.cmd and FluxGPU.cs in ' + $resolved + '). Run setup.cmd from the FluxGPU folder.') -ForegroundColor Red
        Write-SetupLog 'ERROR' ('Not a FluxGPU folder: ' + $resolved)
        return 1
    }
    Write-Title ('FluxGPU setup wizard ' + $script:SetupVersion + $(if ($script:IsDryRun) { '   (DRY RUN: nothing will be changed)' } else { '' }))
    if ($Mode -eq 'Menu') {
        Write-Info 'No admin needed. Nothing outside your own user account is changed.'
        $c = Read-Choice 'What would you like to do?' @('Set up FluxGPU (first time, or to change options)', 'Repair (rebuild, fix shortcuts, check settings)', 'Uninstall (remove shortcuts and startup; asks before deleting settings)', 'Exit') 1
        $Mode = @('Install', 'Repair', 'Uninstall', 'Exit')[$c - 1]
        if ($Mode -eq 'Exit') { Write-SetupLog 'INFO' 'Exit chosen'; return 0 }
    }
    switch ($Mode) {
        'Install' { Invoke-Install $ctx }
        'Repair' { Invoke-Install $ctx -Repair }
        'Uninstall' { Invoke-Uninstall $ctx }
    }
    return (Write-Summary)
}

# Run the wizard unless this file is being dot-sourced (the tests dot-source it to reach the functions).
if ($MyInvocation.InvocationName -ne '.') {
    $code = 1
    try { $code = Start-SetupWizard $Action $RepoRoot }
    catch {
        Write-Host ('  Setup stopped because of an unexpected error: ' + $_.Exception.Message) -ForegroundColor Red
        Write-SetupLog 'ERROR' ('Unexpected: ' + $_.Exception.ToString())
        if ($script:LogFile) { Write-Host ('  Details are in ' + $script:LogFile) }
    }
    exit $code
}
