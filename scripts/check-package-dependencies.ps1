param(
    # A built .nupkg of one of this repo's packages.
    [Parameter(Mandatory)][string]$Nupkg
)
# Fails when the package names a lab package (one under src\) at a version other than that
# package's own <Version>. build-package.ps1 runs it after each pack (audit 2026-11-06: Logging
# coen-lab.4 named Core coen-lab.4 while Core was coen-lab.3).
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $Nupkg).Path)
try {
    $entry = $zip.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
    if (-not $entry) { throw "$Nupkg holds no .nuspec." }
    $reader = New-Object IO.StreamReader($entry.Open())
    try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
} finally { $zip.Dispose() }
$wrong = @()
foreach ($dep in @($nuspec.GetElementsByTagName('dependency'))) {
    $project = Join-Path $repo "src\$($dep.id)\$($dep.id).csproj"
    if (-not (Test-Path $project)) { continue }
    [xml]$csproj = Get-Content $project -Raw
    $want = "$($csproj.Project.PropertyGroup.Version)".Trim()
    if ($dep.version -ne $want) { $wrong += "$($dep.id) $($dep.version) (src\$($dep.id) is $want)" }
}
if ($wrong) { throw "$(Split-Path -Leaf $Nupkg) names $($wrong -join ', ')." }
"  dependencies match src\"
