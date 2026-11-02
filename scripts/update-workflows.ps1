param(
    # This script's own clones of the workflows, never anybody's working copy: <Work>\<Org>\<repo>
    # for main and a worktree <Work>\<Org>\<repo>@<branch> for each other branch, each set to
    # exactly what GitHub has at every run.
    [string]$Work = (Join-Path $env:LOCALAPPDATA 'lib-open-rigs\clones'),
    # rig-setup's lists of the lab's repositories, every profile's: the ones its Repos button
    # writes on the share, and its own manifest's. Every one that is a Bonsai project
    # (rig-setup's rule: .bonsai\Bonsai.config and src\main.bonsai) is a lab workflow here.
    [string]$RepoList = 'K:\config\profile-repos.json',
    [string]$Manifest = 'C:\Github\Coen-Lab\rig-setup\manifest.json',
    # Lab workflows not (yet) on rig-setup: workflows.txt here, one GitHub <org>/<repo> a line.
    [string]$Extra = '',
    # The workflow whose Bonsai.config, written by Bonsai's own package manager and run on a
    # rig, supplies the entries for the packages the lab packages depend on.
    [string]$Reference = 'ucl-open/coen-behaviour-box'
)
# Keeps every lab Bonsai workflow on the lab's packages, on main and on each branch ticked for
# it in rig-setup: every package in local_packages\ here, at the version here, with what they
# depend on. Run by scripts\push.ps1 after each push here, and hourly (scripts\schedule.ps1),
# so a repository added with rig-setup's Repos button, or a branch ticked there, gets them
# unasked.
#
# For each of those branches:
#   - each package here is updated to the version here, or added if the branch lacks it
#     (scripts\install-package.ps1: local_packages\, Bonsai.config, .csproj PackageReference);
#   - each package they depend on that the branch lacks is added from the reference
#     workflow's Bonsai.config, entries as Bonsai wrote them there; one the branch already
#     has, at any version, is left as it is;
#   - .bonsai\NuGet.config gets the ../local_packages/ source if it lacks it;
#   - an open-rigs git source in pyproject.toml is set to this repository at the last commit
#     that changed src\, pyproject.toml or local_packages\, then uv.lock is relocked and, if
#     the workflow has regenerate-schemas, its schema and generated classes are regenerated.
# A branch that changed is checked with its own scripts\check-workflow.ps1, or else with
# scripts\check-bonsai.ps1 here; if that passes, those files and nothing else are committed and
# pushed to that branch, and rig-setup's 30-minute update takes main to the rigs. A failure is
# not committed and is named in the table; the next run tries again from what GitHub has.
# Exits 1 unless every branch of every lab workflow is up to date.
$ErrorActionPreference = 'Stop'
# A throw (unpushed commits here, no lab share) is written to the output and exits 1: the hourly
# task's *> redirect does not catch a terminating error, so its log stopped after "Packages:"
# with nothing saying why (audit, 2 Nov 2026).
trap { "Stopped: $($_.Exception.Message)"; exit 1 }
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$local = Join-Path $repo 'local_packages'
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Windows PowerShell turns a native command's stderr into errors, which 'Stop' would throw on,
# so native output is collected as text and only the exit code decides.
function Invoke-Native([string]$Exe) {
    $ErrorActionPreference = 'Continue'
    & $Exe @args 2>&1 | ForEach-Object { "$_" }
}
function Invoke-Git([string]$Dir) {
    $out = Invoke-Native git -C $Dir @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' '): $(($out | Out-String).Trim())" }
    $out
}
function Get-Nuspec([string]$Nupkg) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Nupkg)
    try {
        $spec = $zip.Entries | Where-Object { $_.FullName -notmatch '/' -and $_.Name -like '*.nuspec' } | Select-Object -First 1
        $reader = New-Object IO.StreamReader($spec.Open())
        try { [xml]$reader.ReadToEnd() } finally { $reader.Close() }
    } finally { $zip.Dispose() }
}
# The ids a package depends on under .NET Framework (Bonsai's), from its nuspec.
function Get-Dependencies([xml]$Nuspec) {
    $deps = $Nuspec.package.metadata.dependencies
    if (-not $deps) { return @() }
    $groups = @($deps.group | Where-Object { $_ -and ($_.targetFramework -match '^(\.NETFramework|net4)' -or -not $_.targetFramework) })
    @($groups | ForEach-Object { $_.dependency } | Where-Object { $_ } | ForEach-Object { $_.id }) + @($deps.dependency | Where-Object { $_ } | ForEach-Object { $_.id })
}
function Get-ConfigVersion([string]$Text, [string]$Id) {
    $m = [regex]::Match($Text, "<Package id=`"$([regex]::Escape($Id))`" version=`"([^`"]+)`"")
    if ($m.Success) { $m.Groups[1].Value } else { '' }
}
function Get-Clone([string]$Url) {
    if ($Url -notmatch 'github\.com[/:]([^/]+)/([^/]+?)(\.git)?/?$') { throw "not a GitHub repository URL: $Url" }
    [pscustomobject]@{ Slug = "$($Matches[1])/$($Matches[2])"; Dir = Join-Path $Work "$($Matches[1])\$($Matches[2])"; Url = $Url }
}
# This script's clone of a repository, cloned the first time and fetched every time.
function Sync-Repo($Clone) {
    if (-not (Test-Path (Join-Path $Clone.Dir '.git'))) {
        New-Item -ItemType Directory -Force (Split-Path $Clone.Dir) | Out-Null
        $out = Invoke-Native git clone -q --no-checkout $Clone.Url $Clone.Dir
        if ($LASTEXITCODE -ne 0) { throw "could not clone $($Clone.Url): $(($out | Out-String).Trim())" }
    }
    $null = Invoke-Git $Clone.Dir fetch -q --prune origin
}
# The folder a branch is worked in, set to exactly what GitHub has: the clone itself for main,
# otherwise a worktree beside it named as rig-setup names branch clones, <repo>@<branch>.
# What is gitignored (.bonsai\Packages, Bonsai.exe) stays.
function Get-BranchFolder([string]$Dir, [string]$Branch) {
    if ($Branch -eq 'main') { return $Dir }
    "$Dir@$($Branch -replace '[\\/:*?"<>|]', '-')"
}
function Set-Branch([string]$Dir, [string]$Branch) {
    $wt = Get-BranchFolder $Dir $Branch
    if (-not (Test-Path (Join-Path $wt '.git'))) { $null = Invoke-Git $Dir worktree add -q --detach $wt "origin/$Branch" }
    $null = Invoke-Git $wt checkout -q --detach "origin/$Branch"
    $null = Invoke-Git $wt reset -q --hard "origin/$Branch"
    $null = Invoke-Git $wt clean -fdq
    $wt
}
# What a Bonsai setup has left undone in a folder: Bonsai.exe, then each package Bonsai.config
# names without its folder in .bonsai\Packages. Empty once it is set up.
function Get-SetupGap([string]$Dir) {
    $bonsai = Join-Path $Dir '.bonsai'
    if (-not (Test-Path (Join-Path $bonsai 'Bonsai.exe'))) { return @('Bonsai.exe') }
    $config = [IO.File]::ReadAllText((Join-Path $bonsai 'Bonsai.config'))
    # NuGet may name the folder with a fourth part the config leaves off (openal.redist 2.0.7 is
    # Packages\openal.redist.2.0.7.0).
    @([regex]::Matches($config, '<Package id="([^"]+)" version="([^"]+)"') | ForEach-Object { "$($_.Groups[1].Value).$($_.Groups[2].Value)" } |
        Where-Object { -not (Test-Path (Join-Path $bonsai "Packages\$_")) -and -not (Test-Path (Join-Path $bonsai "Packages\$_.0")) })
}
# A folder not set up here gets its Bonsai and packages, as rig-setup does: from a copy of
# main's packages when there is one, so the restore fetches only what differs. Judged by what
# the setup leaves, not by .bonsai\Packages existing: a setup killed halfway, or one that found
# no network after main's packages were copied in, left the folder and was never run again
# (round 7, 1 Nov 2026).
function Initialize-Bonsai([string]$Dir, [string]$From) {
    $bonsai = Join-Path $Dir '.bonsai'; $unpacked = Join-Path $bonsai 'Packages'; $exe = Join-Path $bonsai 'Bonsai.exe'
    if (-not (Get-SetupGap $Dir)) { return }
    "  setting up its Bonsai (.bonsai\Setup.ps1)..."
    if ($From -and (Test-Path (Join-Path $From '.bonsai\Packages'))) {
        $null = Invoke-Native robocopy (Join-Path $From '.bonsai\Packages') $unpacked /E /NFL /NDL /NJH /NJS /NP
    }
    # Setup.ps1 downloads Bonsai.exe, then starts it to restore the packages; Bonsai is a
    # windowed program that PowerShell does not wait for, so that restore is waited for here,
    # then run once more and waited for, and judged by the Packages folder it leaves.
    $null = Invoke-Native powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $bonsai 'Setup.ps1')
    $deadline = (Get-Date).AddMinutes(20)
    Get-Process Bonsai -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } |
        ForEach-Object { $null = $_.WaitForExit([int][math]::Max(0, ($deadline - (Get-Date)).TotalMilliseconds)) }
    if (Test-Path $exe) {
        $restore = Start-Process $exe -ArgumentList '--no-editor' -WorkingDirectory $bonsai -PassThru -WindowStyle Hidden
        if (-not $restore.WaitForExit([int][math]::Max(0, ($deadline - (Get-Date)).TotalMilliseconds))) { $restore.Kill() }
    }
    $gap = @(Get-SetupGap $Dir)
    if ($gap) { throw "Bonsai setup left $($gap.Count) missing, first $($gap[0]) (no network, or a Bonsai.config Bonsai cannot read)" }
    # A Bonsai.exe newer than Bonsai.config upgrades the environment as it restores; that is
    # never this script's to commit.
    if (Invoke-Git $Dir status --porcelain) { throw "Bonsai's setup changed tracked files ($((Invoke-Git $Dir status --porcelain) -join ', ')); nothing done" }
}
# Adds a line to a Bonsai.config section, alphabetically by the id or name it carries, making
# the section if the file has none (LibraryFolders).
function Add-ConfigLine([string]$Text, [string]$Section, [string]$Line) {
    $nl = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $lines = [Collections.Generic.List[string]]($Text -split $nl)
    $start = $lines.FindIndex([Predicate[string]] { param($l) $l.Trim() -eq "<$Section>" })
    if ($start -lt 0) {
        $end = $lines.FindIndex([Predicate[string]] { param($l) $l.Trim() -eq '</PackageConfiguration>' })
        $lines.InsertRange($end, [string[]]@("  <$Section>", "  </$Section>"))
        $start = $end
    }
    $key = { param($l) [regex]::Match($l, '(?:id|assemblyName|path)="([^"]+)"').Groups[1].Value }
    $mine = & $key $Line
    $insertAt = $start + 1
    for ($i = $start + 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*</') { break }
        $name = & $key $lines[$i]
        if ($name -and [string]::Compare($name, $mine, $true) -lt 0) { $insertAt = $i + 1 }
    }
    $lines.Insert($insertAt, $Line)
    $lines -join $nl
}

# The version list: one .nupkg per package id.
$packages = @{}
foreach ($f in Get-ChildItem $local -File -Filter '*.nupkg') {
    $nuspec = Get-Nuspec $f.FullName
    $id = $nuspec.package.metadata.id
    if ($packages.ContainsKey($id)) { throw "local_packages\ holds two versions of ${id}: $($packages[$id].File.Name) and $($f.Name). Keep one." }
    $packages[$id] = [pscustomobject]@{ Version = $nuspec.package.metadata.version; File = $f; Hash = (Get-FileHash $f.FullName).Hash; Nuspec = $nuspec }
}
"Packages: $(($packages.Keys | Sort-Object | ForEach-Object { "$_ $($packages[$_].Version)" }) -join ', ')"

# This repository must be what GitHub has: every workflow commit names the commit here.
$null = Invoke-Git $repo fetch -q
if ([int](Invoke-Git $repo rev-list --count '@{u}..HEAD')) { throw "lib-open-rigs has unpushed commits. Push them first (scripts\push.ps1)." }
if (Invoke-Git $repo status --porcelain -- local_packages) { throw "local_packages\ here has uncommitted changes. Commit and push them first." }
if ([int](Invoke-Git $repo rev-list --count 'HEAD..@{u}')) {
    if (Invoke-Git $repo status --porcelain) { throw "lib-open-rigs is behind GitHub and has local changes. Commit or discard them first." }
    $null = Invoke-Git $repo merge --ff-only -q '@{u}'
    "lib-open-rigs brought up to date with GitHub; running the updated script."
    & powershell -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path -Work $Work -RepoList $RepoList -Manifest $Manifest -Extra $Extra -Reference $Reference
    exit $LASTEXITCODE
}
$source = (Invoke-Git $repo rev-parse --short HEAD) | Select-Object -First 1
# The commit every workflow's open-rigs Python package is pinned to: the last one here that
# changed what the workflows run (src\, pyproject.toml, local_packages\), so the schema and the
# Bonsai packages come from the same open-rigs and a docs or scripts commit moves no pin.
$pin = (Invoke-Git $repo log -1 --format=%H -- src pyproject.toml local_packages) | Select-Object -First 1
$pinLine = "open-rigs = {git = `"https://github.com/Coen-Lab/lib-open-rigs`", rev = `"$pin`"}"

