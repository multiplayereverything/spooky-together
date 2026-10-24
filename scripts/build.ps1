param([ValidateSet('All','BepInEx','MelonLoader')][string]$Loader = 'All', [string]$OutDir)
$ErrorActionPreference = 'Stop'
$modRoot = Split-Path $PSScriptRoot -Parent
$gameRoot = Split-Path $modRoot -Parent
if (!(Test-Path -LiteralPath (Join-Path $gameRoot 'SpookyUnity.exe'))) { throw 'SpookyUnity.exe was not found next to MultiplayerMod.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework C# compiler is required.' }
$managed = Join-Path $gameRoot 'SpookyUnity_Data/Managed'
$common = @('Transport.cs','RelayLink.cs','ModInfo.cs','OnlineRelay.cs','Runtime.cs','Net.cs','Rooms.cs','Avatars.cs','Downed.cs','LanDiscovery.cs','Enemies.cs','SharedState.cs','Combat.cs','Endings.cs','PropRefresh.cs','SpecimenAudio.cs','Cutouts.cs','StaleLocks.cs','RoomLook.cs','DebugMenu.cs','Lab.cs','Tours.cs','Patches.cs') | ForEach-Object { Join-Path $modRoot "src/$_" }
$references = @((Join-Path $managed 'Assembly-CSharp.dll'), (Join-Path $managed 'Rewired_Core.dll')) + @(Get-ChildItem -LiteralPath $managed -Filter 'UnityEngine*.dll' | ForEach-Object FullName)
$netstandard = Join-Path $managed 'netstandard.dll'
if (Test-Path -LiteralPath $netstandard) { $references += $netstandard }
foreach ($target in @('BepInEx','MelonLoader')) {
    if ($Loader -ne 'All' -and $Loader -ne $target) { continue }
    $destination = if ($OutDir) { Join-Path $OutDir $target } else { Join-Path $modRoot "artifacts/$target" }
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    if ($target -eq 'BepInEx') {
        $core = Join-Path $modRoot 'deps/BepInEx-5.4.23.5/BepInEx/core'
        $extra = @((Join-Path $core 'BepInEx.dll'), (Join-Path $core '0Harmony.dll'))
        $entry = Join-Path $modRoot 'src/BepInExEntry.cs'
    } else {
        $core = Join-Path $modRoot 'deps/MelonLoader-0.7.3/MelonLoader/net472'
        $extra = @((Join-Path $core 'MelonLoader.dll'), (Join-Path $core '0Harmony.dll'))
        $entry = Join-Path $modRoot 'src/MelonEntry.cs'
    }
    foreach ($reference in ($references + $extra)) { if (!(Test-Path -LiteralPath $reference)) { throw "Missing reference: $reference" } }
    $arguments = @('/nologo','/target:library','/optimize+','/langversion:5','/nowarn:1701,1702',"/out:$destination/SpookyTogether.$target.dll")
    $arguments += ($references + $extra | ForEach-Object { "/reference:$_" })
    & $compiler @arguments @common $entry
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $target" }
    Write-Output "Built $target -> $destination"
}
