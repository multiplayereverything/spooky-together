# Runs one scripted lab tour and judges it. The copies start minimized and small (see launch-lab.ps1; -Hidden hides them).
# Results go to artifacts/tests/<tour>-results.json.
# Usage: run-tour.ps1 -Tour enemies [-Players 4] [-Seconds 120] [-Mutate retarget] [-Hidden] (-Mutate turns fixes off; see mutants.ps1)
param([Parameter(Mandatory)][string]$Tour, [int]$Players = 4, [int]$Seconds = 0, [string]$Mansion = '', [int]$MinRooms = -1, [int]$Lag = 0, [string]$Address = '127.0.0.1', [switch]$Steam, [string]$Mutate = '', [switch]$Hidden)
$ErrorActionPreference = 'Stop'
$modRoot = Split-Path $PSScriptRoot -Parent
$tours = @{
    'enemies'      = @{ Seconds = 150; Expect = @('enemy.spawned@host','enemy.retarget@host','enemy.hurt.routed@host','enemy.churn.clean@host','enemy.hit.host@host','enemy.hit.local.skipped@anyclient','client.enemy.bound','client.enemy.tracking','client.enemy.cleanup','client.enemy.anim','client.enemy.anim.legacy','enemy.legacy.spawned@host','enemy.hurt.received@anyclient','retarget.cached.Spec6Controller@host','retarget.cached.Spec8Controller@host','retarget.cached.HookedDollControl@host','retarget.cached.FrenzyControl@host','retarget.cached.NightmareController@host') }
    'state'        = @{ Seconds = 130;  Expect = @('state.keys','state.items','state.remove','state.violence','props.refresh') }
    'axe'          = @{ Seconds = 110;  Expect = @('axe.granted@host','axe.hit.relayed','client.axe.host.armed') }
    'ending'       = @{ Seconds = 100;  Expect = @('ending.Credits') }
    'clientending' = @{ Seconds = 100;  Expect = @('ending.Credits') }
    'takerbail'    = @{ Seconds = 140; Expect = @('ending.Credits') }
    'takerbad'     = @{ Seconds = 200; Expect = @('ending.BadEnd') }
    'takergood'    = @{ Seconds = 220; Expect = @('ending.GoodEnd') }
    'allspecs'     = @{ Seconds = 900; Expect = @('specs.listed@host', 'specs.done@host') }
    'elevator'     = @{ Seconds = 200; Expect = @('elevator.ride@anyclient') }
    'debug'        = @{ Seconds = 150; Expect = @('debug.locks', 'debug.tabs', 'debug.travel', 'client.debug.spawn.bound', 'debug.report@host') }
    'spec6'        = @{ Seconds = 150; Expect = @('client.spec6.screen') }
    'look'         = @{ Seconds = 150; Expect = @(); MinRooms = 5 }
    'pairing'      = @{ Seconds = 200; Expect = @('pairing.host@host','client.pairing.adopted','client.pairing.replaced','client.pairing.single') }
    'furnace'      = @{ Seconds = 420; Expect = @('furnace.onekey','furnace.state') }
    'puzzles'      = @{ Seconds = 150; Expect = @('puzzle.once') }
    'lateending'   = @{ Seconds = 140; Expect = @('ending.Credits@host','ending.Credits@c1','ending.Credits@c2','ending.stranded.left@c3') }
    'latekey'      = @{ Seconds = 120; Expect = @('client.latekey.kept') }
    'latelook'     = @{ Seconds = 150; Expect = @('client.room.look', 'puppet.roomready@c3') }
    'loadending'   = @{ Seconds = 140; Expect = @('ending.Credits') }
    'stallending'  = @{ Seconds = 120; Expect = @('ending.Credits', 'stall.prompt@c1') }
    'chase'        = @{ Seconds = 150; Expect = @('chase.target@host') }
    'stuck'        = @{ Seconds = 150; Expect = @('locks.released@anyclient') }
    'cutouts'      = @{ Seconds = 420; Expect = @('client.cutout.side','client.cutout.look') }
    'dollprops'    = @{ Seconds = 260; Expect = @('client.props.real') }
    'reconnect'    = @{ Seconds = 220; Expect = @('reconnect.peers@host','reconnect.avatars@host','reconnect.rejoined@anyclient') }
    'mansion'      = @{ Seconds = 240; Expect = @(); MinRooms = 8 }
    'soak'         = @{ Seconds = 600; Expect = @(); MinRooms = 25 }
    'serverlist'   = @{ Seconds = 75;  Expect = @('client.serverlist.found'); MinRooms = 2 }
}
if (!$tours.ContainsKey($Tour)) { throw "Unknown tour $Tour" }
$labRoot = (Join-Path $modRoot 'private')
$busy = Get-CimInstance Win32_Process -Filter "Name='SpookyUnity.exe'" | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($labRoot, [StringComparison]::OrdinalIgnoreCase) }
if ($busy) {
    # Only copies under private/ (lab-owned, never the real game) are touched: give them time to exit, then stop them.
    $busy | ForEach-Object { Wait-Process -Id $_.ProcessId -Timeout 30 -ErrorAction SilentlyContinue }
    $busy = Get-CimInstance Win32_Process -Filter "Name='SpookyUnity.exe'" | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($labRoot, [StringComparison]::OrdinalIgnoreCase) }
    $busy | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; Wait-Process -Id $_.ProcessId -Timeout 15 -ErrorAction SilentlyContinue }
}
$spec = $tours[$Tour]
if ($Seconds -le 0) { $Seconds = $spec.Seconds }
# A copy stopped by the previous tour can hold its log for a few seconds while it exits (longer on a busy PC).
foreach ($old in @(Get-ChildItem (Join-Path $modRoot 'private') -Filter 'spooky-together-lab.log' -Recurse -Depth 1 -ErrorAction SilentlyContinue)) {
    for ($try = 0; $try -lt 30; $try++) {
        try { Remove-Item -LiteralPath $old.FullName -Force -ErrorAction Stop; break }
        catch { if ($try -eq 29) { throw "The lab log $($old.FullName) is still in use after 30 s." }; Start-Sleep 1 }
    }
}
$hostArgs = '-st-auto-start -st-auto-door'
$clientArgs = if ($Tour -eq 'serverlist') { '-st-join-lan' } else { '-st-auto-door' }
if ($Tour -in @('soak', 'mansion')) { $hostArgs += ' -st-soak-spawn' }
if ($Tour -in @('soak', 'mansion', 'reconnect', 'takerbail', 'takerbad', 'takergood', 'cutouts')) { $hostArgs += ' -st-god'; $clientArgs += ' -st-god' }
if ($Tour -eq 'latelook' -and !$Mansion) { $Mansion = 'EM_Mansion' }   # its unlit walls take EMKHMonster5's CowWallMat
if ($Mansion) { $hostArgs += " -st-mansion $Mansion" }
$tourArg = if ($tour -in @('soak','serverlist','mansion')) { '' } else { $Tour }
if ($Lag -gt 0) { $hostArgs += " -st-lag $Lag"; $clientArgs += " -st-lag $Lag" }
if ($Tour -eq 'allspecs') { $map = @{ 'base' = ''; 'em' = 'EM_Mansion'; 'kh' = 'KH_Mansion'; 'doll' = 'dollhouse' }; $f = if ($Mansion) { ($map.GetEnumerator() | Where-Object { $_.Value -eq $Mansion } | Select-Object -First 1).Key } else { 'base' }; $hostArgs += " -st-specs $f -st-god"; $clientArgs += ' -st-god'; }
if ($Tour -in @('elevator', 'stuck', 'look', 'spec6', 'debug', 'pairing', 'puzzles', 'chase', 'latelook')) { $hostArgs += ' -st-god'; $clientArgs += ' -st-god' }
if ($Tour -in @('dollprops', 'furnace')) { $hostArgs += ' -st-mansion dollhouse -st-god'; $clientArgs += ' -st-god' }
if ($Mansion) { $out = Join-Path $modRoot "artifacts/tests/$Tour-$Mansion-results.json" }
if ($Address -ne '127.0.0.1') { $out = Join-Path $modRoot ("artifacts/tests/$Tour-via-$Address" + $(if ($Lag -gt 0) { "-lag$Lag" } else { '' }) + "-results.json") }
elseif ($Lag -gt 0) { $out = Join-Path $modRoot ("artifacts/tests/$Tour" + $(if ($Mansion) { "-$Mansion" } else { '' }) + "-lag$Lag-results.json") }
if ($Mutate) { $hostArgs += " -st-mutate $Mutate"; $clientArgs += " -st-mutate $Mutate"; $out = Join-Path $modRoot "artifacts/tests/mutants/$Tour-$($Mutate -replace ',', '+')-results.json" }
if ($Steam) {
    # Everyone finds the host through a real Steam lobby (tagged with this run, so no other game is joined) and talks
    # over Steam Networking Sockets. Lab copies share one Steam account, which cannot reach itself peer to peer, so they
    # connect over 127.0.0.1 through the same Steam sockets; P2P through Valve's relay needs two accounts.
    if ($Tour -eq 'serverlist') { throw 'The serverlist tour is LAN discovery; use another tour with -Steam.' }
    $tag = "lab" + (Get-Random -Maximum 1000000000)
    $hostArgs += " -st-steam -st-steam-lab $tag"
    $clientArgs += " -st-join-steam -st-steam-lab $tag"
    $spec.Expect = @($spec.Expect) + @('steam.lobby@host', 'client.steam.found')
    $out = Join-Path $modRoot "artifacts/tests/$Tour-steam-results.json"
}
# Endurance runs (soak, campaigns) last their full time; every other tour stops as soon as all its checks have passed.
$early = $spec.Expect.Count -gt 0 -and $Tour -notin @('soak', 'mansion') -and !$spec.MinRooms
if (!$early) {
    & (Join-Path $PSScriptRoot 'launch-lab.ps1') -Players $Players -Seconds $Seconds -HostArgs $hostArgs -ClientArgs $clientArgs -Tour $tourArg -JoinAddress $Address -Hidden:$Hidden
} else {
    & (Join-Path $PSScriptRoot 'launch-lab.ps1') -Players $Players -HostArgs $hostArgs -ClientArgs $clientArgs -Tour $tourArg -JoinAddress $Address -Hidden:$Hidden
    $deadline = (Get-Date).AddSeconds($Seconds)
    $probe = Join-Path $modRoot 'private/tour-probe.json'
    $probeArgs = @((Join-Path $modRoot 'tests/verify-lab.py'), $modRoot, '--out', $probe)
    foreach ($e in $spec.Expect) { $probeArgs += @('--expect', $e) }
    do {
        Start-Sleep 5
        python -I @probeArgs | Out-Null
        $probeResult = Get-Content $probe -Raw | ConvertFrom-Json
        # A client still loading into the host's room is not done yet either: stopping then judged it "fell behind".
        $waiting = @($probeResult.failures | Where-Object { $_ -match 'not seen|no client passed|fell behind' }).Count
        $failedCheck = @($probeResult.failures | Where-Object { $_ -match 'check .* failed' }).Count
        if ($Tour -eq 'allspecs') { $failedCheck = 0 }   # a sweep reports every specimen, not just the first failure
    } while (($waiting -gt 0) -and ($failedCheck -eq 0) -and ((Get-Date) -lt $deadline))
    # A few more seconds so error and memory samples cover the end of the tour.
    Start-Sleep 10
    $pids = Get-Content (Join-Path $modRoot 'private/lab-pids.txt') | ForEach-Object { [int]$_ }
    foreach ($id in $pids) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
    foreach ($id in $pids) { Wait-Process -Id $id -Timeout 15 -ErrorAction SilentlyContinue }
    Write-Output 'stopped lab processes'
}
if (!$out) { $out = Join-Path $modRoot "artifacts/tests/$Tour-results.json" }
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$verify = @((Join-Path $modRoot 'tests/verify-lab.py'), $modRoot, '--out', $out)
foreach ($e in $spec.Expect) { $verify += @('--expect', $e) }
$rooms = if ($MinRooms -ge 0) { $MinRooms } else { $spec.MinRooms }
if ($rooms) { $verify += @('--min-rooms', $rooms) }
python -I @verify | Out-Null
# Keep each tour's per-instance evidence; the next tour reuses the lab folders.
$keep = Join-Path $modRoot ("artifacts/tests/logs/" + $(if ($Mansion) { "$Tour-$Mansion" } else { $Tour }) + $(if ($Lag -gt 0) { "-lag$Lag" } else { '' }) + $(if ($Steam) { "-steam" } else { '' }) + $(if ($Mutate) { "-mutant-$($Mutate -replace ',', '+')" } else { '' }))
New-Item -ItemType Directory -Force $keep | Out-Null
foreach ($lab in 'lab-host','lab-client','lab-c2','lab-c3') {
    $src = Join-Path $modRoot "private/$lab/spooky-together-lab.log"
    if (Test-Path $src) { Copy-Item -Force $src (Join-Path $keep "$lab.log") }
}
$result = Get-Content $out -Raw | ConvertFrom-Json
Write-Output ("{0}: {1}" -f $Tour, $(if ($result.pass) { 'PASS' } else { 'FAIL' }))
foreach ($f in $result.failures) { Write-Output "  - $f" }
foreach ($p in $result.instances.PSObject.Properties) {
    $i = $p.Value
    $passed = @($i.checks | Where-Object pass).Count; $total = @($i.checks).Count
    Write-Output ("  {0}: checks {1}/{2}, seq {3}, errors {4}, heap slope {5} MB/10s, objects slope {6}, desync max {7}, mod ms median {8} max {9}, mod objects {10} (slope {11})" -f $p.Name, $passed, $total, $i.max_seq, $i.errors_end, $i.heap_slope_mb_per_10s, $i.objects_slope_per_10s, $i.desync_max, $i.modms_median, $i.modms_max, $i.modobjects_end, $i.modobjects_slope)
}
