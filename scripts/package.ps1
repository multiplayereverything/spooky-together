# Builds release ZIPs for friends: BepInEx (loader included) and MelonLoader (mod only). No game files are included.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$modRoot = Split-Path $PSScriptRoot -Parent
$version = (Select-String -Path (Join-Path $modRoot 'src/Runtime.cs') -Pattern 'ModVersion = "([^"]+)"').Matches[0].Groups[1].Value
$out = Join-Path $modRoot 'artifacts/release'
New-Item -ItemType Directory -Force $out | Out-Null
$stage = Join-Path $modRoot 'private/package-stage'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }

# BepInEx: extract into the game folder (next to SpookyUnity.exe).
$b = Join-Path $stage 'bepinex'
Copy-Item -Recurse (Join-Path $modRoot 'deps/BepInEx-5.4.23.5') $b
New-Item -ItemType Directory -Force (Join-Path $b 'BepInEx/plugins/SpookyTogether') | Out-Null
Copy-Item (Join-Path $modRoot 'artifacts/BepInEx/SpookyTogether.BepInEx.dll') (Join-Path $b 'BepInEx/plugins/SpookyTogether/')
Copy-Item (Join-Path $modRoot 'README.md') (Join-Path $b 'README.md')

# MelonLoader: copy into Mods/ of a game that already has MelonLoader 0.7.x.
$m = Join-Path $stage 'melonloader'
New-Item -ItemType Directory -Force (Join-Path $m 'Mods') | Out-Null
Copy-Item (Join-Path $modRoot 'artifacts/MelonLoader/SpookyTogether.MelonLoader.dll') (Join-Path $m 'Mods/')
Copy-Item (Join-Path $modRoot 'README.md') (Join-Path $m 'README.md')

foreach ($kind in @(@('bepinex', 'BepInEx'), @('melonloader', 'MelonLoader'))) {
    $zip = Join-Path $out "SpookyTogether-$($kind[1])-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    # Not Compress-Archive: Windows PowerShell 5 writes backslashes into entry names, which other unzip tools mangle.
    $root = (Resolve-Path (Join-Path $stage $kind[0])).Path
    $archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
            $name = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name)
        }
    } finally { $archive.Dispose() }
    Write-Output "Packaged $zip"
}
