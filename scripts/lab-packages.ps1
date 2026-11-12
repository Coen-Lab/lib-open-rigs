# The lab's packages and what each is built from, for build-package.ps1 and
# push-and-update-workflows.ps1 (one list, so the two never disagree). Dot-source it.

# A change whose lines are all comments or blank changes no package, so it neither blocks a push
# nor needs a version (86daefc, comment-only edits to Manifest, blocked every push until the
# check ignored them; audit-rigs set C, 6 Nov 2026).
# One pattern, ending in the line's own newline: git hands -I the line with its newline and lets
# ^ match after it, so '^\s*$' matched the empty end of every line and the checks below passed
# every change (audit-rigs set C, run 4, 8 Nov 2026); and -I with --ignore-blank-lines each
# ignore only a hunk wholly of their own kind, so a comment added with a blank line counted.
# Test-CommentOnlyFilter holds both answers against known commits.
$script:CommentOnly = @('-I', "^[ `t`r]*(//[^`n]*)?`n")

function Test-CommentOnlyFilter([string]$Repo) {
    # Manifest coen-lab.10 to .11 changed code; 86daefc's ExpectedFiles.cs edit changed /// lines only.
    & git -C $Repo diff --quiet @script:CommentOnly 9e4eca7 e0740ec -- src/OpenRigs.Manifest
    $code = $LASTEXITCODE
    & git -C $Repo diff --quiet @script:CommentOnly '86daefc^' 86daefc -- src/OpenRigs.Manifest/ExpectedFiles.cs
    $comment = $LASTEXITCODE
    if ($code -ne 1 -or $comment -ne 0) { throw "The comment-only filter is wrong here (a code change read $code, want 1; a comment change read $comment, want 0): fix `$script:CommentOnly in scripts\lab-packages.ps1." }
}

# A package's inputs are the files it compiles from elsewhere, not their folder: Video compiles
# ..\Shared\RigDevices.cs alone, and src/Shared made a change to any shared file a change to Video (audit, 11 Nov 2026).
function Test-LabPackageInputs([string]$Repo) {
    $video = Get-LabPackages $Repo | Where-Object { $_.Id -eq 'OpenRigs.Video' }
    if (-not $video -or $video.Inputs -notcontains 'src/Shared/RigDevices.cs' -or $video.Inputs -contains 'src/Shared') {
        throw "Get-LabPackages is wrong here (OpenRigs.Video's inputs are $($video.Inputs -join ', '); want src/Shared/RigDevices.cs, not src/Shared): fix it in scripts\lab-packages.ps1."
    }
}

# patches\README.md named .nupkg files local_packages\ no longer held (Devices .4, Video .3), so
# every package it names must be there (audit, 11 Nov 2026).
function Assert-PatchesNamedBuilt([string]$Repo) {
    $text = Get-Content (Join-Path $Repo 'patches\README.md') -Raw
    $gone = @([regex]::Matches($text, '[A-Za-z][\w.]*?\.\d+\.\d+\.\d+[\w.-]*\.nupkg') | ForEach-Object { $_.Value } | Sort-Object -Unique |
        Where-Object { -not (Test-Path (Join-Path $Repo "local_packages\$_")) })
    if ($gone) { throw "patches\README.md names packages local_packages\ does not hold: $($gone -join ', '). Correct it." }
}

