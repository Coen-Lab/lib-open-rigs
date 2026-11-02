# Use instead of git push here. Pushes, then runs update-workflows.ps1, so every workflow is on
# the packages here (or named as failing) while whoever pushed is still looking. A run where
# every workflow is already up to date only compares files and takes seconds; the checks run
# only for a workflow that changes. Exits with update-workflows.ps1's exit code.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

# What is published is local_packages\, so a package whose <Version> was raised and never built
# would push and report every workflow up to date (round 6, 0a90299). Each package's version
# must be the .nupkg there.
$unbuilt = @(Get-ChildItem (Join-Path $repo 'src') -Directory | ForEach-Object {
    $project = Join-Path $_.FullName "$($_.Name).csproj"
    if (Test-Path $project) {
        $version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
        # Only the lab's packages carry a <Version>; open-rigs' own are versioned when packed.
        if ($version -and -not (Test-Path (Join-Path $repo "local_packages\$($_.Name).$version.nupkg"))) { "$($_.Name) $version" }
    } })
if ($unbuilt) { throw "Not built: $($unbuilt -join ', '). Run scripts\build-package.ps1 -Package <name> and commit local_packages\ first." }

# Windows PowerShell turns a native command's stderr into errors, so git runs under 'Continue'.
& { $ErrorActionPreference = 'Continue'; & git -C $repo push 2>&1 | ForEach-Object { "$_" } }
if ($LASTEXITCODE -ne 0) { throw "git push failed." }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'update-workflows.ps1')
exit $LASTEXITCODE