# One run at a time: push.ps1's and the hourly one share the clones. A run already going may
# have read local_packages\ before this push, so this one waits for it rather than trusting it.
$mutex = New-Object Threading.Mutex($false, 'Global\lib-open-rigs-update-workflows')
try { $mine = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $mine = $true }
if (-not $mine) {
    "Another run of update-workflows.ps1 is going; waiting for it to finish (at most an hour)..."
    try { $mine = $mutex.WaitOne([TimeSpan]::FromHours(1)) } catch [Threading.AbandonedMutexException] { $mine = $true }
    if (-not $mine) { throw "The other run of update-workflows.ps1 has held the clones for an hour; nothing done. The next hourly run tries again." }
}

# rig-setup's repos for every profile, then workflows.txt, each repository once.
if (-not (Test-Path $RepoList)) { throw "Cannot read rig-setup's repo list at $RepoList (is the lab share mapped?)." }
if (-not (Test-Path $Manifest)) { throw "Cannot read rig-setup's manifest at $Manifest." }
if (-not $Extra) { $Extra = Join-Path $repo 'workflows.txt' }
# Each repository's branches besides main are the ones ticked for it in rig-setup (a
# "branches" list on its entry), or named after it in workflows.txt.
# A workflows.txt line with !main leaves main alone and keeps only the branches named: a
# workflow whose main runs live experiments while a branch moves to open-rigs (av-dome).
$entries = @((Get-Content $RepoList -Raw | ConvertFrom-Json).repos | ForEach-Object { [pscustomobject]@{ Url = $_.url; Branches = @($_.branches | Where-Object { $_ }); SkipMain = $false } }) +
    @((Get-Content $Manifest -Raw | ConvertFrom-Json).repos | ForEach-Object { [pscustomobject]@{ Url = $_.url; Branches = @($_.branches | Where-Object { $_ }); SkipMain = $false } }) +
    @(Get-Content $Extra | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') } | ForEach-Object {
        $slug, $rest = $_ -split '\s+'
        [pscustomobject]@{ Url = "https://github.com/$slug.git"; Branches = @($rest | Where-Object { $_ -and $_ -ne '!main' }); SkipMain = '!main' -in $rest } })
