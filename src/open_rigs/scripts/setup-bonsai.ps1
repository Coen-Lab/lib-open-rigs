param(
    # The workflow repository's .bonsai\ folder: Bonsai.config, NuGet.config, and Bonsai once this has run.
    [Parameter(Mandatory)][string]$Environment
)
# The lab's Bonsai setup, one copy for every workflow: each one's .bonsai\Setup.ps1 runs this, from the open-rigs it
# pins (.venv\Lib\site-packages\open_rigs\scripts), on its own .bonsai\. Downloads the Bonsai that Bonsai.config
# pins when Bonsai.exe is not there yet, puts the workflow's layout in place, and restores the packages.
# Exits with Bonsai's restore's exit code.
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
    Move-Item -Path "temp.config" "NuGet.config" -Force -ErrorAction SilentlyContinue
    Remove-Item -Path "temp.zip"
}
# The workflow's layout (config\main.layout), when it has one, into the editor's per-machine file. rig-setup runs
# this again when the layout changes (3 Nov 2026: a node added in front of Hardware moved its window index). A
# different one already here is kept beside it as main.saved.layout.
$layout = ".\Settings\src\main.layout"
$tracked = "..\config\main.layout"
if (Test-Path $tracked) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $layout) -Force | Out-Null
    if ((Test-Path $layout) -and ((Get-FileHash $layout).Hash -ne (Get-FileHash $tracked).Hash)) {
        Copy-Item $layout ".\Settings\src\main.saved.layout" -Force
    }
    Copy-Item $tracked $layout -Force
}
& .\Bonsai.exe --no-editor
$restoreExit = $LASTEXITCODE
Pop-Location
exit $restoreExit
