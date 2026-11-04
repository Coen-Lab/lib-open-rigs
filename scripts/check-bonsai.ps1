param(
    # A Bonsai project in the open-rigs layout: .bonsai\ with its packages restored, and
    # src\main.bonsai.
    [Parameter(Mandatory)][string]$Repo,
    # Internal: the step this script runs in a fresh process of its own (assemblies stay loaded).
    [ValidateSet('', 'Build')][string]$Step = ''
)
# The check update-workflows.ps1 runs on a workflow that has no scripts\check-workflow.ps1 of
# its own, without the rig and without opening Bonsai:
#   1. Compiles src\Extensions, if the workflow has one, under C# 5, the language Bonsai
#      compiles it with.
#   2. Builds src\main.bonsai with Bonsai's own expression builders, as Bonsai does at Start
#      before it subscribes anything: every type, selector, include, subject and mapping.
# It cannot check hardware, visualizers or the data a run writes. Exit 0 when both pass.
$ErrorActionPreference = 'Stop'
$self = $MyInvocation.MyCommand.Path
$Repo = (Resolve-Path $Repo).Path
$bonsaiDir = Join-Path $Repo '.bonsai'
$bin = Join-Path ([IO.Path]::GetTempPath()) ("check-bonsai-" + (Split-Path $Repo -Leaf))
$workflow = Join-Path $Repo 'src\main.bonsai'

if ($Step -eq 'Build') {
    [xml]$cfg = Get-Content (Join-Path $bonsaiDir 'Bonsai.config') -Raw
    $map = @{}
    foreach ($a in $cfg.PackageConfiguration.AssemblyLocations.AssemblyLocation) {
        if ($a.processorArchitecture -in 'MSIL', 'Amd64') { $map[$a.assemblyName] = Join-Path $bonsaiDir $a.location }
    }
    if (Test-Path (Join-Path $bin 'Extensions.dll')) { $map['Extensions'] = Join-Path $bin 'Extensions.dll' }
    # Native libraries (OpenCV, libgit2) from the folders Bonsai.config names for x64.
    foreach ($f in $cfg.PackageConfiguration.LibraryFolders.LibraryFolder) {
        if ($f.platform -eq 'x64') { $env:PATH = (Join-Path $bonsaiDir $f.path) + ';' + $env:PATH }
    }
    $script:resolveMap = $map; $script:resolvePkgs = Join-Path $bonsaiDir 'Packages'; $script:resolveBin = $bin
    $resolver = [ResolveEventHandler]{
        param($s, $e)
        $n = (New-Object Reflection.AssemblyName $e.Name).Name
        $loaded = [AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq $n } | Select-Object -First 1
        if ($loaded) { return $loaded }
        $p = $script:resolveMap[$n]
        if (-not $p -and (Test-Path (Join-Path $script:resolveBin "$n.dll"))) { $p = Join-Path $script:resolveBin "$n.dll" }
        if (-not $p) {
            $p = Get-ChildItem $script:resolvePkgs -Recurse -Filter "$n.dll" -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\lib\\net4' } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
        }
        if ($p -and (Test-Path $p)) { return [Reflection.Assembly]::LoadFrom($p) }
        return $null
    }
    [AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
    foreach ($n in 'Bonsai.Core', 'Bonsai') { $null = [Reflection.Assembly]::LoadFrom($map[$n]) }
    if ($map['Extensions']) { $null = [Reflection.Assembly]::LoadFrom($map['Extensions']) }
    # Every assembly the workflow's namespaces name, loaded first so the serializer finds them.
    foreach ($m in [regex]::Matches((Get-Content $workflow -Raw), 'assembly=([A-Za-z0-9\.]+)"')) {
        $null = [Reflection.Assembly]::Load($m.Groups[1].Value)
    }
    # Bonsai opens a workflow from its own folder, and relative include paths
    # (Extensions\Foo.bonsai) are found from there.
    Set-Location (Split-Path $workflow); [Environment]::CurrentDirectory = (Split-Path $workflow)
    $reader = [Xml.XmlReader]::Create($workflow)
    try { $wb = [Bonsai.WorkflowBuilder]::Serializer.Deserialize($reader) } finally { $reader.Close() }
    try {
        $null = [Bonsai.Expressions.ExpressionBuilderGraphExtensions]::Build($wb.Workflow)
    } catch {
        $x = $_.Exception
        while ($x) { "  $($x.GetType().Name): $($x.Message)"; $x = $x.InnerException }
        exit 1
    }
    exit 0
}

if (-not (Test-Path (Join-Path $bonsaiDir 'Packages'))) { throw "No .bonsai\Packages in $Repo. Run its .bonsai\Setup.ps1 first." }
Remove-Item $bin -Recurse -Force -ErrorAction SilentlyContinue
$extensions = Join-Path $Repo 'src\Extensions.csproj'
if (Test-Path $extensions) {
    "1. Compiling src\Extensions as C# 5..."
    # Bonsai compiles the Extensions folder beside each workflow with that folder's own
    # Extensions.csproj, so a folder under src with a project of its own is not part of this
    # build (av-dome's GammaCalibrationFromBonvision needs MathNet, which src's project does
    # not list; 4 Nov 2026). Setting the property replaces the SDK's hidden-folder exclude, so
    # that is kept. Quoted as \"...\" for Windows PowerShell 5.1, which runs this script.
    $src = Join-Path $Repo 'src'
    $nested = @(Get-ChildItem $src -Recurse -Filter *.csproj | Where-Object { $_.DirectoryName -ne $src } |
        ForEach-Object { ($_.DirectoryName.Substring($src.Length + 1) -replace '\\', '/') + '/**' } | Select-Object -Unique)
    $exclude = '-p:DefaultItemExcludesInProjectFolder=\"' + ((@('**/.*/**') + $nested) -join ';') + '\"'
    $log = & { $ErrorActionPreference = 'Continue'; & dotnet build $extensions -o $bin -nologo -v q -p:LangVersion=5 $exclude `
        --source https://api.nuget.org/v3/index.json --source (Join-Path $Repo 'local_packages') 2>&1 | ForEach-Object { "$_" } }
    if ($LASTEXITCODE -ne 0) { $log | Where-Object { $_ -match 'error' } | Select-Object -Unique | ForEach-Object { "  $_" }; "  FAILED"; exit 1 }
    "  ok"
} else { "1. No src\Extensions to compile." }
"2. Building src\main.bonsai with Bonsai's builders..."
& { $ErrorActionPreference = 'Continue'; & powershell -NoProfile -ExecutionPolicy Bypass -File $self -Repo $Repo -Step Build 2>&1 | ForEach-Object { "$_" } }
if ($LASTEXITCODE -ne 0) { "  FAILED"; "check-bonsai: the workflow does not build."; exit 1 }
"  ok"
"check-bonsai: both steps passed."
