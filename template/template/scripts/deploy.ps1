param(
    # Root of the per-machine rig configuration directories. The rig workflow reads
    # <ConfigRoot>\<MACHINE-NAME>\rig.yml and calibration artefacts beside it.
    [string]$ConfigRoot = "C:\RigConfigs"
)

$scriptPath = $MyInvocation.MyCommand.Path
$scriptDirectory = Split-Path -Parent $scriptPath
Set-Location (Split-Path -Parent $scriptDirectory)

Write-Output "Initializing and updating submodules..."
&git submodule update --init --recursive

if (-not (Get-Command uv -ErrorAction SilentlyContinue)) {
    throw "The 'uv' command was not found. See https://docs.astral.sh/uv/getting-started/installation/ for instructions."
}

Write-Output "Creating a Python environment..."
if (Test-Path -Path ./.venv) {
    Remove-Item ./.venv -Recurse -Force
}
&uv venv
.\.venv\Scripts\Activate.ps1
Write-Output "Synchronizing environment..."
&uv sync

Write-Output "Regenerating JSON schema from pydantic models..."
&uv run regenerate-schemas

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The 'dotnet' command was not found. Install the .NET SDK: https://dotnet.microsoft.com/download"
}

Write-Output "Restoring dotnet tools..."
&dotnet tool restore

# Derive C# namespace from schema filename (snake_case -> PascalCase).
$schemaFile = Get-ChildItem ".\src\DataSchemas\*.json" | Select-Object -First 1
if (-not $schemaFile) { throw "No JSON schema found in .\src\DataSchemas\. Run 'uv run regenerate-schemas' first." }
$namespace = ($schemaFile.BaseName -split "_" | ForEach-Object { $_.Substring(0,1).ToUpper() + $_.Substring(1) }) -join ""

Write-Output "Generating C# classes (namespace: $namespace, serializers: json yaml)..."
Push-Location .\src\Extensions
&dotnet bonsai.sgen $schemaFile.FullName --namespace $namespace --serializer json --serializer yaml
Pop-Location

Write-Output "Writing the rig configuration template..."
&uv run python examples/rig.py

# Same name the rig workflow resolves at startup (Environment.MachineName).
$machineDirectory = Join-Path $ConfigRoot ([Environment]::MachineName)
Write-Output "Preparing the rig configuration directory $machineDirectory..."
New-Item -ItemType Directory -Path (Join-Path $machineDirectory "calibration") -Force | Out-Null
$rigConfig = Join-Path $machineDirectory "rig.yml"
if (Test-Path -Path $rigConfig) {
    Write-Output "Keeping the existing $rigConfig"
} else {
    # The template's $schema path is relative to config\; point the copy at this checkout instead.
    $schemaDirectory = (Resolve-Path ".\src\DataSchemas").Path -replace "\\", "/"
    $text = [IO.File]::ReadAllText((Resolve-Path ".\config\rig-template.yml").Path)
    $text = $text.Replace('$schema=../src/DataSchemas/', '$schema=' + $schemaDirectory + '/')
    [IO.File]::WriteAllText($rigConfig, $text)
    Write-Output "Created $rigConfig from config\rig-template.yml; fill in this rig's values there."
}

if (-not (Test-Path -Path src\DataSchemas)) {
    New-Item -ItemType Directory -Path src\DataSchemas | Out-Null
}
if (-not (Test-Path -Path src\Extensions)) {
    New-Item -ItemType Directory -Path src\Extensions | Out-Null
}
