# Transport checks without the game: direct LAN join, a full game, a bad frame.
#   ./scripts/test-transport.ps1
# tests/SteamStub.cs stands in for src/SteamNet.cs; the Steam path needs the game and Steam (run-tour.ps1 -Steam).
$ErrorActionPreference = 'Stop'
$modRoot = Split-Path $PSScriptRoot -Parent
$output = Join-Path $modRoot 'artifacts/tests'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /target:exe /langversion:5 "/out:$output/TransportTests.exe" (Join-Path $modRoot 'src/Transport.cs') (Join-Path $modRoot 'tests/SteamStub.cs') (Join-Path $modRoot 'tests/TransportTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
& "$output/TransportTests.exe" "$output/transport-results.json"
if ($LASTEXITCODE -ne 0) { throw 'Transport tests failed.' }
