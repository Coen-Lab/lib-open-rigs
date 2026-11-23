param(
    # The workflow repository's .bonsai\ folder: Bonsai.config, NuGet.config, and Bonsai once this has run.
    [Parameter(Mandatory)][string]$Environment
)
# The lab's Bonsai setup, one copy for every workflow: each one's .bonsai\Setup.ps1 runs this, from the open-rigs it
# pins (.venv\Lib\site-packages\open_rigs\scripts), on its own .bonsai\. Downloads the Bonsai that Bonsai.config
# pins when Bonsai.exe is not there yet, puts the workflow's layout in place, and restores the packages.
# Exits with Bonsai's restore's exit code, or 1 when a package Bonsai.config names was not installed.
Push-Location $Environment
if (!(Test-Path "./Bonsai.exe")) {
    $release = "https://github.com/bonsai-rx/bonsai/releases/latest/download/Bonsai.zip"
    $configPath = "./Bonsai.config"
    if (Test-Path $configPath) {
        [xml]$config = Get-Content $configPath
        $bootstrapper = $config.PackageConfiguration.Packages.Package.where{$_.id -eq 'Bonsai'}
        if ($bootstrapper) {
            $version = $bootstrapper.version
            $release = "https://github.com/bonsai-rx/bonsai/releases/download/$version/Bonsai.zip"
        }
    }
    Invoke-WebRequest $release -OutFile "temp.zip" -ErrorAction Stop
    # The zip holds a NuGet.config of its own, which would replace the workflow's.
    Move-Item -Path "NuGet.config" "temp.config" -ErrorAction SilentlyContinue
    Expand-Archive "temp.zip" -DestinationPath "." -Force
    if (Test-Path "temp.config") { Move-Item -Path "temp.config" "NuGet.config" -Force -ErrorAction Stop }
    Remove-Item -Path "temp.zip"
}
# The workflow's layout (config\main.layout), when it has one, into the editor's per-machine file. A rig's setup
# runs this again when the layout changes (a node added in front of Hardware moved its window index). A
# different one already here is kept beside it as main.saved.layout.
$layout = ".\Settings\src\main.layout"
$tracked = "..\config\main.layout"
if (Test-Path $tracked) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $layout) -Force | Out-Null
    # .NET's SHA-256: under Windows PowerShell started from pwsh, the Utility module's hash cmdlet does not load and both read null.
    function Get-Sha256([string]$path) {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { [BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $path).Path))) } finally { $sha.Dispose() }
    }
    if ((Test-Path $layout) -and ((Get-Sha256 $layout) -ne (Get-Sha256 $tracked))) {
        Copy-Item $layout ".\Settings\src\main.saved.layout" -Force
    }
    Copy-Item $tracked $layout -Force
}
# A package folder's name keeps the package's own version, which NuGet may have normalised in Bonsai.config
# (openal.redist.2.0.7.0 for 2.0.7), so trailing .0 parts are not compared.
function Get-NormalVersion([string]$v) { $parts = $v -split '-', 2; ($parts[0] -replace '(\.0)+$', '') + $(if ($parts.Count -gt 1) { '-' + $parts[1] }) }
function Get-Named {
    $named = @{}
    foreach ($p in ([xml](Get-Content ".\Bonsai.config" -Raw)).PackageConfiguration.Packages.Package) {
        $named["$($p.id).$(Get-NormalVersion $p.version)".ToLowerInvariant()] = "$($p.id) $($p.version)"
    }
    $named
}
# The committed Bonsai.config: a restore that updates packages can rewrite it without some it names (an update from
# older OpenRigs packages dropped Bonsai.DAQmx), so it is put back and restored once more, and every check below is
# against it.
$committed = if (Test-Path ".\Bonsai.config") { [IO.File]::ReadAllBytes((Resolve-Path ".\Bonsai.config").Path) }
$named = if ($committed) { Get-Named } else { @{} }
function Test-Dropped { $now = Get-Named; @($named.Keys | Where-Object { -not $now.ContainsKey($_) }).Count -gt 0 }
& .\Bonsai.exe --no-editor
$restoreExit = $LASTEXITCODE
if ($restoreExit -eq 0 -and $committed -and (Test-Dropped)) {
    "The restore rewrote Bonsai.config without packages it names; putting it back and restoring again"
    [IO.File]::WriteAllBytes((Resolve-Path ".\Bonsai.config").Path, $committed)
    & .\Bonsai.exe --no-editor
    $restoreExit = $LASTEXITCODE
    if (Test-Dropped) { [IO.File]::WriteAllBytes((Resolve-Path ".\Bonsai.config").Path, $committed) }
}
# After a good restore, each package folder Bonsai.config no longer names goes: the restore adds a folder per version
# and never removes one, so every package bump left the old one behind (one rig held three old versions
# beside the current ones). Bonsai loads from the AssemblyLocation paths only, so none was in use. A folder not
# named <id>.<version> is left alone.
if ($restoreExit -eq 0 -and (Test-Path ".\Packages") -and $committed) {
    foreach ($dir in Get-ChildItem ".\Packages" -Directory) {
        $m = [regex]::Match($dir.Name, '^(.+?)\.(\d+(\.\d+)*(-.+)?)$')
        if (-not $m.Success -or $named.ContainsKey("$($m.Groups[1].Value).$(Get-NormalVersion $m.Groups[2].Value)".ToLowerInvariant())) { continue }
        try { Remove-Item -LiteralPath $dir.FullName -Recurse -Force -ErrorAction Stop; "Removed $($dir.Name), which Bonsai.config no longer names" }
        catch { [Console]::Error.WriteLine("Could not remove $($dir.Name): $($_.Exception.Message)") }
    }
    # Every package Bonsai.config names has its folder, or the setup fails naming each missing one.
    $have = @{}
    foreach ($dir in Get-ChildItem ".\Packages" -Directory) {
        $m = [regex]::Match($dir.Name, '^(.+?)\.(\d+(\.\d+)*(-.+)?)$')
        if ($m.Success) { $have["$($m.Groups[1].Value).$(Get-NormalVersion $m.Groups[2].Value)".ToLowerInvariant()] = $true }
    }
    $missing = @($named.Keys | Where-Object { -not $have.ContainsKey($_) } | ForEach-Object { $named[$_] } | Sort-Object)
    if ($missing) {
        [Console]::Error.WriteLine("Bonsai.config names packages the restore did not install: $($missing -join ', '). Run this setup again; if they stay missing, delete .bonsai\Packages and run it again.")
        $restoreExit = 1
    }
}
Pop-Location
exit $restoreExit
