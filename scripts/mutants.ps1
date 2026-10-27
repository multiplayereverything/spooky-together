# Mutation check: turns one fix off at a time (-st-mutate, lab-only) and runs the tour that guards it.
# Each tour must FAIL with its fix off; a tour that still passes does not test that fix. Writes artifacts/tests/mutants/summary.txt.
param([int]$Players = 4, [string[]]$Only = @())
$ErrorActionPreference = 'Stop'
$modRoot = Split-Path $PSScriptRoot -Parent
# Fix name (Mutant("...") in src) -> the tour whose checks must catch it.
$mutants = [ordered]@{
    'retarget'    = 'enemies'   # specimens chase the nearest standing player
    'hurtrouting' = 'enemies'   # a specimen's hit lands on the player it chases
    'cachedplayer' = 'chase'    # Specimen 8 (it keeps the player before steering) heads for the nearest teammate
    'keys'        = 'state'     # run keys reach every player
    'hits'        = 'axe'       # weapon hits reach every copy
    'elevator'    = 'elevator'  # a teammate's elevator press starts the ride for everyone
    'stalelocks'  = 'stuck'     # locks left by a room a teammate's door unloaded are released
    'pairing'     = 'pairing'   # a client's own spawn becomes the host's specimen instead of being removed
    'puzzlereplay' = 'puzzles'  # a replayed puzzle segment writes nothing, so its AddKey counts once
    'endingrescue' = 'lateending' # an ending missed outside the run is applied later, and a stranded client leaves
    'keyorder'    = 'latekey'   # key changes newer than a pending snapshot are replayed on top of it, not wiped by it
    'lookretry'   = 'latelook'  # a client that painted before a re-skinning specimen's materials loaded repaints once they do
    'earlypuppet' = 'latelook'  # a late joiner spawns host specimens only once its room is ready, so no missing-door errors
    'endingload'  = 'loadending' # an ending that arrives mid room load waits for the load instead of being lost
    'endingstall' = 'stallending' # a held ending with no room load to wait for still goes after 3 s
    'furnace'     = 'furnace'   # furnace actions re-read the shared state and run on the host, so one key results
}
$lines = @("Spooky Together mutation check, $Players players, $(Get-Date -Format s)")
$survived = 0; $ran = 0
foreach ($entry in $mutants.GetEnumerator()) {
    $name = $entry.Key; $tour = $entry.Value
    if ($Only.Count -gt 0 -and $name -notin $Only) { continue }
    $output = & (Join-Path $PSScriptRoot 'run-tour.ps1') -Tour $tour -Players $Players -Mutate $name 2>&1 | ForEach-Object { "$_" }
    $ran++
    $failed = [bool]($output | Where-Object { $_ -eq "${tour}: FAIL" })
    if (!$failed) { $survived++ }
    $lines += ("{0} ({1}): {2}" -f $name, $tour, $(if ($failed) { 'caught' } else { 'SURVIVED' }))
    $lines += $output | Where-Object { $_ -match '^\s+- ' } | Select-Object -First 4
}
$lines += "Mutants survived: $survived of $ran"
$dir = Join-Path $modRoot 'artifacts/tests/mutants'
New-Item -ItemType Directory -Force $dir | Out-Null
Set-Content (Join-Path $dir 'summary.txt') $lines
$lines
