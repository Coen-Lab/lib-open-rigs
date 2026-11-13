param(
    # The package to build: a folder under src\ holding <Package>.csproj, such as OpenRigs.LiveView.
    [Parameter(Mandatory)][string]$Package,
    # A Bonsai environment root (a folder with .bonsai\Bonsai.config and local_packages\) to
    # install the package into for the first time. Workflows that already have the package get
    # the new version from rig-setup's update-workflows.ps1.
    [string]$Install = ''
)
# Builds one package into local_packages\, replacing the version there before, so the folder
# holds one version of each package. With -Install, also puts it into another environment
# (scripts\install-package.ps1).
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$project = Join-Path $repo "src\$Package\$Package.csproj"
if (-not (Test-Path $project)) { throw "No package ${Package}: src\$Package\$Package.csproj does not exist." }
$out = Join-Path $repo 'local_packages'
[xml]$csproj = Get-Content $project -Raw
# The package id is the project's name, as in open-rigs (no PackageId in the .csproj).
$id = $Package
$version = $csproj.Project.PropertyGroup.Version
$name = "$id.$version.nupkg"

# NuGet caches a package by id and version, on this machine and on every rig, so a rebuilt
# package under a version already committed would be ignored wherever the old one is cached.
# (git's "did not match" on stderr would stop this script under 'Stop', so it runs under 'Continue'.)
# Committed at any time, not only now: a version set back by hand names a file an older commit
# held and a rig may have cached (audit-rigs set C).
$was = & git -C $repo log --all -1 --format=%h -- "local_packages/$name"
if ($was) { throw "$name was committed before ($was). Raise <Version> in src\$Package\$Package.csproj before building." }

# dotnet pack builds what is on disk, while the nuspec names HEAD as the commit it was built on:
# packed over uncommitted source, the package matches no commit, and every push check passes it
# (audit-rigs set C, 6 Nov 2026). So the package's source is committed first, then built.
. (Join-Path $PSScriptRoot 'lab-packages.ps1')
$inputs = (Get-LabPackages $repo | Where-Object { $_.Id -eq $id }).Inputs
$dirty = @(& git -C $repo status --porcelain -- @inputs)
if ($dirty) { throw "src\$Package has uncommitted changes ($($dirty.Count) file(s)). Commit them first, so the package names the commit it was built from." }

"Packing $id $version..."
# Each package keeps its own .csproj <Version> (build\Common.csproj.targets). No -p:Version: a global
# property reaches the ProjectReferences too, so each dependency named the packed package's version
# (Logging coen-lab.4 wanted Core coen-lab.4 while Core was coen-lab.3; audit 2026-11-06).
& dotnet pack $project -c Release -nologo -v q -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed" }
$nupkg = Join-Path $out $name
if (-not (Test-Path $nupkg)) { throw "no $nupkg after pack" }
try { & (Join-Path $PSScriptRoot 'check-package-dependencies.ps1') -Nupkg $nupkg }
catch { Remove-Item $nupkg; throw "$_ Nothing was kept." }
Get-ChildItem $out -File -Filter '*.nupkg' | Where-Object { $_.Name -match "^$([regex]::Escape($id))\.\d" -and $_.Name -ne $name } |
    ForEach-Object { Remove-Item $_.FullName; "  removed $($_.Name)" }
"  $nupkg"
if (-not $Install) { exit 0 }

& (Join-Path $PSScriptRoot 'install-package.ps1') -Nupkg $nupkg -Environment $Install
