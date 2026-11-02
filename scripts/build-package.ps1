param(
    # The package to build: a folder under src\ holding <Package>.csproj, such as OpenRigs.LiveView.
    [Parameter(Mandatory)][string]$Package,
    # A Bonsai environment root (a folder with .bonsai\Bonsai.config and local_packages\) to
    # install the package into for the first time. Workflows that already have the package get
    # the new version from scripts\update-workflows.ps1.
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
& { $ErrorActionPreference = 'Continue'; & git -C $repo ls-files --error-unmatch "local_packages/$name" 2>&1 | Out-Null }
if ($LASTEXITCODE -eq 0) { throw "$name is already committed. Raise <Version> in src\$Package\$Package.csproj before building." }

"Packing $id $version..."
# open-rigs' build stamps 42.42.42-dev0 on any package not given its version on the command line.
& dotnet pack $project -c Release -nologo -v q -o $out -p:Version=$version
if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed" }
$nupkg = Join-Path $out $name
if (-not (Test-Path $nupkg)) { throw "no $nupkg after pack" }
Get-ChildItem $out -File -Filter '*.nupkg' | Where-Object { $_.Name -match "^$([regex]::Escape($id))\.\d" -and $_.Name -ne $name } |
    ForEach-Object { Remove-Item $_.FullName; "  removed $($_.Name)" }
"  $nupkg"
if (-not $Install) { exit 0 }

& (Join-Path $PSScriptRoot 'install-package.ps1') -Nupkg $nupkg -Environment $Install
