<#
.SYNOPSIS
    Tests for tools\Setup-FluxGPU.ps1: the pure logic, plus whole wizard runs (set up, repair, uninstall, dry run)
    with every Windows-specific call replaced by a mock. Nothing on the PC is changed: files are written only
    to a temporary folder, and shortcuts, the startup entry, builds and launches are mocks.

    Windows:  powershell -NoProfile -ExecutionPolicy Bypass -File tools\Test-Setup.ps1
    pwsh:     pwsh -NoProfile -File tools/Test-Setup.ps1
    Exit code 0 = every check passed.
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Test runner output.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidOverwritingBuiltInCmdlets', '', Justification = 'Mocks.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Mocks.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Mocks keep the real signatures.')]
param()

$ErrorActionPreference = 'Stop'
$setup = Join-Path $PSScriptRoot 'Setup-FluxGPU.ps1'
. $setup

$script:Checks = 0
$script:Failed = 0
$script:Group = ''

function Write-Check([string]$Text, [string]$Color) { [Console]::ForegroundColor = $Color; [Console]::WriteLine($Text); [Console]::ResetColor() }
function Start-Group([string]$Name) { $script:Group = $Name; Write-Check ('== ' + $Name) 'Cyan' }
function Assert-True($Condition, [string]$What) {
    $script:Checks++
    if ($Condition) { return }
    $script:Failed++
    Write-Check ('   FAIL [' + $script:Group + '] ' + $What) 'Red'
}
function Assert-Equal($Expected, $Actual, [string]$What) {
    Assert-True ($Expected -eq $Actual) ($What + ' (expected "' + $Expected + '", got "' + $Actual + '")')
}

# ------------------------------------------------------------------------------------------------
Start-Group 'log stamp'
Assert-Equal '11-26-26 05:41 PM' (Format-LogStamp (Get-Date -Year 2026 -Month 11 -Day 26 -Hour 17 -Minute 41 -Second 5)) 'PM stamp'
Assert-Equal '01-02-27 09:05 AM' (Format-LogStamp (Get-Date -Year 2027 -Month 1 -Day 2 -Hour 9 -Minute 5)) 'AM stamp'