$clones = @(); $listed = @{}
foreach ($e in $entries) {
    $c = Get-Clone $e.Url; $k = $c.Slug.ToLower()
    if (-not $listed.ContainsKey($k)) { $listed[$k] = $c; $c | Add-Member Branches @(); $c | Add-Member SkipMain $true; $clones += $c }
    $listed[$k].Branches = @($listed[$k].Branches + $e.Branches | Where-Object { $_ -ne 'main' } | Sort-Object -Unique)
    # Any list that wants main (rig-setup's always do) keeps it.
    $listed[$k].SkipMain = $listed[$k].SkipMain -and $e.SkipMain
}

# The packages the lab packages depend on, as the reference workflow's main has them: its
# Bonsai.config lines for each, and its restored folder for checks. A dependency the reference
# does not have is one it runs without, so no workflow is given it either.
$ref = $clones | Where-Object { $_.Slug -eq $Reference } | Select-Object -First 1
if (-not $ref) { $ref = Get-Clone "https://github.com/$Reference.git" }
Sync-Repo $ref
$refDir = Set-Branch $ref.Dir 'main'
Initialize-Bonsai $refDir ''
$refConfig = [IO.File]::ReadAllText((Join-Path $refDir '.bonsai\Bonsai.config'))
$refPackages = Join-Path $refDir '.bonsai\Packages'
$dependencies = [ordered]@{}
$queue = [Collections.Generic.Queue[string]]::new()
$seen = @{}
foreach ($id in $packages.Keys) { $queue.Enqueue($id) }
while ($queue.Count) {
    $id = $queue.Dequeue()
    if ($seen.ContainsKey($id)) { continue }
    $seen[$id] = $true
    if ($packages.ContainsKey($id)) { $nuspec = $packages[$id].Nuspec }
    else {
        $v = Get-ConfigVersion $refConfig $id
        if (-not $v) { continue }
        $spec = Get-ChildItem (Join-Path $refPackages "$id.$v") -Filter '*.nuspec' -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $spec) { throw "The reference workflow lists $id $v but has no .bonsai\Packages\$id.${v}." }
        $nuspec = [xml](Get-Content $spec.FullName -Raw)
        $prefix = [regex]::Escape("Packages/$id.$v/")
        $locations = @($refConfig -split "`r?`n" | Where-Object { $_ -match "<AssemblyLocation .*location=`"$prefix" })
        $names = @($locations | ForEach-Object { [regex]::Match($_, 'assemblyName="([^"]+)"').Groups[1].Value })
        $lines = @($refConfig -split "`r?`n" | Where-Object { $_ -match "<Package id=`"$([regex]::Escape($id))`" " } | Select-Object -First 1 |
                ForEach-Object { [pscustomobject]@{ Section = 'Packages'; Line = $_ } }) +
            @($refConfig -split "`r?`n" | Where-Object { $_ -match '<AssemblyReference assemblyName="([^"]+)"' -and $Matches[1] -in $names } |
                ForEach-Object { [pscustomobject]@{ Section = 'AssemblyReferences'; Line = $_ } }) +
            @($locations | ForEach-Object { [pscustomobject]@{ Section = 'AssemblyLocations'; Line = $_ } }) +
            @($refConfig -split "`r?`n" | Where-Object { $_ -match "<LibraryFolder .*path=`"$prefix" } |
                ForEach-Object { [pscustomobject]@{ Section = 'LibraryFolders'; Line = $_ } })
        $dependencies[$id] = [pscustomobject]@{ Version = $v; Lines = $lines }
    }
    foreach ($d in Get-Dependencies $nuspec) { $queue.Enqueue($d) }
}
"With what they depend on: $($dependencies.Count) packages from $Reference's Bonsai.config."

# One branch of one workflow, in its folder set to what GitHub has. Returns its row, or nothing
# for a branch other than main that is not a Bonsai project.
function Update-Branch([string]$Slug, [string]$Branch, [string]$Dir, [string]$MainDir) {
    $name = if ($Branch -eq 'main') { $Slug } else { "$Slug@$Branch" }
    $row = [pscustomobject]@{ Workflow = $name; Result = ''; Detail = '' }
    "`n== $name"
    try {
        $bonsai = Join-Path $Dir '.bonsai'
        if (-not ((Test-Path (Join-Path $bonsai 'Bonsai.config')) -and (Test-Path (Join-Path $Dir 'src\main.bonsai')))) {
            if ($Branch -ne 'main') { return }
            $row.Result = 'not Bonsai'; return $row
        }
        Initialize-Bonsai $Dir $(if ($Branch -ne 'main') { $MainDir } else { '' })

        # What to do, decided before anything changes.
        $config = [IO.File]::ReadAllText((Join-Path $bonsai 'Bonsai.config'))
        $unpacked = Join-Path $bonsai 'Packages'
        $plan = @(); $clash = @()
        foreach ($id in $packages.Keys | Sort-Object) {
            $p = $packages[$id]; $have = Get-ConfigVersion $config $id
            $copy = Join-Path $Dir "local_packages\$id.$have.nupkg"
            $action = if (-not $have) { "added $id $($p.Version)" }
                elseif ($have -ne $p.Version) { "$id $have -> $($p.Version)" }
                elseif (-not (Test-Path $copy)) { "$id $have (local_packages had no copy)" }
                elseif ((Get-FileHash $copy).Hash -ne $p.Hash) { $clash += "$id $have"; continue }
                # The gitignored .bonsai\Packages can be on another version after a failed run.
                elseif (-not (Test-Path (Join-Path $unpacked "$id.$have"))) { '' }
                else { continue }
            $plan += [pscustomobject]@{ Package = $p; Change = $action }
        }
        if ($clash) { $row.Result = 'FAILED'; $row.Detail = "not the same file as the same version in lib-open-rigs: $($clash -join ', '). One was rebuilt without raising its version. Nothing changed."; return $row }
        $missing = @($dependencies.Keys | Where-Object { -not (Get-ConfigVersion $config $_) })

        foreach ($step in $plan) {
            if ($step.Change) { & (Join-Path $PSScriptRoot 'install-package.ps1') -Nupkg $step.Package.File.FullName -Environment $Dir }
            else { $null = & (Join-Path $PSScriptRoot 'install-package.ps1') -Nupkg $step.Package.File.FullName -Environment $Dir; "  $($step.Package.File.Name): unpacked into .bonsai\Packages again" }
        }
        $changes = @($plan | Where-Object Change | ForEach-Object Change)
        if ($missing) {
            $configPath = Join-Path $bonsai 'Bonsai.config'
            $bytes = [IO.File]::ReadAllBytes($configPath)
            $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
            $text = [IO.File]::ReadAllText($configPath)
            foreach ($id in $missing) {
                $d = $dependencies[$id]
                foreach ($l in $d.Lines) { $text = Add-ConfigLine $text $l.Section $l.Line }
                $from = Join-Path $refPackages "$id.$($d.Version)"
                if (-not (Test-Path (Join-Path $unpacked "$id.$($d.Version)"))) { Copy-Item $from $unpacked -Recurse }
                $changes += "added $id $($d.Version)"
            }
            # Bonsai refuses a Bonsai.config that is not well-formed XML; never leave one.
            try { $null = [xml]$text } catch { throw "the merged Bonsai.config would not be valid XML ($($_.Exception.Message)); nothing written" }
            [IO.File]::WriteAllText($configPath, $text, (New-Object Text.UTF8Encoding($bom)))
            "  Bonsai.config: added $($missing.Count) packages the lab packages depend on, as $Reference has them"
        }
        $nugetConfig = Join-Path $bonsai 'NuGet.config'
        $nuget = [IO.File]::ReadAllText($nugetConfig)
        if ($nuget -notmatch 'value="\.\./local_packages/?"') {
            $nl = if ($nuget.Contains("`r`n")) { "`r`n" } else { "`n" }
            $nuget = $nuget -replace '(\r?\n)(\s*)</packageSources>', "${nl}    <add key=`"local packages`" value=`"../local_packages/`" />`$1`$2</packageSources>"
            [IO.File]::WriteAllText($nugetConfig, $nuget, (New-Object Text.UTF8Encoding($false)))
            $changes += 'NuGet.config: the ../local_packages/ source'
        }
        # A workflow that takes open-rigs from git ([tool.uv.sources]) takes it from here, at $pin,
        # with its lock and its generated schema (regenerate-schemas) to match.
        $pyproject = Join-Path $Dir 'pyproject.toml'
        $py = if (Test-Path $pyproject) { [IO.File]::ReadAllText($pyproject) } else { '' }
        $m = [regex]::Match($py, '(?m)^open-rigs\s*=\s*\{[^}\r\n]*\}')
        if ($m.Success -and $m.Value -ne $pinLine) {
            [IO.File]::WriteAllText($pyproject, $py.Replace($m.Value, $pinLine), (New-Object Text.UTF8Encoding($false)))
            $out = Invoke-Native uv lock --directory $Dir
            if ($LASTEXITCODE -ne 0) { throw "uv lock failed: $(($out | Select-Object -Last 3) -join ' / ')" }
            if ($py -match '(?m)^regenerate-schemas\s*=') {
                # Its paths are relative to the repository, and it runs the bonsai.sgen tool.
                Push-Location $Dir
                try {
                    if (Test-Path '.config\dotnet-tools.json') { $null = Invoke-Native dotnet tool restore }
                    $out = Invoke-Native uv run regenerate-schemas
                    if ($LASTEXITCODE -ne 0) { throw "regenerate-schemas failed: $(($out | Select-Object -Last 3) -join ' / ')" }
                } finally { Pop-Location }
            }
            $changes += "open-rigs (Python) from lib-open-rigs $($pin.Substring(0, 7)): pyproject.toml, uv.lock$(if ($py -match '(?m)^regenerate-schemas\s*=') { ', regenerated schemas' })"
        }
        if (-not $changes) { $row.Result = 'up to date'; return $row }
        $written = (Get-FileHash (Join-Path $bonsai 'Bonsai.config')).Hash
        # What this script wrote, staged before the check, so whatever the check changes is stray.
        $null = Invoke-Git $Dir add -A

        $own = Join-Path $Dir 'scripts\check-workflow.ps1'
        $check = if (Test-Path $own) { 'scripts\check-workflow.ps1' } else { 'lib-open-rigs scripts\check-bonsai.ps1' }
        "  checking with $check..."
        $log = if (Test-Path $own) { Invoke-Native powershell -NoProfile -ExecutionPolicy Bypass -File $own }
            else { Invoke-Native powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'check-bonsai.ps1') -Repo $Dir }
        $passed = $LASTEXITCODE -eq 0
        $log | ForEach-Object { "  | $_" }
        if (-not $passed) {
            # The first lines that say what broke: an exception, a compiler error or a failed test.
            $why = @($log | Where-Object { $_ -match 'Exception|error [A-Z]+\d+|^\s+FAIL\s' -and $_ -notmatch '^\s*(\+|At |CategoryInfo|FullyQualifiedErrorId)' } |
                Select-Object -First 2 | ForEach-Object { $_.Trim() })
            if (-not $why) { $why = @($log | Where-Object { $_.Trim() } | Select-Object -Last 1 | ForEach-Object { $_.Trim() }) }
            $row.Result = 'FAILED'; $row.Detail = "$check failed ($($why -join ' / ')); not committed: $($changes -join '; ')"; return $row
        }
        if ((Get-FileHash (Join-Path $bonsai 'Bonsai.config')).Hash -ne $written) { $row.Result = 'FAILED'; $row.Detail = "Bonsai.config was changed after this script wrote it, by the check; not committed: $($changes -join '; ')"; return $row }

        # Only the files this script wrote, staged above.
        $stray = @(Invoke-Git $Dir status --porcelain) | Where-Object { $_ -notmatch '^[MADR] ' }
        if ($stray) { $row.Result = 'FAILED'; $row.Detail = "files changed during the check ($($stray -join ', ')); not committed: $($changes -join '; ')"; return $row }
        $message = "The lab's open-rigs from lib-open-rigs $source`n`n" + (($changes | ForEach-Object { "- $_" }) -join "`n") +
            "`n`nThese keep the workflow's Bonsai packages and open-rigs at the lab's versions.`n$check passed.`nBy lib-open-rigs scripts\update-workflows.ps1.`n"
        $msgFile = [IO.Path]::GetTempFileName()
        [IO.File]::WriteAllText($msgFile, $message, (New-Object Text.UTF8Encoding($false)))
        try { $null = Invoke-Git $Dir commit -q -F $msgFile } finally { Remove-Item $msgFile }
        # Refused if somebody pushed to the branch meanwhile; the next run starts from theirs.
        $null = Invoke-Git $Dir push -q origin "HEAD:refs/heads/$Branch"
        $row.Result = 'updated'; $row.Detail = "$($changes.Count) change(s), pushed $((Invoke-Git $Dir rev-parse --short HEAD) | Select-Object -First 1): $($changes -join '; ')"
    } catch {
        $row.Result = 'FAILED'; $row.Detail = "$($_.Exception.Message)"
    }
    $row
}

