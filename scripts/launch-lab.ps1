# Starts up to four isolated lab copies: a BepInEx host and MelonLoader/BepInEx clients, with their windows hidden.
# Test-owned PIDs go to private/lab-pids.txt; stop only those (never a user-owned game process).
param(
    [int]$Players = 2, [int]$Seconds = 0, [switch]$Visible,
    [string]$HostArgs = '', [string]$ClientArgs = '', [string]$Tour = '', [string]$JoinAddress = '127.0.0.1'
)
$ErrorActionPreference = 'Stop'
$modRoot = Split-Path $PSScriptRoot -Parent
$labs = @(
    @{ Name = 'host'; Dir = 'lab-host'; Loader = 'BepInEx' },
    @{ Name = 'c1'; Dir = 'lab-client'; Loader = 'MelonLoader' },
    @{ Name = 'c2'; Dir = 'lab-c2'; Loader = 'BepInEx' },
    @{ Name = 'c3'; Dir = 'lab-c3'; Loader = 'MelonLoader' }
) | Select-Object -First $Players

Add-Type -Namespace SpookyLab -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
'@

$logs = Join-Path $modRoot 'private/lab-logs'
New-Item -ItemType Directory -Force $logs | Out-Null
Get-ChildItem $logs -Filter '*.log' -ErrorAction SilentlyContinue | Remove-Item -Force
$window = '-screen-fullscreen 0 -screen-width 640 -screen-height 360'
$started = @()
foreach ($lab in $labs) {
    $dir = Join-Path $modRoot "private/$($lab.Dir)"
    # Every run starts in LAN mode: a tour that stopped early must not leave a copy in online mode (online tours pass
    # -st-online / -st-relay, which override the saved setting).
    $ini = Join-Path $dir 'UserData/SpookyTogether.ini'
    if (Test-Path $ini) { (Get-Content $ini) -replace '^Mode=.*$', 'Mode=lan' | Set-Content $ini }
    if ($lab.Loader -eq 'BepInEx') {
        Copy-Item -Force (Join-Path $modRoot 'artifacts/BepInEx/SpookyTogether.BepInEx.dll') (Join-Path $dir 'BepInEx/plugins/SpookyTogether/')
    } else {
        Copy-Item -Force (Join-Path $modRoot 'artifacts/MelonLoader/SpookyTogether.MelonLoader.dll') (Join-Path $dir 'Mods/')
        Remove-Item (Join-Path $dir 'MelonLoader/Latest.log') -ErrorAction SilentlyContinue
    }
    $log = Join-Path $logs "$($lab.Name).log"
    # Lab port 27120 keeps tests clear of a real game hosting on 27020 at the same time.
    $role = if ($lab.Name -eq 'host') { "-st-host -st-port 27120 -st-name Host -st-expect $Players $HostArgs" } else { "-st-join $JoinAddress -st-port 27120 -st-name $($lab.Name.ToUpper()) $ClientArgs" }
    if ($Tour) { $role += " -st-tour $Tour" }
    $p = Start-Process -FilePath (Join-Path $dir 'SpookyUnity.exe') -WorkingDirectory $dir -PassThru -WindowStyle Minimized `
        -ArgumentList "$window -logFile `"$log`" -st-lab $role"
    $started += $p
    if (!$Visible) {
        # Hide the window as soon as Unity creates it so the lab never covers the user's screen.
        for ($i = 0; $i -lt 100; $i++) { $p.Refresh(); if ($p.MainWindowHandle -ne [IntPtr]::Zero) { [SpookyLab.Win]::ShowWindow($p.MainWindowHandle, 0) | Out-Null; break }; Start-Sleep -Milliseconds 100 }
    }
    if ($lab.Name -eq 'host') { Start-Sleep 4 }
}
Set-Content (Join-Path $modRoot 'private/lab-pids.txt') (($started | ForEach-Object Id) -join "`n")
Write-Output ("started " + (($started | ForEach-Object { $_.Id }) -join ', '))
if ($Seconds -gt 0) {
    Start-Sleep $Seconds
    foreach ($p in $started) { if (!$p.HasExited) { Stop-Process -Id $p.Id -Force } }
    foreach ($p in $started) { $p.WaitForExit(15000) | Out-Null }
    Write-Output 'stopped lab processes'
}