# What every way of shipping local_packages\ to the workflows checks first: push-and-update-workflows.ps1
# before its push, and update-workflows.ps1 run on its own (a lone run after a plain git push shipped
# whatever local_packages\ held, unchecked; audit-rigs set C, run 4).
function Assert-LabPackagesShippable([string]$Repo) {
    $lab = Get-LabPackages $Repo
    Test-CommentOnlyFilter $Repo
    Test-LabPackageInputs $Repo
    Assert-PatchesNamedBuilt $Repo

    # What is published is local_packages\, so a package whose <Version> was raised and never built
    # would push and report every workflow up to date (round 6, 0a90299). Each package's version
    # must be the .nupkg there.
    # A build not committed passed every check below (the file is on disk, git names no commit for
    # it), so the raised <Version> went up without its .nupkg (audit, 8 Nov 2026).
    if (git -C $Repo status --porcelain -- local_packages) { throw "local_packages\ has uncommitted changes. Commit them first." }
    $unbuilt = @($lab | Where-Object { -not (Test-Path (Join-Path $Repo $_.Nupkg)) } | ForEach-Object { $_.Name })
    if ($unbuilt) { throw "Not built: $($unbuilt -join ', '). Run scripts\build-package.ps1 -Package <name> and commit local_packages\ first." }

    # And the reverse: source changed after its .nupkg was committed, with <Version> left as it was,
    # would push and ship nothing (audit-rigs set C). A comment-only change is not a change.
    $stale = @($lab | ForEach-Object {
        $inputs = $_.Inputs
        $built = git -C $Repo log -1 --format=%H -- $_.Nupkg
        if ($built) {
            & git -C $Repo diff --quiet @script:CommentOnly $built HEAD -- @inputs
            if ($LASTEXITCODE -ne 0) {
                $changed = git -C $Repo log -1 --format=%H -- @inputs
                "$($_.Name) (source changed in $("$changed".Substring(0, 7)))"
            }
        } })
    if ($stale) { throw "Changed since built: $($stale -join ', '). Raise <Version>, run scripts\build-package.ps1 -Package <name> and commit local_packages\ first." }

    # Two sessions raised one package to the same version with other contents, and a rebase kept a
    # .nupkg built on source older than the commit it sat in; the checks above, reading only this
    # clone's commits, passed it (audit-rigs set C, 6d0b870). So origin is fetched and compared too.
    & { $ErrorActionPreference = 'Continue'; & git -C $Repo fetch -q 2>&1 | ForEach-Object { "$_" } }
    if ($LASTEXITCODE -ne 0) { throw "git fetch failed." }
    # NuGet caches by id and version, so a version origin already holds must not change.
    $taken = @(git -C $Repo log --format= --name-only --diff-filter=AM '@{u}..HEAD' -- 'local_packages/*.nupkg' | Where-Object { $_ } | Sort-Object -Unique |
        Where-Object { git -C $Repo log '@{u}' -1 --format=%h -- $_ } | ForEach-Object { Split-Path $_ -Leaf })
    if ($taken) { throw "Already on origin with other contents: $($taken -join ', '). Raise <Version> and rebuild." }
    # Each .nupkg names the commit it was built on, and the source there must be the source in the
    # commit it was added onto (its parent), or uncommitted, HEAD's.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $misbuilt = @($lab | ForEach-Object {
        $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $Repo $_.Nupkg))
        try {
            $reader = New-Object IO.StreamReader (($zip.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1).Open())
            $commit = ([xml]$reader.ReadToEnd()).package.metadata.repository.commit
            $reader.Dispose()
        } finally { $zip.Dispose() }
        $added = git -C $Repo log -1 --format=%H --diff-filter=AM -- $_.Nupkg
        $onto = if ($added) { "$added^" } else { 'HEAD' }
        $inputs = $_.Inputs
        if (-not $commit) { "$($_.Name) (its nuspec names no commit)" }
        elseif (-not (git -C $Repo rev-parse -q --verify "$commit^{commit}")) { "$($_.Name) (built on $($commit.Substring(0, 7)), which this clone does not have)" }
        else {
            & git -C $Repo diff --quiet @script:CommentOnly $commit $onto -- @inputs
            if ($LASTEXITCODE -ne 0) { "$($_.Name) (built on $($commit.Substring(0, 7)), but its source differs from what it was committed onto)" }
        } })
    if ($misbuilt) { throw "Built on other source: $($misbuilt -join ', '). Raise <Version>, rebuild and commit local_packages\ first." }
    # update-workflows.ps1 would stop at the second.
    $twice = @(Get-ChildItem (Join-Path $Repo 'local_packages') -Filter '*.nupkg' |
        Group-Object { $_.Name -replace '\.\d+\.\d+\.\d+.*$', '' } | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name })
    if ($twice) { throw "Two versions in local_packages\ of: $($twice -join ', '). Keep one (build-package.ps1 removes the old)." }
}

function Get-LabPackages([string]$Repo) {
    # Only the lab's packages carry a <Version>; open-rigs' own are versioned when packed. A
    # package's source is its folder (not its READMEs), every file it compiles from elsewhere
    # (..\Shared\*.cs, ..\Shared\RigDevices.cs) and the build files every project imports: build\ (Common.csproj.targets
    # names what dependency version each nuspec carries; 86daefc changed it and nothing was
    # flagged, audit-rigs set C) and the Directory.Build files.
    @(Get-ChildItem (Join-Path $Repo 'src') -Directory | ForEach-Object {
        $project = Join-Path $_.FullName "$($_.Name).csproj"
        if (-not (Test-Path $project)) { return }
        $xml = [xml](Get-Content $project -Raw)
        $version = $xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
        if (-not $version) { return }
        $inputs = @("src/$($_.Name)", ":!src/$($_.Name)/*.md") + @($xml.Project.ItemGroup.Compile.Include | Where-Object { $_ -match '^\.\.\\' } |
            ForEach-Object { 'src/' + $_.Substring(3).Replace('\', '/') } | Sort-Object -Unique) + @('build', 'Directory.Build.props', 'Directory.Build.targets' | Where-Object { Test-Path (Join-Path $Repo $_) })
        [pscustomobject]@{ Id = $_.Name; Version = $version; Name = "$($_.Name) $version"; Nupkg = "local_packages/$($_.Name).$version.nupkg"; Inputs = $inputs } })
}
