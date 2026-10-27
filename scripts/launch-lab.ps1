# Starts up to four isolated lab copies: a BepInEx host and MelonLoader/BepInEx clients. By default each one starts
# minimized in the taskbar ("Spooky lab - host" and so on) as a small window (480x270, lowest quality, 30 fps,
# BelowNormal priority) that restores at the right edge of the primary monitor; nothing is activated or made topmost.
# -Hidden hides them instead.
# Test-owned PIDs go to private/lab-pids.txt; stop only those (never a user-owned game process).
param(
    [int]$Players = 2, [int]$Seconds = 0, [switch]$Hidden,
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
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr hWnd, out RECT rect);
[DllImport("user32.dll")] public static extern bool SetWindowPlacement(System.IntPtr hWnd, ref WINDOWPLACEMENT placement);
[DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern bool SetWindowText(System.IntPtr hWnd, string text);
[DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetClassName(System.IntPtr hWnd, System.Text.StringBuilder name, int size);
public static bool IsUnity(System.IntPtr h) { var b = new System.Text.StringBuilder(64); GetClassName(h, b, 64); return b.ToString() == "UnityWndClass"; }
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct POINT { public int X, Y; }
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct WINDOWPLACEMENT { public int length, flags, showCmd; public POINT minPosition, maxPosition; public RECT normalPosition; }
'@
Add-Type -AssemblyName System.Windows.Forms
$work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea

# Minimized in the taskbar without activating (SW_SHOWMINNOACTIVE); restoring a copy brings it up small at the right
# edge, top to bottom, with a second column to the left when four don't fit. Called once per copy, so a copy the user
# restored is never minimized again.
function Park([System.Diagnostics.Process]$p, [int]$slot, [string]$name) {
    $h = $p.MainWindowHandle
    [SpookyLab.Win]::SetWindowText($h, "Spooky lab - $name") | Out-Null
    $r = New-Object SpookyLab.Win+RECT
    [SpookyLab.Win]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = [Math]::Max(200, $r.Right - $r.Left); $hh = [Math]::Max(150, $r.Bottom - $r.Top)
    $rows = [Math]::Max(1, [Math]::Floor($work.Height / $hh))
    $col = [Math]::Floor($slot / $rows); $row = $slot % $rows
    $place = New-Object SpookyLab.Win+WINDOWPLACEMENT
    $place.length = [System.Runtime.InteropServices.Marshal]::SizeOf($place)
    $place.showCmd = 7
    $place.normalPosition.Left = $work.Right - ($col + 1) * $w; $place.normalPosition.Top = $work.Top + $row * $hh
    $place.normalPosition.Right = $place.normalPosition.Left + $w; $place.normalPosition.Bottom = $place.normalPosition.Top + $hh
    [SpookyLab.Win]::SetWindowPlacement($h, [ref]$place) | Out-Null
}

$logs = Join-Path $modRoot 'private/lab-logs'
New-Item -ItemType Directory -Force $logs | Out-Null
Get-ChildItem $logs -Filter '*.log' -ErrorAction SilentlyContinue | Remove-Item -Force
$window = '-screen-fullscreen 0 -screen-width 480 -screen-height 270 -screen-quality Fastest'
$started = @()
foreach ($lab in $labs) {
    $dir = Join-Path $modRoot "private/$($lab.Dir)"
    # Every run starts in LAN mode: a tour that stopped early must not leave a copy in Steam mode (Steam tours pass
    # -st-steam / -st-join-steam, which override the saved setting).
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
    # MelonLoader's console would be a second window (and the process's first main window): keep it hidden.
    if ($lab.Loader -eq 'MelonLoader') { $role += ' --melonloader.hideconsole' }
    $p = Start-Process -FilePath (Join-Path $dir 'SpookyUnity.exe') -WorkingDirectory $dir -PassThru -WindowStyle Minimized `
        -ArgumentList "$window -logFile `"$log`" -st-lab $role"
    $started += $p
    try { $p.PriorityClass = 'BelowNormal' } catch { Write-Output "could not lower priority of $($p.Id)" }
    for ($i = 0; $i -lt 150; $i++) {
        $p.Refresh()
        if ($p.MainWindowHandle -ne [IntPtr]::Zero -and [SpookyLab.Win]::IsUnity($p.MainWindowHandle)) {
            # Hidden: never shown. Otherwise minimized, restoring at the side, without taking focus.
            if ($Hidden) { [SpookyLab.Win]::ShowWindow($p.MainWindowHandle, 0) | Out-Null } else { Park $p ($started.Count - 1) $lab.Name }
            break
        }
        Start-Sleep -Milliseconds 100
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
