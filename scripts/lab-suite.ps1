# Runs every lab tour with hidden windows (game must be closed) and writes artifacts/tests/suite-summary.txt.
param([int]$Players = 4, [switch]$SkipSoak, [switch]$Quick)
$ErrorActionPreference = 'Stop'
$modRoot = Split-Path $PSScriptRoot -Parent
$tours = @('enemies', 'state', 'axe', 'ending', 'clientending', 'takerbail', 'takerbad', 'takergood', 'dollprops', 'cutouts', 'elevator', 'stuck', 'serverlist', 'reconnect', 'pairing', 'furnace', 'puzzles', 'lateending', 'chase', 'latekey', 'latelook', 'loadending', 'stallending')
if (!$SkipSoak -and !$Quick) { $tours += 'soak' }
# Quick: the essential sync checks only (about 15 minutes); the full run adds endurance, lag and every campaign.
if ($Quick) { $tours = @('enemies', 'state', 'axe', 'clientending', 'takerbad', 'cutouts', 'elevator', 'stuck', 'look', 'debug', 'serverlist', 'pairing', 'furnace', 'puzzles', 'lateending', 'chase', 'latekey', 'latelook', 'loadending', 'stallending') }
$lines = @("Spooky Together lab suite, $Players players, $(Get-Date -Format s)")
$failed = 0
foreach ($tour in $tours) {
    $output = & (Join-Path $PSScriptRoot 'run-tour.ps1') -Tour $tour -Players $Players 2>&1 | ForEach-Object { "$_" }
    $lines += $output | Where-Object { $_ -notmatch '^(started|stopped)' }
    if ($output | Where-Object { $_ -eq "${tour}: FAIL" }) { $failed++ }
}
if ($Quick) {
    $lines += "Tours failed: $failed of $($tours.Count)"
    Set-Content (Join-Path $modRoot 'artifacts/tests/suite-summary.txt') $lines
    $lines
    return
}
# Slow-link check: enemies with 150 ms one-way delay plus jitter.
$output = & (Join-Path $PSScriptRoot 'run-tour.ps1') -Tour enemies -Lag 150 -Players $Players 2>&1 | ForEach-Object { "$_" }
$lines += "[lag 150 ms]"
$lines += $output | Where-Object { $_ -notmatch '^(started|stopped)' }
if ($output | Where-Object { $_ -eq 'enemies: FAIL' }) { $failed++ }
$tours += 'enemies-lag150'
# Network path: clients join through this PC's non-loopback address (LAN or VPN) with 120 ms lag plus jitter.
$address = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } | Select-Object -First 1).IPAddress
if ($address) {
    $output = & (Join-Path $PSScriptRoot 'run-tour.ps1') -Tour soak -Address $address -Lag 120 -Players $Players 2>&1 | ForEach-Object { "$_" }
    $lines += "[soak via $address, 120 ms lag]"
    $lines += $output | Where-Object { $_ -notmatch '^(started|stopped)' }
    if ($output | Where-Object { $_ -eq 'soak: FAIL' }) { $failed++ }
    $tours += "soak-via-address"
}
# Every other campaign, driven through real doors (Endless needs the axe and a boarded door).
foreach ($mode in @(@('EM_Mansion', 5), @('KH_Mansion', 5), @('dollhouse', 4))) {
    $output = & (Join-Path $PSScriptRoot 'run-tour.ps1') -Tour mansion -Mansion $mode[0] -MinRooms $mode[1] -Players $Players 2>&1 | ForEach-Object { "$_" }
    $lines += "[$($mode[0])]"
    $lines += $output | Where-Object { $_ -notmatch '^(started|stopped)' }
    if ($output | Where-Object { $_ -eq 'mansion: FAIL' }) { $failed++ }
    $tours += "mansion-$($mode[0])"
}
$lines += "Tours failed: $failed of $($tours.Count)"
$summary = Join-Path $modRoot 'artifacts/tests/suite-summary.txt'
Set-Content $summary $lines
$lines
