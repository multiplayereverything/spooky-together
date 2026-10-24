# Runs one scripted lab tour with hidden windows and judges it. Results go to artifacts/tests/<tour>-results.json.
# Usage: run-tour.ps1 -Tour enemies [-Players 4] [-Seconds 120]
param([Parameter(Mandatory)][string]$Tour, [int]$Players = 4, [int]$Seconds = 0, [string]$Mansion = '', [int]$MinRooms = -1, [int]$Lag = 0, [string]$Address = '127.0.0.1', [switch]$Online, [switch]$ModInfo)
$ErrorActionPreference = 'Stop'
$modRoot = Split-Path $PSScriptRoot -Parent
$tours = @{
    'enemies'      = @{ Seconds = 150; Expect = @('enemy.spawned@host','enemy.retarget@host','enemy.hurt.routed@host','enemy.churn.clean@host','enemy.hit.host@host','enemy.hit.local.skipped@anyclient','client.enemy.bound','client.enemy.tracking','client.enemy.cleanup','client.enemy.anim','client.enemy.anim.legacy','enemy.legacy.spawned@host','enemy.hurt.received@anyclient') }
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
Get-ChildItem (Join-Path $modRoot 'private') -Filter 'spooky-together-lab.log' -Recurse -Depth 1 -ErrorAction SilentlyContinue | Remove-Item -Force
$hostArgs = '-st-auto-start -st-auto-door'
$clientArgs = if ($Tour -eq 'serverlist') { '-st-join-lan' } else { '-st-auto-door' }
if ($Tour -in @('soak', 'mansion')) { $hostArgs += ' -st-soak-spawn' }
if ($Tour -in @('soak', 'mansion', 'reconnect', 'takerbail', 'takerbad', 'takergood', 'cutouts')) { $hostArgs += ' -st-god'; $clientArgs += ' -st-god' }
if ($Mansion) { $hostArgs += " -st-mansion $Mansion" }
$tourArg = if ($tour -in @('soak','serverlist','mansion')) { '' } else { $Tour }
if ($Lag -gt 0) { $hostArgs += " -st-lag $Lag"; $clientArgs += " -st-lag $Lag" }
if ($Tour -eq 'allspecs') { $map = @{ 'base' = ''; 'em' = 'EM_Mansion'; 'kh' = 'KH_Mansion'; 'doll' = 'dollhouse' }; $f = if ($Mansion) { ($map.GetEnumerator() | Where-Object { $_.Value -eq $Mansion } | Select-Object -First 1).Key } else { 'base' }; $hostArgs += " -st-specs $f -st-god"; $clientArgs += ' -st-god'; }
if ($Tour -in @('elevator', 'stuck', 'look', 'spec6', 'debug')) { $hostArgs += ' -st-god'; $clientArgs += ' -st-god' }
if ($Tour -eq 'dollprops') { $hostArgs += ' -st-mansion dollhouse -st-god'; $clientArgs += ' -st-god' }
if ($Mansion) { $out = Join-Path $modRoot "artifacts/tests/$Tour-$Mansion-results.json" }
if ($Address -ne '127.0.0.1') { $out = Join-Path $modRoot ("artifacts/tests/$Tour-via-$Address" + $(if ($Lag -gt 0) { "-lag$Lag" } else { '' }) + "-results.json") }
elseif ($Lag -gt 0) { $out = Join-Path $modRoot ("artifacts/tests/$Tour" + $(if ($Mansion) { "-$Mansion" } else { '' }) + "-lag$Lag-results.json") }
$relayProcess = $null
if ($Online) {
    # Everyone plays through the global relay (mod-relay/relay.py) on a lab port: the host opens a public
    # online room and each client finds it in the online list instead of using an address.
    if ($Tour -eq 'serverlist') { throw 'The serverlist tour is LAN discovery; use another tour with -Online.' }
    $relayPort = 7794
    if (Get-NetTCPConnection -LocalPort $relayPort -State Listen -ErrorAction SilentlyContinue) { throw "Port $relayPort is in use: the lab won't share it." }
    $relayScript = if ($env:MOD_RELAY) { $env:MOD_RELAY } else { (Join-Path $HOME 'Desktop\AIStuff\global-mods-server\relay.py') }
    $relayProcess = Start-Process -FilePath 'python' -ArgumentList @('-u',('"'+$relayScript+'"'),'--port',$relayPort,'--log') -RedirectStandardOutput (Join-Path $modRoot 'private/relay-lab.log') -RedirectStandardError (Join-Path $modRoot 'private/relay-lab-err.log') -WindowStyle Hidden -PassThru
    $serverArg = " -st-relay 127.0.0.1:$relayPort"
    $infoProcess = $null
    if ($ModInfo) {
        # -ModInfo: no -st-relay; every copy learns the server from a local mods-info
        # (github.com/multiplayereverything/mods-info) on port 7799.
        $infoPort = 7799
        if (Get-NetTCPConnection -LocalPort $infoPort -State Listen -ErrorAction SilentlyContinue) { throw "Port $infoPort is in use: the lab won't share it." }
        $infoRoot = Join-Path $modRoot 'private/modinfo-lab'
        New-Item -ItemType Directory -Force (Join-Path $infoRoot 'mods') | Out-Null
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        [IO.File]::WriteAllText((Join-Path $infoRoot 'servers.json'), "{`"schema`":1,`"servers`":[{`"id`":`"main`",`"name`":`"Lab relay`",`"address`":`"127.0.0.1:$relayPort`",`"status`":`"online`"}]}", $utf8)
        [IO.File]::WriteAllText((Join-Path $infoRoot 'mods/spooky.json'), "{`"schema`":1,`"id`":`"spooky`",`"name`":`"Spooky Together`",`"game`":`"Spooky's Jump Scare Mansion`",`"engine`":`"Unity`",`"status`":`"testing`",`"latest_version`":`"99.0`",`"min_version`":`"0.1`",`"protocol`":`"lab`",`"download_url`":`"`",`"changelog_url`":`"`",`"relay`":{`"mode`":`"tunnel`",`"max_players`":4,`"server`":`"main`"},`"notice`":`"`",`"updated`":`"2026-10-24`"}", $utf8)
        $infoProcess = Start-Process -FilePath 'python' -ArgumentList @('-m','http.server',$infoPort,'--bind','127.0.0.1','--directory',('"'+$infoRoot+'"')) -RedirectStandardOutput (Join-Path $modRoot 'private/modinfo-lab.log') -RedirectStandardError (Join-Path $modRoot 'private/modinfo-lab-err.log') -WindowStyle Hidden -PassThru
        $serverArg = " -st-online -st-modinfo http://127.0.0.1:$infoPort/"
        # A saved Relay= from earlier labs would win over mods-info: clear it in every lab copy.
        Get-ChildItem (Join-Path $modRoot 'private') -Filter 'SpookyTogether.ini' -Recurse -Depth 3 -ErrorAction SilentlyContinue | ForEach-Object {
            (Get-Content -LiteralPath $_.FullName) | Where-Object { $_ -notmatch '^Relay=' } | Set-Content -LiteralPath $_.FullName
        }
        $spec.Expect = @($spec.Expect) + @('online.modinfo@host', 'client.online.modinfo')
    }
    $hostArgs += $serverArg
    $clientArgs += "$serverArg -st-join-online"
    $spec.Expect = @($spec.Expect) + @('online.room@host', 'client.online.found')
    $out = Join-Path $modRoot ("artifacts/tests/$Tour-online" + $(if ($ModInfo) { '-modinfo' } else { '' }) + "-results.json")
}
# Endurance runs (soak, campaigns) last their full time; every other tour stops as soon as all its checks have passed.
$early = $spec.Expect.Count -gt 0 -and $Tour -notin @('soak', 'mansion') -and !$spec.MinRooms
if (!$early) {
    & (Join-Path $PSScriptRoot 'launch-lab.ps1') -Players $Players -Seconds $Seconds -HostArgs $hostArgs -ClientArgs $clientArgs -Tour $tourArg -JoinAddress $Address
} else {
    & (Join-Path $PSScriptRoot 'launch-lab.ps1') -Players $Players -HostArgs $hostArgs -ClientArgs $clientArgs -Tour $tourArg -JoinAddress $Address
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
$keep = Join-Path $modRoot ("artifacts/tests/logs/" + $(if ($Mansion) { "$Tour-$Mansion" } else { $Tour }) + $(if ($Lag -gt 0) { "-lag$Lag" } else { '' }))
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
if ($relayProcess) { Stop-Process -Id $relayProcess.Id -Force -ErrorAction SilentlyContinue }
if ($infoProcess) { Stop-Process -Id $infoProcess.Id -Force -ErrorAction SilentlyContinue }
