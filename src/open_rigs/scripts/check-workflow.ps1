param(
    # The workflow repository to check: .bonsai\ with its packages restored, src\main.bonsai, src\Extensions.
    [Parameter(Mandatory)][string]$Repo,
    # The workflow in src to check (a QC repo has several, one per rig kind); its layout is config\<stem>.layout.
    [string]$Workflow = 'main.bonsai',
    # Internal: the step this script runs in a fresh process of its own (assemblies stay loaded).
    [ValidateSet('', 'Build', 'Checks')][string]$Step = ''
)
# The lab's check of a workflow made of the lab's device pieces, one copy for every such workflow: each one's scripts\check-workflow.ps1
# runs this, from the open-rigs it pins (.venv\Lib\site-packages\open_rigs\scripts), on itself. Without the rig and
# without opening Bonsai: no window, no device.
#   1. Compiles src\Extensions under C# 5, the language Bonsai compiles it with (a workflow with no src\Extensions.csproj has no extensions to build).
#   2. Runs tests\Extensions.Tests.
#   3. Builds src\<Workflow> (main.bonsai unless -Workflow names another) with Bonsai's own expression builders, as Bonsai does at Start before it subscribes
#      anything, against the lab's rig template (open_rigs\rigs\rig-template.yml) filled in as a rig with exactly the devices the workflow's pieces drive (workflow_lint.filled_rig), and the lint's hardware rules: every type,
#      selector, include, subject and mapping; what RecordHarpDevices made from rig.yml, what ExperimentManifest
#      expects, the trial row's columns against schemas\trial-columns.yml, and each window in config\<its stem>.layout.
#      Skipped, and said, on a machine without the Spinnaker SDK (a CI runner). A workflow that includes NoExperimentFolder (a QC
#      calibration, a preflight) records nothing, so it is built against the filled template and linted all the same, but the Harp records,
#      the manifest's expectations and the trial row's columns are skipped, and said.
#   4. Each config\task*.yml reads as the workflow reads it at Go, TaskPath is exposed (neither for a NoExperimentFolder workflow, which
#      reads no generated TaskLogic), and every key in the template reaches the Settings_Rig JSON the experiment logs.
# Needs the environment set up (.bonsai\Setup.ps1: .bonsai\Packages). Exit 0 when all pass.
$ErrorActionPreference = 'Stop'
$self = $MyInvocation.MyCommand.Path
$Repo = (Resolve-Path $Repo).Path
$workflowFile = Join-Path $Repo "src\$Workflow"
if (-not (Test-Path $workflowFile -PathType Leaf)) { "check-workflow: $Repo has no src\$Workflow."; exit 1 }
# A workflow that includes NoExperimentFolder makes no experiment folder: no Harp records, manifest or trial log.
$noRecords = (Get-Content $workflowFile -Raw).Contains('Path="OpenRigs.Logging:NoExperimentFolder.bonsai"')
$layoutFile = Join-Path $Repo ("config\" + [IO.Path]::GetFileNameWithoutExtension($Workflow) + ".layout")
$bin = Join-Path ([IO.Path]::GetTempPath()) ('check-workflow-' + (Split-Path $Repo -Leaf))
$bonsaiDir = Join-Path $Repo '.bonsai'
$template = Join-Path (Split-Path -Parent (Split-Path -Parent $self)) 'rigs\rig-template.yml'

# A folder keeps the package's own version, which NuGet may have normalised in Bonsai.config (openal.redist.2.0.7.0 for
# 2.0.7), so trailing .0 parts are not compared.
function Get-NormalVersion([string]$v) { $parts = $v -split '-', 2; ($parts[0] -replace '(\.0)+$', '') + $(if ($parts.Count -gt 1) { '-' + $parts[1] }) }

# Each package Bonsai.config names: its id, the version it names, its folder in .bonsai\Packages at that version ($null
# when there is none) and every version of it there.
function Get-Packages {
    $dirs = @(Get-ChildItem (Join-Path $bonsaiDir 'Packages') -Directory)
    foreach ($p in ([xml](Get-Content (Join-Path $bonsaiDir 'Bonsai.config') -Raw)).PackageConfiguration.Packages.Package) {
        $prefix = $p.id + '.'
        $versions = @($dirs | Where-Object { $_.Name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and $_.Name.Substring($prefix.Length) -match '^\d' })
        [pscustomobject]@{
            Id      = $p.id
            Version = $p.version
            Folder  = $versions | Where-Object { (Get-NormalVersion $_.Name.Substring($prefix.Length)) -eq (Get-NormalVersion $p.version) } | Select-Object -First 1 -ExpandProperty FullName
            Found   = @($versions | ForEach-Object { $_.Name.Substring($prefix.Length) })
        }
    }
}

# An assembly Bonsai.config does not locate, from the package folders at the versions it names: never another version
# left in .bonsai\Packages.
function Find-PackageAssembly([string[]]$folders, [string]$name) {
    $folders | ForEach-Object { Get-ChildItem $_ -Recurse -Filter "$name.dll" -ErrorAction SilentlyContinue } |
        Where-Object { $_.FullName -match '\\lib\\net4' } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}

function Initialize-Assemblies {
    [xml]$cfg = Get-Content (Join-Path $bonsaiDir 'Bonsai.config') -Raw
    $map = @{}
    foreach ($a in $cfg.PackageConfiguration.AssemblyLocations.AssemblyLocation) {
        if ($a.processorArchitecture -in 'MSIL', 'Amd64') { $map[$a.assemblyName] = Join-Path $bonsaiDir $a.location }
    }
    # The Extensions build's own copies, when there is a build (none without src\Extensions.csproj: the packages' are used).
    $map['Extensions'] = Join-Path $bin 'Extensions.dll'
    foreach ($n in 'YamlDotNet', 'Newtonsoft.Json') { if (Test-Path (Join-Path $bin "$n.dll")) { $map[$n] = Join-Path $bin "$n.dll" } }
    # Native libraries (OpenCV, libgit2) from the folders Bonsai.config names for x64.
    foreach ($f in $cfg.PackageConfiguration.LibraryFolders.LibraryFolder) {
        if ($f.platform -eq 'x64') { $env:PATH = (Join-Path $bonsaiDir $f.path) + ';' + $env:PATH }
    }
    $script:resolveMap = $map; $script:resolveFolders = @(Get-Packages | Where-Object { $_.Folder } | ForEach-Object { $_.Folder })
    $script:resolver = [ResolveEventHandler]{
        param($s, $e)
        $n = (New-Object Reflection.AssemblyName $e.Name).Name
        $loaded = [AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq $n } | Select-Object -First 1
        if ($loaded) { return $loaded }
        $p = $script:resolveMap[$n]
        if (-not $p -or -not (Test-Path $p)) { $p = Find-PackageAssembly $script:resolveFolders $n }
        if ($p -and (Test-Path $p)) { return [Reflection.Assembly]::LoadFrom($p) }
        return $null
    }
    [AppDomain]::CurrentDomain.add_AssemblyResolve($script:resolver)
    foreach ($n in 'Bonsai.Core', 'Bonsai', 'Extensions', 'OpenRigs.Devices') { if (Test-Path $map[$n]) { $null = [Reflection.Assembly]::LoadFrom($map[$n]) } }
}

# The template filled in as a rig with exactly the workflow's devices (workflow_lint.filled_rig, from its pieces), at
# <bin>\config\<MACHINE>\rig.yml, so the nodes that read rig.yml when the workflow is built make what they make on such
# a rig; this machine need not have one. Sets $script:plan: the Harp records and files the build should make.
function Write-FilledTemplate {
    $out = & { $ErrorActionPreference = 'Continue'; & uv run --project $Repo -q python -m open_rigs.rigs.workflow_lint $Repo (Join-Path $bin 'config') ([Environment]::MachineName) $Workflow 2>&1 | ForEach-Object { "$_" } }
    if ($LASTEXITCODE -ne 0) { throw "check-workflow: workflow_lint could not fill the rig template: $($out -join ' ')" }
    $script:plan = ($out | Select-Object -Last 1) | ConvertFrom-Json
    $rigYml = Join-Path $bin "config\$([Environment]::MachineName)\rig.yml"
    if ([IO.File]::ReadAllText($rigYml).Contains('CHANGE-ME')) { throw "check-workflow: the filled template still holds CHANGE-ME; update workflow_lint.filled_rig." }
    return $rigYml
}

if ($Step -eq 'Build') {
    Initialize-Assemblies
    $workflowPath = $workflowFile
    foreach ($m in [regex]::Matches((Get-Content $workflowPath -Raw), 'assembly=([A-Za-z0-9\.]+)"')) {
        $null = [Reflection.Assembly]::Load($m.Groups[1].Value)
    }
    # Bonsai opens a workflow from its own folder, and relative include paths are found from there.
    Set-Location (Split-Path $workflowPath); [Environment]::CurrentDirectory = (Split-Path $workflowPath)
    $reader = [Xml.XmlReader]::Create($workflowPath)
    try { $wb = [Bonsai.WorkflowBuilder]::Serializer.Deserialize($reader) } finally { $reader.Close() }
    $rigYml = Write-FilledTemplate
    $configRoot = Join-Path $bin 'config'
    # Every node's own copy of where rig.yml is (ResolveRigConfigDirectory, RecordHarpDevices, StreamDevices;
    # ExperimentManifest's ConfigFiles) is typed by hand into the workflow: they must all be the same root, and
    # each is pointed at the filled template for this build.
    $script:roots = New-Object System.Collections.Generic.List[string]
    function Redirect-ConfigRoots($graph, [bool]$record = $true) {
        foreach ($n in $graph) {
            $b = [Bonsai.Expressions.ExpressionBuilder]::Unwrap($n.Value)
            $owner = if ($b -is [Bonsai.Expressions.CombinatorBuilder]) { $b.Combinator } else { $b }
            # A node's inner Settings node mirrors its ConfigRoot and is already redirected with it.
            if ($owner -and $owner.GetType().Namespace -like 'OpenRigs.*' -and $owner.GetType().Name -notlike '*Settings' -and $owner.PSObject.Properties['ConfigRoot']) {
                if ($record) { $script:roots.Add("$($owner.GetType().Name)=$($owner.ConfigRoot)") }; $owner.ConfigRoot = $configRoot
            }
            if ($b.GetType().Name -eq 'ExperimentManifest') {
                foreach ($file in $b.ConfigFiles -split ';') { if ($file -match '^(.*)\\%COMPUTERNAME%\\rig\.yml$') { $script:roots.Add("ExperimentManifest.ConfigFiles=$($Matches[1])") } }
                $b.ConfigFiles = $rigYml
            }
            # An include's workflow is not loaded before the build: its ConfigRoot is the include's own property, kept as XML until then.
            # One that takes ConfigRoot is loaded now (an embedded include is not reloaded at the build) and redirected inside,
            # its nodes' own roots not counted, as they are the include's or set from ReadLabRig's subject: ExperimentRecords'
            # RecordHarpDevices and ExperimentManifest.
            if ($b -is [Bonsai.Expressions.IncludeWorkflowBuilder]) {
                $p = [ComponentModel.TypeDescriptor]::GetProperties($b)['ConfigRoot']
                if ($p) {
                    $v = $p.GetValue($b); if ($v -is [Xml.Linq.XElement]) { $v = $v.Value }
                    if ($record) { $script:roots.Add("$($b.Path)=$v") }
                    $null = $b.ArgumentRange
                    if ($b.Workflow) { Redirect-ConfigRoots $b.Workflow $false }
                    continue
                }
            }
            if ($b -is [Bonsai.Expressions.IWorkflowExpressionBuilder] -and $b.Workflow) { Redirect-ConfigRoots $b.Workflow }
        }
    }
    Redirect-ConfigRoots $wb.Workflow
    $failed = 0
    $distinct = @($script:roots | ForEach-Object { ($_ -split '=', 2)[1].TrimEnd('\') } | Sort-Object -Unique)
    if ($script:roots.Count -ge 1 -and $distinct.Count -eq 1) { "  ok    every node reads rig.yml under the same root, $($distinct[0]) ($($script:roots.Count) nodes)" }
    else { "  FAIL  the nodes' rig.yml roots differ, or none was found: $($script:roots -join '; ')"; $failed++ }
    try {
        $built = [Bonsai.Expressions.ExpressionBuilderGraphExtensions]::Build($wb.Workflow)
    } catch {
        $x = $_.Exception
        while ($x) { $b = $null; try { $b = $x.Builder } catch {}; $nm = if ($b) { [Bonsai.Expressions.ExpressionBuilder]::GetElementDisplayName($b) } else { '' }; "  FAIL  $($x.GetType().Name): $nm :: $($x.Message)"; $x = $x.InnerException }
        exit 1
    }
    "  ok    $Workflow builds"
    # What the build made, read from the objects in the built expression.
    Add-Type -ReferencedAssemblies System.Core -TypeDefinition @'
public class BuiltObjects : System.Linq.Expressions.ExpressionVisitor {
    public System.Collections.Generic.List<object> Found = new System.Collections.Generic.List<object>();
    protected override System.Linq.Expressions.Expression VisitConstant(System.Linq.Expressions.ConstantExpression node) {
        if (node.Value != null) Found.Add(node.Value);
        return base.VisitConstant(node);
    }
}
'@
    $objects = New-Object BuiltObjects
    $null = $objects.Visit($built)
    foreach ($p in $script:plan.problems) { "  FAIL  $p"; $failed++ }
    if ($noRecords) {
        "  ok    NoExperimentFolder: the workflow records nothing, so no RecordHarpDevices, ExperimentManifest or trial log to check"
        exit $(if ($failed) { 1 } else { 0 })
    }
    $records = @($objects.Found | Where-Object { $_.GetType().FullName -eq 'OpenRigs.HarpDevices.RecordHarpDevice' })
    $got = @($records | ForEach-Object { "$($_.Name) $($_.PortName) $($_.UsbSerial) $($_.ExpectedWhoAmI)" } | Sort-Object)
    $want = @($script:plan.records | Sort-Object)
    if (($got -join '; ') -eq ($want -join '; ')) { "  ok    RecordHarpDevices records the workflow's $($want.Count) Harp devices from rig.yml" }
    else { "  FAIL  RecordHarpDevices made: $($got -join '; '); the workflow's pieces drive: $($want -join '; ')"; $failed++ }
    $dumps = @($objects.Found | Where-Object { $_.GetType().FullName -eq 'OpenRigs.HarpDevices.RequestRegisterDump' }).Count
    if ($dumps -eq $want.Count) { "  ok    ... and asks each for its dump on <Name>.Commands" } else { "  FAIL  $dumps dump request(s), not $($want.Count): is every board's command subject <Name>.Commands?"; $failed++ }
    $recorder = @($objects.Found | Where-Object { $_.GetType().Name -eq 'ManifestRecorder' })
    $planField = if ($recorder.Count) { $recorder[0].GetType().GetFields([Reflection.BindingFlags]'Instance,NonPublic,Public') | Where-Object { $_.FieldType.Name -eq 'ExpectedPlan' } | Select-Object -First 1 }
    # The devices' folders and the Harp records, from rig.yml; each log's entry (ending _) is workflow_lint.expected_files_problems' to check.
    $expected = if ($planField) { @($planField.GetValue($recorder[0]).Entries | ForEach-Object { $_.Path } | Where-Object { -not $_.EndsWith('_') }) } else { @() }
    $wantFiles = @($script:plan.files)
    $missing = @($wantFiles | Where-Object { $expected -notcontains $_ })
    $extra = @($expected | Where-Object { $wantFiles -notcontains $_ })
    # A condition (path?Subject) that is not a bool subject published before ExperimentManifest is expected always, with this note.
    $unresolved = if ($planField) { @($planField.GetValue($recorder[0]).Notes | Where-Object { $_ -like 'Condition * expected always.' }) } else { @() }
    if ($unresolved.Count -eq 0) { "  ok    every condition in ExpectedFiles is a bool subject the workflow publishes" }
    else { $unresolved | ForEach-Object { "  FAIL  $_" }; $failed++ }
    if ($missing.Count -eq 0 -and $extra.Count -eq 0) { "  ok    ExperimentManifest expects the $($expected.Count) device folders and Harp records of an experiment" }
    else {
        "  FAIL  ExperimentManifest expects '$($expected -join ', ')'; missing: $($missing -join ', '); extra: $($extra -join ', ')"; $failed++
        if ($planField) { foreach ($note in $planField.GetValue($recorder[0]).Notes) { "        its note: $note" } }
    }
    # The trial row's columns, as LogTrial's writer names them, against schemas\trial-columns.yml.
    $rowType = [AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq 'Extensions' } | ForEach-Object { $_.GetType('TrialRow') } | Select-Object -First 1
    if ($rowType) {
        $columns = [OpenRigs.Logging.CsvWriter]::ColumnNames([Bonsai.Harp.Timestamped`1].MakeGenericType($rowType))
        $schema = @((Get-Content (Join-Path $Repo 'schemas\trial-columns.yml')) | Where-Object { $_ -match '^([A-Za-z]\w*):\s*$' } | ForEach-Object { $Matches[1] })
        if (($columns -join ',') -eq ($schema -join ',')) { "  ok    BonsaiData_Trial's $($columns.Count) columns are schemas\trial-columns.yml's, in its order" }
        else { "  FAIL  BonsaiData_Trial writes $($columns -join ','); schemas\trial-columns.yml lists $($schema -join ',')"; $failed++ }
    } elseif ((Get-Content $workflowFile -Raw).Contains('OpenRigs.Logging:LogTrial.bonsai')) { "  FAIL  src\Extensions has no TrialRow, the trial log's row"; $failed++ }
    else { "  ok    no trial log (the workflow includes no LogTrial)" }
    # config\main.layout's windows, on the nodes as the editor has them: Bonsai loads each group in topological
    # order, not file order, so a node fed from later in the file moves up and shifts the indices after it. A window
    # on the wrong node makes the editor offer to delete the layout, and --start waits. A visualizer type not in
    # $wanted fails until it is added with the output type it needs ('' for any).
    $layoutPath = $layoutFile
    if (Test-Path $layoutPath) {
        $inspectable = [Bonsai.Expressions.ExpressionBuilderGraphExtensions]::ToInspectableGraph($wb.Workflow)
        $null = [Bonsai.Expressions.ExpressionBuilderGraphExtensions]::Build($inspectable)
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
                    if ($null -eq $type) { "  FAIL  layout $at`: $($window.VisualizerTypeName) is not one this check knows" }
                    elseif ($type -and "$($node.ObservableType)" -ne $type) { "  FAIL  layout $at`: $($window.VisualizerTypeName) on a node of $($node.ObservableType), not $type" }
                    else { "  ok    layout window $($window.VisualizerTypeName) on $at" }
                }
            }
        }
        [xml]$layout = Get-Content $layoutPath -Raw
        $layoutLines = @(Test-Layout $layout.VisualizerLayout $inspectable '')
        $layoutLines
        $failed += @($layoutLines | Where-Object { $_ -like '  FAIL*' }).Count
    } else { "  ok    no config\$(Split-Path $layoutFile -Leaf); nothing to check" }
    exit $(if ($failed) { 1 } else { 0 })
}

if ($Step -eq 'Checks') {
    Initialize-Assemblies
    foreach ($p in $script:resolveMap.Values) { if (Test-Path $p) { try { $null = [Reflection.Assembly]::LoadFrom($p) } catch {} } }
    # The resolver goes (a script block resolving on an Rx thread deadlocks) unless there is no Extensions build to
    # supply YamlDotNet at the version OpenRigs.Devices binds to: then the package's is resolved to it.
    if (Test-Path (Join-Path $bin 'YamlDotNet.dll')) { [AppDomain]::CurrentDomain.remove_AssemblyResolve($script:resolver) }
    $failed = 0
    # Each task file as the workflow reads it at Go (the generated deserializer: no unknown key).
    $taskType = [AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq 'Extensions' } |
        ForEach-Object { $_.GetTypes() } | Where-Object { $_.IsPublic -and $_.Name -like '*TaskLogic' -and $_.Namespace -like '*Task' } | Select-Object -First 1
    # A workflow with NoExperimentFolder has no generated TaskLogic: its task file, if any, is read by an include.
    $taskFiles = if ($noRecords) { @() } else { @(Get-ChildItem (Join-Path $Repo 'config') -Filter 'task*.yml' -ErrorAction SilentlyContinue) }
    if (-not $taskType -and $taskFiles.Count) { "  FAIL  src\Extensions has no generated <Name>Task.<Name>TaskLogic"; exit 1 }
    if ($noRecords) { "  ok    NoExperimentFolder: no generated TaskLogic, so no task file read here" }
    elseif (-not $taskFiles.Count) { "  ok    no task file (the workflow takes none)" }
    foreach ($file in $taskFiles) {
        try {
            $d = (New-Object YamlDotNet.Serialization.DeserializerBuilder).Build()
            $c = $d.Deserialize((New-Object IO.StringReader ([IO.File]::ReadAllText($file.FullName))), $taskType)
            # A task with training stages needs a row; a task with none has no such property.
            $stages = $c.TaskParameters.PSObject.Properties['TrainingStages']
            if ($null -eq $c.TaskParameters) { "  FAIL  config\$($file.Name) has no taskParameters"; $failed++ }
            elseif ($stages -and $stages.Value.Count -eq 0) { "  FAIL  config\$($file.Name) has no trainingStages"; $failed++ }
            else { "  ok    config\$($file.Name) reads as the workflow reads it at Go" }
        } catch { $x = $_.Exception; while ($x.InnerException) { $x = $x.InnerException }; "  FAIL  config\$($file.Name): $($x.Message)"; $failed++ }
    }
    $wf = Get-Content $workflowFile -Raw
    if ($wf -match '<Property Name="TaskPath"') { "  ok    $Workflow exposes TaskPath" }
    elseif ($taskFiles.Count) { "  FAIL  $Workflow does not expose TaskPath"; $failed++ }
    $yaml = [IO.File]::ReadAllText((Write-FilledTemplate))
    $E = [Linq.Expressions.Expression]
    $wait = [System.Reactive.Linq.Observable].GetMethods() | Where-Object { $_.Name -eq 'Wait' -and $_.GetParameters().Count -eq 1 } | Select-Object -First 1
    # The experiment's Settings_Rig JSON is rig.yml as parsed (OpenRigs.Devices.Rigs, the lab rig model's classes),
    # and the manifest keeps only rig.yml's hash, so every key in rig.yml must reach the JSON.
    try {
        $x = $E::Constant([System.Reactive.Linq.Observable]::Return($yaml), [IObservable[string]])
        $des = New-Object OpenRigs.Devices.Rigs.DeserializeFromYaml
        $des.Type = [Activator]::CreateInstance([Bonsai.Expressions.TypeMapping`1].MakeGenericType([OpenRigs.Devices.Rigs.LabRig]))
        $x = $des.Build([Linq.Expressions.Expression[]]@($x))
        $cb = New-Object Bonsai.Expressions.CombinatorBuilder
        $ser = New-Object OpenRigs.Devices.Rigs.SerializeToJson; $ser.Formatting = 'Indented'; $cb.Combinator = $ser
        $x = $cb.Build([Linq.Expressions.Expression[]]@($x))
        $json = $E::Lambda($E::Call($wait.MakeGenericMethod([string]), $x)).Compile().DynamicInvoke()
        $jsonKeys = New-Object System.Collections.Generic.List[string]
        foreach ($t in [Newtonsoft.Json.Linq.JToken]::Parse($json).Descendants()) {
            if ($t -is [Newtonsoft.Json.Linq.JProperty]) { $jsonKeys.Add(($t.Path -replace '\[\d+\]', '[]')) }
        }
        $yamlKeys = New-Object System.Collections.Generic.List[string]
        function Add-YamlKeys($node, [string]$at) {
            if ($node -is [System.Collections.IDictionary]) { foreach ($k in $node.Keys) { $p = if ($at) { "$at.$k" } else { "$k" }; $yamlKeys.Add($p); Add-YamlKeys $node[$k] $p } }
            elseif ($node -is [System.Collections.IList]) { foreach ($i in $node) { Add-YamlKeys $i "$at[]" } }
        }
        Add-YamlKeys ((New-Object YamlDotNet.Serialization.Deserializer).Deserialize((New-Object IO.StringReader $yaml))) ''
        $lost = @($yamlKeys | Where-Object { $jsonKeys -notcontains $_ })
        if ($yamlKeys.Count -gt 0 -and $lost.Count -eq 0) { "  ok    Settings_Rig JSON keeps all $($yamlKeys.Count) keys of the lab's rig template" }
        else { "  FAIL  Settings_Rig JSON lost $($lost.Count) of $($yamlKeys.Count) keys of the lab's rig template: $($lost -join ', ')"; $failed++ }
    } catch { $x = $_.Exception; while ($x.InnerException) { $x = $x.InnerException }; "  FAIL  Settings_Rig JSON: $($x.Message)"; $failed++ }
    exit $(if ($failed) { 1 } else { 0 })
}

if (-not (Test-Path (Join-Path $bonsaiDir 'Packages'))) { throw "No .bonsai\Packages in $Repo. Run .bonsai\Setup.ps1 first." }
# Every package Bonsai.config names, at its version, in .bonsai\Packages: a Bonsai.config moved on since the last setup
# otherwise fails the build only as "proxy for the unknown type".
$stale = @(Get-Packages | Where-Object { -not $_.Folder })
foreach ($p in $stale) {
    "check-workflow: .bonsai\Packages lacks $($p.Id) $($p.Version), the version Bonsai.config names (found: $(if ($p.Found.Count) { $p.Found -join ', ' } else { 'none' })). Run .bonsai\Setup.ps1."
}
if ($stale.Count) { exit 1 }
if (-not (Test-Path $template)) { throw "No lab rig template at $template." }
# NuGet sources: nuget.org, local_packages and every local folder .bonsai\NuGet.config lists, so a lab package new
# to this machine's NuGet cache is found.
$sources = @('https://api.nuget.org/v3/index.json', (Join-Path $Repo 'local_packages'))
$nugetConfig = Join-Path $bonsaiDir 'NuGet.config'
if (Test-Path $nugetConfig) {
    foreach ($a in ([xml](Get-Content $nugetConfig -Raw)).configuration.packageSources.add) {
        $v = $a.value; if ($v -notmatch '^https?:') { $sources += $(if ([IO.Path]::IsPathRooted($v)) { $v } else { [IO.Path]::GetFullPath((Join-Path $bonsaiDir $v)) }) }
    }
}
$sourceArgs = @($sources | Select-Object -Unique | ForEach-Object { '--source'; $_ })
$bad = 0
"1. Compiling src\Extensions as C# 5..."
Remove-Item $bin -Recurse -Force -ErrorAction SilentlyContinue
$null = New-Item -ItemType Directory $bin -Force
if (-not (Test-Path (Join-Path $Repo 'src\Extensions.csproj'))) { "  ok    no extensions to build (no src\Extensions.csproj)" }
else {
    $log = & { $ErrorActionPreference = 'Continue'; & dotnet build (Join-Path $Repo 'src\Extensions.csproj') -o $bin -nologo -v q -p:LangVersion=5 @sourceArgs 2>&1 | ForEach-Object { "$_" } }
    if ($LASTEXITCODE -ne 0) { $log | Where-Object { $_ -match 'error' } | Select-Object -Unique | ForEach-Object { "  $_" }; "  FAILED"; exit 1 }
    "  ok"
}
"2. tests\Extensions.Tests..."
$tests = Join-Path $Repo 'tests\Extensions.Tests'
if (-not (Test-Path $tests)) { "  ok    none (src\Extensions holds no task logic)" }
else {
    & { $ErrorActionPreference = 'Continue'; & dotnet restore $tests -v q @sourceArgs 2>&1 | Out-Null }
    $log = & { $ErrorActionPreference = 'Continue'; & dotnet test $tests --no-restore -nologo 2>&1 | ForEach-Object { "$_" } }
    $testExit = $LASTEXITCODE
    $log | Where-Object { $_ -match 'Passed!|Failed|error' } | ForEach-Object { "  $_" }
    if ($testExit -ne 0) { "  FAILED"; $bad++ }
}
"3. Building $Workflow with Bonsai's builders, against the filled template..."
# The camera pieces load SpinnakerNET, which needs the Spinnaker SDK's native Spinnaker_v140.dll on PATH. The SDK
# is a login-gated download, so a GitHub runner has none and main.bonsai cannot be read there ("There is an error in
# XML document" at the camera include's first property; CI, 15 Nov 2026). Skipped only then; every rig and lab
# machine with the SDK runs it, as update-workflows.ps1 does before each push.
$spinnaker = @($env:Path -split ';' | Where-Object { $_ -and (Test-Path (Join-Path $_ 'Spinnaker_v140.dll')) })
if (-not $spinnaker.Count) { "  SKIPPED: no Spinnaker SDK (Spinnaker_v140.dll on PATH), so $Workflow cannot be built here" }
else {
    & { $ErrorActionPreference = 'Continue'; & powershell -NoProfile -ExecutionPolicy Bypass -File $self -Repo $Repo -Workflow $Workflow -Step Build 2>&1 | ForEach-Object { "$_" } }
    if ($LASTEXITCODE -ne 0) { "  FAILED"; $bad++ }
}
"4. The task files, the launch properties and Settings_Rig..."
& { $ErrorActionPreference = 'Continue'; & powershell -NoProfile -ExecutionPolicy Bypass -File $self -Repo $Repo -Workflow $Workflow -Step Checks 2>&1 | ForEach-Object { "$_" } }
if ($LASTEXITCODE -ne 0) { "  FAILED"; $bad++ }
if ($bad) { "check-workflow: $bad step(s) failed."; exit 1 }
if (-not $spinnaker.Count) { "check-workflow: steps 1, 2 and 4 passed; step 3 skipped (no Spinnaker SDK)." }
else { "check-workflow: all 4 steps passed." }