Start-Group 'Windows paths'
Assert-Equal 'C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe' (Join-WinPath 'C:\Program Files\' 'NVIDIA Corporation' 'NVSMI' 'nvidia-smi.exe') 'join with spaces'
foreach ($ok in @('C:\Windows\System32\nvidia-smi.exe', 'C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe', 'D:\My Tools\nvidia-smi.exe', '\\server\share\nvidia-smi.exe')) {
    Assert-True (Test-AbsoluteWinPath $ok) ('absolute: ' + $ok)
}
foreach ($bad in @('', 'nvidia-smi.exe', 'tools\nvidia-smi.exe', 'C:nvidia-smi.exe', 'C:/Windows/nvidia-smi.exe', 'C:\a\..\nvidia-smi.exe', 'C:\a\.\b.exe', 'C:\a\\b.exe', ' C:\x.exe', 'C:\x.exe ', 'C:\a"b.exe', 'C:\a\b:c.exe')) {
    Assert-True (-not (Test-AbsoluteWinPath $bad)) ('rejected: "' + $bad + '"')
}

Start-Group 'nvidia-smi search order (same as the app)'
$c = Get-NvidiaSmiCandidates 'D:\Custom\nvidia-smi.exe' 'C:\Windows\System32' 'C:\Program Files' 'C:\Program Files'
Assert-Equal 3 $c.Count 'override + System32 + one NVSMI (duplicate Program Files dropped)'
Assert-Equal 'D:\Custom\nvidia-smi.exe' $c[0] 'override first'
Assert-Equal 'C:\Windows\System32\nvidia-smi.exe' $c[1] 'System32 second'
Assert-Equal 'C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe' $c[2] 'NVSMI third'
$c = Get-NvidiaSmiCandidates 'nvidia-smi.exe' 'C:\Windows\System32' 'C:\Program Files' 'C:\Program Files (x86)'
Assert-Equal 3 $c.Count 'relative override ignored (never a PATH lookup)'
Assert-Equal 'C:\Program Files (x86)\NVIDIA Corporation\NVSMI\nvidia-smi.exe' $c[2] 'ProgramFiles after ProgramW6432'
$c = Get-NvidiaSmiCandidates $null 'C:\Windows\System32' $null 'relative'
Assert-Equal 1 $c.Count 'missing or relative Program Files skipped'

Start-Group 'Windows check'
Assert-Equal 'PASS' (Get-WindowsStatus ([pscustomobject]@{ IsWindows = $true; Is64 = $true; Build = 22631; Name = 'Windows 11 Pro' })).Status 'Windows 11'
Assert-Equal 'WARN' (Get-WindowsStatus ([pscustomobject]@{ IsWindows = $true; Is64 = $true; Build = 19045; Name = 'Windows 10 Pro' })).Status 'Windows 10'
Assert-Equal 'FAIL' (Get-WindowsStatus ([pscustomobject]@{ IsWindows = $true; Is64 = $false; Build = 22631; Name = 'Windows 11' })).Status '32-bit'
Assert-Equal 'FAIL' (Get-WindowsStatus ([pscustomobject]@{ IsWindows = $true; Is64 = $true; Build = 9600; Name = 'Windows 8.1' })).Status 'too old'
Assert-Equal 'FAIL' (Get-WindowsStatus ([pscustomobject]@{ IsWindows = $false; Is64 = $true; Build = 0; Name = 'Linux' })).Status 'not Windows'

Start-Group '.NET Framework names'
Assert-Equal '4.8.1' (ConvertTo-NetFrameworkName 533325) '4.8.1'
Assert-Equal '4.8' (ConvertTo-NetFrameworkName 528372) '4.8'
Assert-Equal '4.7.2' (ConvertTo-NetFrameworkName 461814) '4.7.2'
Assert-Equal $null (ConvertTo-NetFrameworkName $null) 'unknown'

Start-Group 'cmd.exe quoting (paths with spaces)'
Assert-Equal '/d /s /c ""C:\My Tools\Flux GPU\build.cmd" test"' (Get-CmdArguments 'C:\My Tools\Flux GPU\build.cmd' 'test') 'with argument'
Assert-Equal '/d /s /c ""C:\My Tools\Flux GPU\build.cmd""' (Get-CmdArguments 'C:\My Tools\Flux GPU\build.cmd' '') 'no argument'
Assert-True (Test-PathSafeForCmd 'C:\My Tools (x86)\Flux & GPU') 'spaces, brackets and & are fine inside quotes'
Assert-True (-not (Test-PathSafeForCmd 'C:\100%\FluxGPU')) '% rejected'

Start-Group 'temperatures'
Assert-Equal 85 (ConvertTo-TemperatureC '85') '85'
Assert-Equal 85.5 (ConvertTo-TemperatureC ' 85.5 ') '85.5'
foreach ($bad in @('29', '121', 'NaN', 'Infinity', 'abc', '', '-5')) { Assert-Equal $null (ConvertTo-TemperatureC $bad) ('rejected "' + $bad + '"') }
Assert-Equal $null (Test-Thresholds 80 88) 'valid pair'
Assert-True ($null -ne (Test-Thresholds 88 88)) 'warn = crit rejected'
Assert-True ($null -ne (Test-Thresholds 90 80)) 'warn > crit rejected'
Assert-True ($null -ne (Test-Thresholds $null 80)) 'missing warn rejected'

Start-Group 'settings.ini rules (same as the app)'
# Exactly what FluxGPU 0.2.0 writes (AppSettings.Serialize).
$appFile = @('# FluxGPU settings (key=value). Edit only while FluxGPU is closed. Blank = automatic.', 'version=1', 'position_units=dip-system',
    'left=1520', 'top=40.5', 'topmost=1', 'compact=0', '# NVIDIA temperature alerts. Blank = GPU slowdown temp - 10 (warn) and - 3 (critical), else 75 / 85.',
    'temp_warn_c=', 'temp_crit_c=', 'temp_hysteresis_c=3', 'alert_sound=1', '# Full path to nvidia-smi.exe. Blank = System32, then Program Files\NVIDIA Corporation\NVSMI.',
    'nvidia_smi_path=', 'migrated_settings_txt=0')
Assert-Equal 0 (Test-SettingsLines $appFile).Count 'the app''s own file has no problems'
Assert-Equal 1 (Test-SettingsLines @('colour=blue')).Count 'unknown key'
Assert-Equal 1 (Test-SettingsLines @('topmost=yes')).Count 'bad bool'
Assert-Equal 1 (Test-SettingsLines @('temp_warn_c=200')).Count 'temp out of range'
Assert-Equal 1 (Test-SettingsLines @('temp_warn_c=90', 'temp_crit_c=85')).Count 'warn above crit'
Assert-Equal 1 (Test-SettingsLines @('nvidia_smi_path=nvidia-smi.exe')).Count 'relative nvidia-smi path'
Assert-Equal 1 (Test-SettingsLines @('just text')).Count 'not key=value'
Assert-Equal 0 (Test-SettingsLines @('nvidia_smi_path=C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe')).Count 'path with spaces is fine'

Start-Group 'settings.ini editing keeps everything else'
$new = Set-SettingsValues $appFile @{ temp_warn_c = '80'; temp_crit_c = '88'; nvidia_smi_path = 'D:\My Tools\nvidia-smi.exe' }
Assert-Equal $appFile.Count $new.Count 'same number of lines'
Assert-Equal 'temp_warn_c=80' $new[8] 'warn replaced in place'
Assert-Equal 'nvidia_smi_path=D:\My Tools\nvidia-smi.exe' $new[13] 'path replaced in place'
Assert-Equal 'left=1520' $new[3] 'window position kept'
Assert-Equal $appFile[7] $new[7] 'comment kept'
$new = Set-SettingsValues @('# c', 'alert_sound=1', 'alert_sound=0') @{ alert_sound = '0'; temp_warn_c = '70' }
Assert-Equal 3 $new.Count 'duplicate dropped, missing key appended'
Assert-Equal 'alert_sound=0' $new[1] 'first kept and set'
Assert-Equal 'temp_warn_c=70' $new[2] 'appended'
Assert-Equal '1520' (Get-SettingValue $appFile 'left') 'read a value'
Assert-Equal $null (Get-SettingValue $appFile 'nope') 'missing value'

Start-Group 'old settings.txt'
$l = ConvertFrom-LegacySettings "1520.5|40|1|0`r`n"
Assert-True ($l -contains 'left=1520.5') 'left'
Assert-True ($l -contains 'top=40') 'top'
Assert-True ($l -contains 'topmost=1') 'topmost'
Assert-True ($l -contains 'compact=0') 'compact'
Assert-True ($l -contains 'migrated_settings_txt=1') 'marked migrated'
Assert-Equal 0 (Test-SettingsLines $l).Count 'migrated file is valid'
Assert-Equal $null (ConvertFrom-LegacySettings 'garbage') 'not the old format'
Assert-Equal $null (ConvertFrom-LegacySettings 'x|y|1|0') 'not numbers'

Start-Group 'atomic settings write (folder with spaces)'
$tmpRoot = Join-Path ([IO.Path]::GetTempPath()) ('FluxGPU setup tests ' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$iniPath = Join-Path (Join-Path $tmpRoot 'App Data') 'settings.ini'
Write-SettingsAtomic $iniPath @('a=1', 'b=2')
Assert-Equal "a=1`r`nb=2`r`n" ([IO.File]::ReadAllText($iniPath)) 'first write'
Write-SettingsAtomic $iniPath @('a=3')
Assert-Equal "a=3`r`n" ([IO.File]::ReadAllText($iniPath)) 'replaced'
Assert-Equal "a=1`r`nb=2`r`n" ([IO.File]::ReadAllText($iniPath + '.bak')) 'previous file kept as .bak'
Assert-True (-not (Test-Path -LiteralPath ($iniPath + '.tmp'))) 'no temp file left'
$bytes = [IO.File]::ReadAllBytes($iniPath)
Assert-True ($bytes[0] -ne 0xEF) 'no UTF-8 BOM'
$back = Read-SettingsLines $iniPath
Assert-Equal 1 $back.Count 'read back one line'
Assert-Equal 'a=3' $back[0] 'read back value'

Start-Group 'data folder guard'
$lad = Join-Path $tmpRoot 'Local'
Assert-True (Test-IsFluxDataFolder (Join-Path $lad 'FluxGPU') $lad) 'the FluxGPU folder'
Assert-True (-not (Test-IsFluxDataFolder $lad $lad)) 'never LOCALAPPDATA itself'
Assert-True (-not (Test-IsFluxDataFolder (Join-Path $lad 'Other') $lad)) 'never another folder'
Assert-True (-not (Test-IsFluxDataFolder '' $lad)) 'never empty'

# ------------------------------------------------------------------------------------------------
# Whole wizard runs with mocks. Everything Windows-specific is replaced below; files are only written
# under $tmpRoot (a fake LOCALAPPDATA and a fake FluxGPU folder whose path has spaces).
# ------------------------------------------------------------------------------------------------

$env:LOCALAPPDATA = Join-Path $tmpRoot 'Local AppData'
$env:ComSpec = 'C:\Windows\system32\cmd.exe'
$env:ProgramW6432 = 'C:\Program Files'
$env:ProgramFiles = 'C:\Program Files'
$repo = Join-Path $tmpRoot 'My Projects\Flux GPU'
New-Item -ItemType Directory -Path $repo -Force | Out-Null
Set-Content -LiteralPath (Join-Path $repo 'build.cmd') -Value '@echo off'
Set-Content -LiteralPath (Join-Path $repo 'FluxGPU.cs') -Value '// stub'
$exe = Join-Path $repo 'FluxGPU.exe'

function Reset-Mocks {
    $script:M = @{
        Answers = New-Object System.Collections.Queue; Values = New-Object System.Collections.Queue
        Questions = New-Object System.Collections.ArrayList; Output = New-Object System.Collections.ArrayList
        Fake = @{}                                     # extra paths that "exist" (Windows paths)
        Build = 0; Test = 0; Smi = @{ Exit = 0; Out = 'Tesla P40, 580.95.05, 45'; TimedOut = $false }
        Calls = New-Object System.Collections.ArrayList
        Shortcuts = @{}; Run = $null; Running = $false; Amd = @([pscustomobject]@{ Name = 'AMD Radeon(TM) Graphics'; ErrorCode = 0 })
        Counters = $true; ExeBuilt = $true
    }
    $script:M.Fake['C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'] = $true
    $script:M.Fake['C:\Windows\System32\nvidia-smi.exe'] = $true
    $script:ChosenSmiPath = $null; $script:FoundSmiPath = $null
}
function Set-Answers([int[]]$Choices, [string[]]$Typed) {
    foreach ($a in $Choices) { $script:M.Answers.Enqueue($a) }
    foreach ($v in $Typed) { $script:M.Values.Enqueue($v) }
}

# --- mocks (same names and parameters as the real functions) ---
function Write-Host { param([Parameter(Position = 0)] $Object, $ForegroundColor, [switch]$NoNewline) [void]$script:M.Output.Add([string]$Object) }
function Read-Choice([string]$Question, [string[]]$Options, [int]$Default = 1) {
    [void]$script:M.Questions.Add($Question)
    if ($script:M.Answers.Count -eq 0) { throw ('Unexpected question: ' + $Question) }
    $n = $script:M.Answers.Dequeue()
    if ($n -lt 1 -or $n -gt $Options.Count) { throw ('Answer ' + $n + ' is not an option for: ' + $Question) }
    return $n
}
function Read-Value([string]$Prompt) {
    if ($script:M.Values.Count -eq 0) { throw ('Unexpected prompt: ' + $Prompt) }
    return $script:M.Values.Dequeue()
}
function Get-WindowsInfo { [pscustomobject]@{ IsWindows = $true; Is64 = $true; Build = 22631; Name = 'Microsoft Windows 11 Pro' } }
function Test-IsAdmin { $false }
function Get-BlockedFiles([string]$Root) { , @() }
function Get-FrameworkFolder { 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319' }
function Get-SystemFolder { 'C:\Windows\System32' }
function Get-NetFrameworkRelease { 533325 }
function Get-AmdAdapters { , @($script:M.Amd) }
function Test-GpuCountersPresent { $script:M.Counters }
function Test-PathExists([string]$Path) {
    if ([string]::IsNullOrEmpty($Path)) { return $false }
    if ($Path -eq $exe) { return $script:M.ExeBuilt }
    if ($script:M.Fake.ContainsKey($Path)) { return $true }
    if ($script:M.Shortcuts.ContainsKey($Path)) { return $true }
    return (Test-Path -LiteralPath $Path -PathType Leaf)
}
function Test-FolderExists([string]$Path) {
    if ($Path -eq 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF') { return $true }
    return (Test-Path -LiteralPath $Path -PathType Container)
}
function Get-FluxProcesses([string]$ExePath) {
    if ($script:M.Running) { return , @([pscustomobject]@{ Id = 4242 }) }
    return , @()
}
function Get-SpecialFolder([string]$Name) { @{ Programs = 'C:\Users\Kevin\AppData\Roaming\Microsoft\Windows\Start Menu\Programs'; DesktopDirectory = 'C:\Users\Kevin\Desktop' }[$Name] }
function Get-ShortcutTarget([string]$LinkPath) { if ($script:M.Shortcuts.ContainsKey($LinkPath)) { return $script:M.Shortcuts[$LinkPath] }; return $null }
function New-FluxShortcut([string]$LinkPath, [string]$ExePath, [string]$WorkingFolder) { [void]$script:M.Calls.Add('shortcut ' + $LinkPath + ' -> ' + $ExePath); $script:M.Shortcuts[$LinkPath] = $ExePath }
function Remove-Item { param([string]$LiteralPath, [switch]$Force, [switch]$Recurse)
    if ($script:M.Shortcuts.ContainsKey($LiteralPath)) { [void]$script:M.Calls.Add('remove ' + $LiteralPath); $script:M.Shortcuts.Remove($LiteralPath); return }
    [void]$script:M.Calls.Add('delete ' + $LiteralPath)
    Microsoft.PowerShell.Management\Remove-Item -LiteralPath $LiteralPath -Force:$Force -Recurse:$Recurse
}
function Get-RunValue { $script:M.Run }
function Set-RunValue([string]$Command) { [void]$script:M.Calls.Add('run= ' + $Command); $script:M.Run = $Command }
function Remove-RunValue { [void]$script:M.Calls.Add('run removed'); $script:M.Run = $null }
function Start-Process { param([string]$FilePath, [string]$WorkingDirectory) [void]$script:M.Calls.Add('start ' + $FilePath + ' in ' + $WorkingDirectory); $script:M.Running = $true }
function Start-Sleep { param([int]$Seconds) }
function Invoke-ProcessCapture([string]$FilePath, [string]$Arguments, [int]$TimeoutMs, [string]$WorkingFolder) {
    [void]$script:M.Calls.Add('exec ' + $FilePath + ' ' + $Arguments + ' (timeout ' + $TimeoutMs + ', in ' + $WorkingFolder + ')')
    if ($FilePath -like '*nvidia-smi.exe') {
        $code = $script:M.Smi.Exit
        if ($script:M.Smi.TimedOut) { $code = $null }
        return [pscustomobject]@{ ExitCode = $code; StdOut = $script:M.Smi.Out; StdErr = ''; TimedOut = $script:M.Smi.TimedOut }
    }
    if ($Arguments.EndsWith(' test"')) {
        $t = $script:M.Test
        return [pscustomobject]@{ ExitCode = $t; StdOut = $(if ($t -eq 0) { "PASS csv`r`n145 checks passed, 0 failed" } else { "FAIL csv: boom`r`n144 checks passed, 1 failed" }); StdErr = ''; TimedOut = $false }
    }
    return [pscustomobject]@{ ExitCode = $script:M.Build; StdOut = $(if ($script:M.Build -eq 0) { 'Done. Run FluxGPU.exe' } else { "FluxGPU.cs(10,5): error CS1002: ; expected`r`nBuild failed." }); StdErr = ''; TimedOut = $false }
}
function Get-StatusOf([string]$Step) { @($script:Results | Where-Object { $_.Step -eq $Step } | ForEach-Object { $_.Status }) }
function Test-CallLike([string]$Pattern) { @($script:M.Calls | Where-Object { $_ -like $Pattern }).Count }
$dataFolder = Join-Path $env:LOCALAPPDATA 'FluxGPU'
$settingsIni = Join-Path $dataFolder 'settings.ini'
$startLnk = 'C:\Users\Kevin\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\FluxGPU.lnk'
$deskLnk = 'C:\Users\Kevin\Desktop\FluxGPU.lnk'

# ------------------------------------------------------------------------------------------------
Start-Group 'wizard: full set-up (Windows 11, P40 + AMD, every option on)'
Reset-Mocks
$script:IsDryRun = $false
# 5: Start menu yes, desktop yes, startup yes | 6: change yes, path keep, temps own, sound off | 7: launch yes
Set-Answers @(1, 1, 1, 2, 1, 3, 3, 1) @('80', '88')
$code = Start-SetupWizard 'Install' $repo
Assert-Equal 0 $code 'exit code 0'
Assert-Equal 0 @($script:Results | Where-Object { $_.Status -eq 'FAIL' }).Count 'no FAIL'
Assert-True ((Get-StatusOf 'Windows') -contains 'PASS') 'Windows PASS'
Assert-True ((Get-StatusOf 'Compiler') -contains 'PASS') 'compiler PASS'
Assert-True (@($script:Results | Where-Object { $_.Step -eq 'NVIDIA' -and $_.Detail -like '*Tesla P40, driver 580.95.05, 45 C*' }).Count -eq 1) 'nvidia-smi test query shown'
Assert-True ((Get-StatusOf 'AMD') -contains 'PASS') 'AMD PASS'
Assert-Equal 1 (Test-CallLike ('exec C:\Windows\System32\nvidia-smi.exe --query-gpu=name,driver_version,temperature.gpu --format=csv,noheader (timeout 10000*')) 'nvidia-smi run with a 10 s limit and safe fields'
$buildCmd = Join-Path $repo 'build.cmd'
Assert-Equal 1 (Test-CallLike ('exec C:\Windows\system32\cmd.exe /d /s /c ""' + $buildCmd + '"" (timeout 300000, in ' + $repo + ')')) 'build.cmd run with quoted path'
Assert-Equal 1 (Test-CallLike ('exec C:\Windows\system32\cmd.exe /d /s /c ""' + $buildCmd + '" test"*')) 'build.cmd test run'
Assert-True (@($script:Results | Where-Object { $_.Step -eq 'Tests' -and $_.Detail -like '*145 checks passed, 0 failed*' }).Count -eq 1) 'test summary shown'
Assert-Equal $exe $script:M.Shortcuts[$startLnk] 'Start menu shortcut'
Assert-Equal $exe $script:M.Shortcuts[$deskLnk] 'desktop shortcut'
Assert-Equal ('"' + $exe + '"') $script:M.Run 'startup entry is the quoted exe path'
$ini = Read-SettingsLines $settingsIni
Assert-True ($null -ne $ini) 'settings.ini written'
Assert-Equal '80' (Get-SettingValue $ini 'temp_warn_c') 'warn saved'
Assert-Equal '88' (Get-SettingValue $ini 'temp_crit_c') 'crit saved'
Assert-Equal '0' (Get-SettingValue $ini 'alert_sound') 'sound off saved'
Assert-Equal 0 (Test-SettingsLines $ini).Count 'written file passes the app rules'
Assert-Equal 1 (Test-CallLike ('start ' + $exe + ' in ' + $repo)) 'launched from its folder'
Assert-True ((Get-StatusOf 'Launch') -contains 'PASS') 'launch PASS'
Assert-True (@($script:M.Output | Where-Object { $_ -like 'Step 7 of 7: *' }).Count -eq 1) 'numbered steps shown'
$logs = @(Get-ChildItem -LiteralPath (Join-Path $dataFolder 'logs') -Filter 'fluxgpu-setup-*.log')
Assert-Equal 1 $logs.Count 'setup log created'
$logText = [IO.File]::ReadAllText($logs[0].FullName)
Assert-True ($logText -match '(?m)^\d\d-\d\d-\d\d \d\d:\d\d (AM|PM) \[INFO\] FluxGPU setup 0\.3\.0-dev started') 'log line uses MM-DD-YY HH:MM AM/PM'
Assert-Equal 0 $script:M.Answers.Count 'every planned answer was used'

Start-Group 'wizard: nvidia-smi missing, user types the path, step 6 saves it'
Reset-Mocks
Remove-Item -LiteralPath $dataFolder -Recurse -Force
$script:M.Fake.Remove('C:\Windows\System32\nvidia-smi.exe')
$script:M.Fake['D:\NVIDIA Tools\nvidia-smi.exe'] = $true
$script:M.Amd = @()
$script:M.Counters = $false
# 3: type path | 5: no, no, no | 6: change yes, use path from step 3, temps keep, sound keep | 7: no
Set-Answers @(1, 2, 2, 2, 2, 3, 1, 1, 2) @('D:\NVIDIA Tools\nvidia-smi.exe')
$code = Start-SetupWizard 'Install' $repo
Assert-Equal 0 $code 'exit code 0 (warnings only)'
$nv = @($script:Results | Where-Object { $_.Step -eq 'NVIDIA' })
Assert-True ($nv[0].Status -eq 'WARN' -and $nv[0].Detail -like '*Looked in: C:\Windows\System32\nvidia-smi.exe; C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe*') 'not found lists where it looked'
Assert-True ($nv[1].Status -eq 'PASS') 'typed path tested'
Assert-True (@($script:Results | Where-Object { $_.Step -eq 'AMD' -and $_.Detail -like 'No AMD graphics adapter*' }).Count -eq 1) 'no AMD warning'
Assert-True (@($script:Results | Where-Object { $_.Step -eq 'AMD' -and $_.Detail -like '*lodctr /R*admin*' }).Count -eq 1) 'counter fix says it needs admin'
Assert-Equal 'D:\NVIDIA Tools\nvidia-smi.exe' (Get-SettingValue (Read-SettingsLines $settingsIni) 'nvidia_smi_path') 'path saved'
Assert-Equal 0 $script:M.Shortcuts.Count 'no shortcuts'
Assert-Equal $null $script:M.Run 'no startup entry'
Assert-Equal 0 (Test-CallLike 'start *') 'not launched'

Start-Group 'wizard: old settings.txt is carried over when settings.ini does not exist'
Reset-Mocks
Remove-Item -LiteralPath $dataFolder -Recurse -Force
Set-Content -LiteralPath (Join-Path $repo 'settings.txt') -Value '1200|300|0|1'
Set-Answers @(2, 2, 2, 2, 1, 1, 2, 2) @()   # 5: no x3 | 6: change, keep path, keep temps, sound on | 7: no
$code = Start-SetupWizard 'Install' $repo
$ini = Read-SettingsLines $settingsIni
Assert-Equal '1200' (Get-SettingValue $ini 'left') 'left migrated'
Assert-Equal '1' (Get-SettingValue $ini 'compact') 'compact migrated'
Assert-Equal '1' (Get-SettingValue $ini 'migrated_settings_txt') 'marked migrated so the app does not migrate again'
Assert-Equal '1' (Get-SettingValue $ini 'alert_sound') 'sound set'
Microsoft.PowerShell.Management\Remove-Item -LiteralPath (Join-Path $repo 'settings.txt')

Start-Group 'wizard: build failure and running app'
Reset-Mocks
$script:M.Build = 1
$script:M.Running = $true
Set-Answers @(2, 2, 2, 2, 1) @()   # 4: skip closing FluxGPU... then 5: no x3, 6: no
$code = Start-SetupWizard 'Install' $repo
Assert-True ((Get-StatusOf 'Build') -contains 'SKIP') 'build skipped when FluxGPU is left running'
Assert-True ((Get-StatusOf 'Launch') -contains 'PASS') 'already running is reported'
Reset-Mocks
$script:M.Build = 1
$script:M.ExeBuilt = $false
Set-Answers @(1) @()   # 6: keep settings (5 and 7 skip because there is no exe)
$code = Start-SetupWizard 'Install' $repo
Assert-Equal 1 $code 'exit code 1 on a failed build'
Assert-True ((Get-StatusOf 'Build') -contains 'FAIL') 'build FAIL'
Assert-True (@($script:M.Output | Where-Object { $_ -like '*error CS1002*' }).Count -eq 1) 'compiler error shown'
Assert-Equal 0 (Test-CallLike '* test"*') 'tests not run after a failed build'
Assert-True ((Get-StatusOf 'Shortcuts') -contains 'SKIP') 'shortcuts skipped without an exe'
Reset-Mocks
$script:M.Test = 1
Set-Answers @(2, 2, 2, 1, 2) @()
$code = Start-SetupWizard 'Install' $repo
Assert-True ((Get-StatusOf 'Tests') -contains 'FAIL') 'failing tests reported'

Start-Group 'wizard: dry run changes nothing'
Reset-Mocks
Remove-Item -LiteralPath $dataFolder -Recurse -Force
$script:IsDryRun = $true
Set-Answers @(1, 1, 1, 2, 2, 2, 2, 1) @()   # yes to everything that changes something
$code = Start-SetupWizard 'Install' $repo
Assert-Equal 0 (Test-CallLike 'shortcut *') 'no shortcuts created'
Assert-Equal 0 (Test-CallLike 'run= *') 'no startup entry'
Assert-Equal 0 (Test-CallLike 'start *') 'not launched'
Assert-Equal 0 (Test-CallLike '*cmd.exe*') 'build not run'
Assert-Equal 1 (Test-CallLike '*nvidia-smi.exe --query-gpu*') 'read-only nvidia-smi check still runs'
Assert-True (-not (Test-Path -LiteralPath $settingsIni)) 'settings.ini not written'
Assert-True (@($script:M.Output | Where-Object { $_ -like '*(dry run) would: create*FluxGPU.lnk*' }).Count -eq 2) 'dry run lists the shortcuts it would make'
$script:IsDryRun = $false

Start-Group 'repair: re-point moved shortcuts, fix startup entry, reset a bad settings.ini'
Reset-Mocks
$script:M.Shortcuts[$startLnk] = 'D:\Old Place\FluxGPU.exe'
$script:M.Shortcuts[$deskLnk] = 'C:\Games\Other.exe'
$script:M.Run = '"D:\Old Place\FluxGPU.exe"'
Write-SettingsAtomic $settingsIni @('left=10', 'top=20', 'colour=blue', 'temp_warn_c=500')
Set-Answers @(2, 2) @()   # 6: back up and start fresh | 7: no
$code = Start-SetupWizard 'Repair' $repo
Assert-Equal $exe $script:M.Shortcuts[$startLnk] 'Start menu shortcut re-pointed'
Assert-Equal 'C:\Games\Other.exe' $script:M.Shortcuts[$deskLnk] 'unrelated shortcut left alone'
Assert-Equal ('"' + $exe + '"') $script:M.Run 'startup entry fixed'
$ini = Read-SettingsLines $settingsIni
Assert-Equal 0 (Test-SettingsLines $ini).Count 'fresh settings.ini is valid'
Assert-Equal '10' (Get-SettingValue $ini 'left') 'window position kept'
Assert-Equal 1 @(Get-ChildItem -LiteralPath $dataFolder -Filter 'settings.ini.bad-*').Count 'bad file backed up'
Assert-True ((Get-StatusOf 'Build') -contains 'PASS') 'repair rebuilds'

Start-Group 'uninstall: keep data'
Reset-Mocks
$script:M.Shortcuts[$startLnk] = $exe
$script:M.Shortcuts[$deskLnk] = 'C:\Games\Other.exe'
$script:M.Run = '"' + $exe + '"'
Set-Answers @(1, 1) @()   # continue | keep data
$code = Start-SetupWizard 'Uninstall' $repo
Assert-Equal 0 $code 'exit code 0'
Assert-True (-not $script:M.Shortcuts.ContainsKey($startLnk)) 'Start menu shortcut removed'
Assert-True ($script:M.Shortcuts.ContainsKey($deskLnk)) 'unrelated desktop shortcut kept'
Assert-Equal $null $script:M.Run 'startup entry removed'
Assert-True (Test-Path -LiteralPath $dataFolder) 'data kept when asked'
Assert-True (@($script:M.Output | Where-Object { $_ -like 'Step 4 of 4: *' }).Count -eq 1) 'uninstall steps numbered'

Start-Group 'uninstall: delete data, and cancel'
Reset-Mocks
Set-Answers @(1, 2) @()   # continue | delete
$code = Start-SetupWizard 'Uninstall' $repo
Assert-True (-not (Test-Path -LiteralPath $dataFolder)) 'data folder deleted when asked'
Assert-Equal 1 (Test-CallLike ('delete ' + $dataFolder)) 'only that folder deleted'
Assert-True (Test-Path -LiteralPath $repo) 'FluxGPU folder untouched'
Reset-Mocks
$script:M.Run = 'x'
Set-Answers @(2) @()
$code = Start-SetupWizard 'Uninstall' $repo
Assert-Equal 'x' $script:M.Run 'cancel changes nothing'

Start-Group 'menu and wrong folder'
Reset-Mocks
Set-Answers @(4) @()
Assert-Equal 0 (Start-SetupWizard 'Menu' $repo) 'Exit from the menu'
Assert-Equal 1 (Start-SetupWizard 'Install' $tmpRoot) 'refuses a folder without build.cmd'

# ------------------------------------------------------------------------------------------------
Microsoft.PowerShell.Management\Remove-Item -LiteralPath $tmpRoot -Recurse -Force
[Console]::WriteLine('')
if ($script:Failed -eq 0) { Write-Check ('' + $script:Checks + ' checks passed, 0 failed') 'Green'; exit 0 }
Write-Check ('' + ($script:Checks - $script:Failed) + ' checks passed, ' + $script:Failed + ' failed') 'Red'
exit 1
