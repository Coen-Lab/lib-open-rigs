param(
    # The workflow repository: its folder name is the usedBy tag, and its schemas\trial-columns.yml the columns.
    [Parameter(Mandatory)][string]$Repo,
    # Its own streams (no other workflow writes them).
    [string[]]$OwnStreams = @(),
    # The lab's streams it also writes: tagged too, so none leaves the list while it needs them.
    [string[]]$SharedStreams = @(),
    # The experiment settings it reads.
    [string[]]$Settings = @(),
    # Names it wrote once and no longer does: @{ trialColumns = @(...); streams = @(...) }.
    [hashtable]$Retired = @{},
    [switch]$Apply,
    [string]$ListPath = 'K:\config\data-dictionary.json'
)
# The lab's one copy, run by each workflow's scripts\add-names.ps1 with its own names (from the open-rigs it pins).
# Adds the workflow's trial columns (schemas\trial-columns.yml) and its own streams to the lab's names list
# (K:\config\data-dictionary.json), which rig-control checks before it launches the workflow, and tags each name the
# workflow uses with its repo's name (usedBy; every other repo's tags are kept). Names it no longer writes ($Retired)
# lose its tag, and leave the list when no other repo is tagged on them. Without -Apply it only says what it would
# change. -Apply saves through app-rig-setup's own Save-NamesDictionary, which refuses if the file changed since it
# was read, backs it up and logs the change. The list is production config: apply only on Pip's word.
$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path $Repo).Path
$me = Split-Path $Repo -Leaf
$rigSetup = 'C:\Github\Coen-Lab\app-rig-setup'
. "$rigSetup\engine\detect.ps1"
. "$rigSetup\engine\device-names.ps1"
$path = $ListPath  # another copy only to try the script
$was = [IO.File]::ReadAllText($path)
$prev = $was | ConvertFrom-Json
$read = "import json,yaml;d=yaml.safe_load(open(r'$Repo\schemas\trial-columns.yml',encoding='utf-8'));print(json.dumps({k:{'type':v['type'],'description':v['description']} for k,v in d.items()}))"
$cols = uv run --project $Repo --with pyyaml python -c $read | ConvertFrom-Json
if (-not $cols) { throw "could not read $Repo\schemas\trial-columns.yml" }
$tc = [ordered]@{}
foreach ($p in $prev.trialColumns.PSObject.Properties) { $tc[$p.Name] = $p.Value }
$added = @()
foreach ($p in $cols.PSObject.Properties) {
    if ($tc.Contains($p.Name)) {
        if ($tc[$p.Name].type -ne $p.Value.type) { throw "column $($p.Name) is $($tc[$p.Name].type) on the list, $($p.Value.type) here" }
        continue
    }
    $tc[$p.Name] = [ordered]@{ type = $p.Value.type; description = $p.Value.description }
    $added += "column $($p.Name)"
}
# The lab-wide meaning of the laser columns every optogenetics workflow shares (audit, 10 Nov 2026).
$labDescriptions = [ordered]@{
    laserCondition = 'The Zapit condition (stimLocations number, from 1) sent to Zapit this trial, 0 when none was sent. Whether the laser was on is laserPowerMw > 0; each workflow''s trial-columns.yml says when a condition goes with the laser off.'
    laserPowerMw   = 'Laser power sent to Zapit for this trial (the task''s optogenetics.laserPowerMw), as commanded, not measured. 0 on laser-off trials and without optogenetics.'
}
$described = @()
foreach ($n in $labDescriptions.Keys) {
    if ($tc.Contains($n) -and $tc[$n].description -cne $labDescriptions[$n]) { $tc[$n].description = $labDescriptions[$n]; $described += $n }
}
$streams = @($prev.streams)
foreach ($s in $OwnStreams) { if ($streams -notcontains $s) { $streams += $s; $added += "stream $s" } }
$es = [ordered]@{}; foreach ($p in $prev.experimentSettings.PSObject.Properties) { $es[$p.Name] = $p.Value }
# usedBy as it was, with this repo on each of its names.
$usedBy = [ordered]@{}
foreach ($section in 'streams', 'experimentSettings', 'trialColumns') {
    $usedBy[$section] = [ordered]@{}
    $old = Get-NamesMember (Get-NamesMember $prev 'usedBy') $section
    if ($old) { foreach ($p in $old.PSObject.Properties) { $usedBy[$section][$p.Name] = @($p.Value) } }
}
$tagged = @()
function Add-Tag([string]$Section, [string]$Name) {
    $tags = @($usedBy[$Section][$Name] | Where-Object { $_ })
    if ($tags -notcontains $me) { $usedBy[$Section][$Name] = @(@($tags) + $me | Sort-Object); $script:tagged += $Name }
}
foreach ($name in $cols.PSObject.Properties.Name) { Add-Tag 'trialColumns' $name }
foreach ($s in $OwnStreams) { Add-Tag 'streams' $s }
foreach ($s in $SharedStreams) { if ($streams -notcontains $s) { throw "stream $s is not on the list" }; Add-Tag 'streams' $s }
foreach ($k in $Settings) { if (-not $es.Contains($k)) { throw "setting $k is not on the list" }; Add-Tag 'experimentSettings' $k }
$removed = @()
foreach ($section in $Retired.Keys) {
    foreach ($name in $Retired[$section]) {
        if (-not $usedBy[$section].Contains($name)) { $others = @() } else { $others = @($usedBy[$section][$name] | Where-Object { $_ -and $_ -ne $me }) }
        if ($others) {
            if (@($usedBy[$section][$name]) -contains $me) { $usedBy[$section][$name] = $others; $removed += "$me tag on $name" }
            continue
        }
        if ($usedBy[$section].Contains($name)) { $usedBy[$section].Remove($name) }
        if ($section -eq 'trialColumns' -and $tc.Contains($name)) { $tc.Remove($name); $removed += "column $name" }
        if ($section -eq 'streams' -and $streams -contains $name) { $streams = @($streams | Where-Object { $_ -ne $name }); $removed += "stream $name" }
    }
}
$body = [ordered]@{ streams = $streams; experimentSettings = $es; trialColumns = $tc; usedBy = $usedBy }
"would add $($added.Count): $($added -join ', ')"
"would tag $($tagged.Count) with ${me}: $($tagged -join ', ')"
"would remove $($removed.Count): $($removed -join ', ')"
"would describe $($described.Count): $($described -join ', ')"
if ($Apply -and ($added -or $tagged -or $removed -or $described)) {
    $what = @($(if ($added) { "added $($added -join ', ')" }), $(if ($tagged) { "tagged $($tagged -join ', ') with $me" }),
              $(if ($removed) { "removed $($removed -join ', ')" }), $(if ($described) { "described $($described -join ', ')" })) -join '; '
    $r = Save-NamesDictionary -Path $path -Body $body -Previous $prev -Was $was -By 'PC' -What "$what ($me)"
    "saved: $($r.Ok) $($r.Detail)"
}
