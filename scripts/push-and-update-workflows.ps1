# Use instead of git push here. Pushes, then runs update-workflows.ps1, so every workflow is on
# the packages here (or named as failing) while whoever pushed is still looking. A run where
# every workflow is already up to date only compares files and takes seconds; the checks run
# only for a workflow that changes. Exits with update-workflows.ps1's exit code.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

# The lab's packages and their source (scripts\lab-packages.ps1, shared with build-package.ps1).
. (Join-Path $PSScriptRoot 'lab-packages.ps1')
$lab = Get-LabPackages $repo

# What is published is local_packages\, so a package whose <Version> was raised and never built
# would push and report every workflow up to date (round 6, 0a90299). Each package's version
# must be the .nupkg there.
$unbuilt = @($lab | Where-Object { -not (Test-Path (Join-Path $repo $_.Nupkg)) } | ForEach-Object { $_.Name })
if ($unbuilt) { throw "Not built: $($unbuilt -join ', '). Run scripts\build-package.ps1 -Package <name> and commit local_packages\ first." }

# And the reverse: source changed after its .nupkg was committed, with <Version> left as it was,
# would push and ship nothing (audit-rigs set C). A comment-only change is not a change.
$stale = @($lab | ForEach-Object {
    $inputs = $_.Inputs
    $built = git -C $repo log -1 --format=%H -- $_.Nupkg
    if ($built) {
        & git -C $repo diff --quiet @script:CommentOnly $built HEAD -- @inputs
        if ($LASTEXITCODE -ne 0) {
            $changed = git -C $repo log -1 --format=%H -- @inputs
            "$($_.Name) (source changed in $("$changed".Substring(0, 7)))"
        }
    } })
if ($stale) { throw "Changed since built: $($stale -join ', '). Raise <Version>, run scripts\build-package.ps1 -Package <name> and commit local_packages\ first." }

# Two sessions raised one package to the same version with other contents, and a rebase kept a
# .nupkg built on source older than the commit it sat in; the checks above, reading only this
# clone's commits, passed it (audit-rigs set C, 6d0b870). So origin is fetched and compared too.
& { $ErrorActionPreference = 'Continue'; & git -C $repo fetch -q 2>&1 | ForEach-Object { "$_" } }
if ($LASTEXITCODE -ne 0) { throw "git fetch failed." }
# NuGet caches by id and version, so a version origin already holds must not change.
$taken = @(git -C $repo log --format= --name-only --diff-filter=AM '@{u}..HEAD' -- 'local_packages/*.nupkg' | Where-Object { $_ } | Sort-Object -Unique |
    Where-Object { git -C $repo log '@{u}' -1 --format=%h -- $_ } | ForEach-Object { Split-Path $_ -Leaf })
if ($taken) { throw "Already on origin with other contents: $($taken -join ', '). Raise <Version> and rebuild." }
# Each .nupkg names the commit it was built on, and the source there must be the source in the
# commit it was added onto (its parent), or uncommitted, HEAD's.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$misbuilt = @($lab | ForEach-Object {
    $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $repo $_.Nupkg))
    try {
        $reader = New-Object IO.StreamReader (($zip.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1).Open())
        $commit = ([xml]$reader.ReadToEnd()).package.metadata.repository.commit
        $reader.Dispose()
    } finally { $zip.Dispose() }
    $added = git -C $repo log -1 --format=%H --diff-filter=AM -- $_.Nupkg
    $onto = if ($added) { "$added^" } else { 'HEAD' }
    $inputs = $_.Inputs
    if (-not $commit) { "$($_.Name) (its nuspec names no commit)" }
    elseif (-not (git -C $repo rev-parse -q --verify "$commit^{commit}")) { "$($_.Name) (built on $($commit.Substring(0, 7)), which this clone does not have)" }
    else {
        & git -C $repo diff --quiet @script:CommentOnly $commit $onto -- @inputs
        if ($LASTEXITCODE -ne 0) { "$($_.Name) (built on $($commit.Substring(0, 7)), but its source differs from what it was committed onto)" }
    } })
if ($misbuilt) { throw "Built on other source: $($misbuilt -join ', '). Raise <Version>, rebuild and commit local_packages\ first." }
# update-workflows.ps1 would stop at the second; this stops the push before it.
$twice = @(Get-ChildItem (Join-Path $repo 'local_packages') -Filter '*.nupkg' |
    Group-Object { $_.Name -replace '\.\d+\.\d+\.\d+.*$', '' } | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name })
if ($twice) { throw "Two versions in local_packages\ of: $($twice -join ', '). Keep one (build-package.ps1 removes the old)." }

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
