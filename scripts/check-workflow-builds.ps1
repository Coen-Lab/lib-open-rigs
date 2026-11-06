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
#      before it subscribes anything: every type, selector, include, subject and mapping; and,
#      when the workflow tracks config\main.layout, that each window sits on a node it fits.
# The names a workflow uses are checked where it runs, not here (DECISIONS.md, stage 2):
# rig-control's scripts\check-workflow-names.ps1, by rig-setup when a rig takes a new version
# and by rig-control's agent before each launch, against the lab's lists on the share.
# It cannot check hardware, the visualizers themselves or the data a run writes. Exit 0 when
# both pass.
$ErrorActionPreference = 'Stop'
$self = $MyInvocation.MyCommand.Path
$Repo = (Resolve-Path $Repo).Path
$bonsaiDir = Join-Path $Repo '.bonsai'
$bin = Join-Path ([IO.Path]::GetTempPath()) ("check-workflow-builds-" + (Split-Path $Repo -Leaf))
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
    $inspectable = [Bonsai.Expressions.ExpressionBuilderGraphExtensions]::ToInspectableGraph($wb.Workflow)
    $null = [Bonsai.Expressions.ExpressionBuilderGraphExtensions]::Build($inspectable)

    # config\main.layout's windows, on the nodes as the editor has them (the lab's rule: every
    # open-rigs workflow tracks its layout there and .bonsai\Setup.ps1 copies it in). Bonsai loads
    # each group in topological order, not file order, so a restructure shifts window indices; a
    # window on a missing node or a wrong type makes the editor offer to delete the layout, and
    # --start waits. Visualizer types this check does not know are listed, not failed.
    $layoutPath = Join-Path $Repo 'config\main.layout'
    if (-not (Test-Path $layoutPath)) { exit 0 }
    $wanted = @{ 'Bonsai.Vision.Design.IplImageVisualizer' = 'OpenCV.Net.IplImage'; 'Bonsai.Design.ObjectTextVisualizer' = '' }
    function Test-Layout($settings, $graph, $where) {
        $nodes = @($graph)
        foreach ($window in $settings.WindowSettings) {
            $i = [int]$window.Index
            if ($i -ge $nodes.Count) { "  FAIL  layout $where$i`: the workflow has $($nodes.Count) nodes there"; continue }
            $node = $nodes[$i].Value
            $at = "$where$i ($($node.Builder.GetType().Name))"
            if ($window.NestedLayout) {
                if ($node.Builder -is [Bonsai.Expressions.IWorkflowExpressionBuilder]) { Test-Layout $window.NestedLayout $node.Builder.Workflow "$at > " }
                else { "  FAIL  layout $at holds windows but is not a group" }
            }
            if ($window.VisualizerTypeName) {
                $type = $wanted[$window.VisualizerTypeName]
                if ($null -eq $type) { "  note  layout $at`: $($window.VisualizerTypeName), a type this check does not know, on a node of $($node.ObservableType)" }
                elseif ($type -and "$($node.ObservableType)" -ne $type) { "  FAIL  layout $at`: $($window.VisualizerTypeName) on a node of $($node.ObservableType), not $type" }
                else { "  ok    layout window $($window.VisualizerTypeName) on $at" }
            }
        }
    }
    [xml]$layout = Get-Content $layoutPath -Raw
    $lines = @(Test-Layout $layout.VisualizerLayout $inspectable '')
    $lines
    exit $(if (@($lines | Where-Object { $_ -like '  FAIL*' }).Count) { 2 } else { 0 })
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
        ForEach-Object { ($_.DirectoryName.Substring($src.Length + 1) -replace '\\', '/' -replace ' ', '%20') + '/**' } | Select-Object -Unique)
    # A space as %20, which MSBuild reads back as one: PowerShell 5.1 does not quote an
    # argument that already holds a quote, so a bare space split it in two (MSB1008).
    $exclude = '-p:DefaultItemExcludesInProjectFolder=\"' + ((@('**/.*/**') + $nested) -join ';') + '\"'
    $log = & { $ErrorActionPreference = 'Continue'; & dotnet build $extensions -o $bin -nologo -v q -p:LangVersion=5 $exclude `
        --source https://api.nuget.org/v3/index.json --source (Join-Path $Repo 'local_packages') 2>&1 | ForEach-Object { "$_" } }
    if ($LASTEXITCODE -ne 0) { $log | Where-Object { $_ -match 'error' } | Select-Object -Unique | ForEach-Object { "  $_" }; "  FAILED"; exit 1 }
    "  ok"
} else { "1. No src\Extensions to compile." }
"2. Building src\main.bonsai with Bonsai's builders..."
& { $ErrorActionPreference = 'Continue'; & powershell -NoProfile -ExecutionPolicy Bypass -File $self -Repo $Repo -Step Build 2>&1 | ForEach-Object { "$_" } }
if ($LASTEXITCODE -eq 2) { "  FAILED"; "check-workflow-builds: config\main.layout does not fit the workflow."; exit 1 }
if ($LASTEXITCODE -ne 0) { "  FAILED"; "check-workflow-builds: the workflow does not build."; exit 1 }
"  ok"
"check-workflow-builds: both steps passed."
