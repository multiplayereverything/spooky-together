# Transport checks without the game: direct LAN join, then the global relay (mod-relay/relay.py).
#   ./scripts/test-relay.ps1        (set MOD_RELAY to use another relay.py)
$ErrorActionPreference = 'Stop'
$modRoot = Split-Path $PSScriptRoot -Parent
$output = Join-Path $modRoot 'artifacts/tests'
New-Item -ItemType Directory -Force -Path $output | Out-Null
if (-not $env:MOD_RELAY) { $env:MOD_RELAY = (Join-Path $HOME 'Desktop\AIStuff\global-mods-server\relay.py') }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /target:exe /langversion:5 "/out:$output/RelayTransportTests.exe" (Join-Path $modRoot 'src/Transport.cs') (Join-Path $modRoot 'src/RelayLink.cs') (Join-Path $modRoot 'tests/RelayTransportTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
& "$output/RelayTransportTests.exe" "$output/relay-transport-results.json"
if ($LASTEXITCODE -ne 0) { throw 'Transport tests failed.' }