$rows = @()
try {
    foreach ($clone in $clones) {
        try { Sync-Repo $clone } catch { $rows += [pscustomobject]@{ Workflow = $clone.Slug; Result = 'FAILED'; Detail = "$($_.Exception.Message)" }; continue }
        $branches = @(Invoke-Git $clone.Dir for-each-ref '--format=%(refname:strip=3)' refs/remotes/origin | Where-Object { $_ -and $_ -ne 'HEAD' })
        if ('main' -notin $branches) { $rows += [pscustomobject]@{ Workflow = $clone.Slug; Result = 'FAILED'; Detail = 'has no main branch on GitHub' }; continue }
        foreach ($branch in @(if (-not $clone.SkipMain) { 'main' }) + @($clone.Branches)) {
            if ($branch -notin $branches) { $rows += [pscustomobject]@{ Workflow = "$($clone.Slug)@$branch"; Result = 'FAILED'; Detail = "ticked in rig-setup, but GitHub has no branch $branch" }; continue }
            try { $dir = Set-Branch $clone.Dir $branch }
            catch { $rows += [pscustomobject]@{ Workflow = "$($clone.Slug)@$branch"; Result = 'FAILED'; Detail = "$($_.Exception.Message)" }; continue }
            # Its progress lines are printed; its row is the one object it returns.
            $out = @(Update-Branch $clone.Slug $branch $dir $clone.Dir)
            $out | Where-Object { $_ -is [string] }
            $r = $out | Where-Object { $_ -isnot [string] } | Select-Object -Last 1
            if ($r) { $rows += $r }
            if ($branch -eq 'main' -and $r.Result -eq 'not Bonsai') { break }
        }
        # A branch unticked, or deleted on GitHub: its worktree goes too.
        $keep = @($clone.Branches | Where-Object { $_ -in $branches } | ForEach-Object { Get-BranchFolder $clone.Dir $_ })
        Get-ChildItem (Split-Path $clone.Dir) -Directory -Filter "$(Split-Path $clone.Dir -Leaf)@*" |
            Where-Object { $_.FullName -notin $keep } | ForEach-Object {
                "  removing $($_.Name): its branch is no longer ticked, or no longer on GitHub"
                $null = Invoke-Native git -C $clone.Dir worktree remove --force $_.FullName
                if (Test-Path $_.FullName) { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
            }
        $null = Invoke-Native git -C $clone.Dir worktree prune
    }
} finally { $mutex.ReleaseMutex() }

"`n" + ($rows | Format-Table -AutoSize -Wrap | Out-String).TrimEnd()
$bad = @($rows | Where-Object { $_.Result -notin 'up to date', 'updated', 'not Bonsai' })
if ($bad) { "`n$($bad.Count) of $($rows.Count) branches are not on the lab's packages."; exit 1 }
"`nEvery lab Bonsai workflow is on the lab's packages, on main and every ticked branch."
