# Use instead of git push here. Pushes, then runs update-workflows.ps1, so every workflow is on
# the packages here (or named as failing) while whoever pushed is still looking. A run where
# every workflow is already up to date only compares files and takes seconds; the checks run
# only for a workflow that changes. Exits with update-workflows.ps1's exit code.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

# The checks every way of shipping local_packages\ runs (scripts\lab-packages.ps1, shared with
# build-package.ps1 and update-workflows.ps1): built, committed, not stale, not misbuilt, once each.
. (Join-Path $PSScriptRoot 'lab-packages.ps1')
Assert-LabPackagesShippable $repo

# push sends HEAD, but the parse check below reads the files here, so an edit not committed could
# pass it and push without it (audit-rigs set C); update-workflows.ps1 refuses one after the push.
if (git -C $repo status --porcelain -- scripts) { throw "scripts\ has uncommitted changes. Commit or discard them first." }

# Whoever pushes next runs these from origin/main, and a script that does not parse fails there
# (audit-rigs set B): no CI here checks the lab's scripts, so this push does.
$broken = @(Get-ChildItem (Join-Path $repo 'scripts') -Filter '*.ps1' | ForEach-Object {
    $errors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$null, [ref]$errors)
    if ($errors) { "$($_.Name): $($errors[0].Message) (line $($errors[0].Extent.StartLineNumber))" } })
if ($broken) { throw "Does not parse: $($broken -join '; '). Fix it before pushing." }

# Windows PowerShell turns a native command's stderr into errors, so git runs under 'Continue'.
& { $ErrorActionPreference = 'Continue'; & git -C $repo push 2>&1 | ForEach-Object { "$_" } }
if ($LASTEXITCODE -ne 0) { throw "git push failed." }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'update-workflows.ps1')
exit $LASTEXITCODE
