param(
    # The workflow repository to check: .bonsai\ with its packages restored, src\main.bonsai, src\Extensions.
    [Parameter(Mandatory)][string]$Repo,
    # Internal: the step this script runs in a fresh process of its own (assemblies stay loaded).
    [ValidateSet('', 'Build', 'Checks')][string]$Step = ''
)
# The lab's check of a workflow on RigHardware, one copy for every such workflow: each one's scripts\check-workflow.ps1
# runs this, from the open-rigs it pins (.venv\Lib\site-packages\open_rigs\scripts), on itself. Without the rig and
# without opening Bonsai: no window, no device.
#   1. Compiles src\Extensions under C# 5, the language Bonsai compiles it with.
#   2. Runs tests\Extensions.Tests.
#   3. Builds src\main.bonsai with Bonsai's own expression builders, as Bonsai does at Start before it subscribes
#      anything, against the lab's rig template (open_rigs\rigs\rig-template.yml) filled in as a rig's: every type,
#      selector, include, subject and mapping; what RecordHarpDevices made from rig.yml, what ExperimentManifest
#      expects, the trial row's columns against schemas\trial-columns.yml, and each window in config\main.layout.
#      Skipped, and said, on a machine without the Spinnaker SDK (a CI runner).
#   4. Each config\task*.yml reads as the workflow reads it at Go, TaskPath is exposed, and every key in the
#      template reaches the Settings_Rig JSON the experiment logs.
# Needs the environment set up (.bonsai\Setup.ps1: .bonsai\Packages). Exit 0 when all pass.
$ErrorActionPreference = 'Stop'
$self = $MyInvocation.MyCommand.Path
$Repo = (Resolve-Path $Repo).Path
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
    $map['Extensions'] = Join-Path $bin 'Extensions.dll'
    foreach ($n in 'YamlDotNet', 'Newtonsoft.Json') { $map[$n] = Join-Path $bin "$n.dll" }
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
        if (-not $p) { $p = Find-PackageAssembly $script:resolveFolders $n }
        if ($p -and (Test-Path $p)) { return [Reflection.Assembly]::LoadFrom($p) }
        return $null
    }
    [AppDomain]::CurrentDomain.add_AssemblyResolve($script:resolver)
    foreach ($n in 'Bonsai.Core', 'Bonsai', 'Extensions', 'OpenRigs.Devices') { $null = [Reflection.Assembly]::LoadFrom($map[$n]) }
}

# The template filled in as a rig's, at <bin>\config\<MACHINE>\rig.yml, so the nodes that read
# rig.yml when the workflow is built make what they make on a rig; this machine need not have one.
function Write-FilledTemplate {
    $rigDir = Join-Path $bin "config\$([Environment]::MachineName)"
    $null = New-Item -ItemType Directory -Path $rigDir -Force
    $rigYml = Join-Path $rigDir 'rig.yml'
    $text = [IO.File]::ReadAllText($template)
    $serials = 'BG01WA0E', 'AQ03WH45', 'AQ03WRBG', 'AQ03WW0D', 'E664C856C360B937', 'E664C856C31F4637'
    foreach ($s in $serials) { $text = ([regex]'usbSerial: CHANGE-ME').Replace($text, "usbSerial: $s", 1) }
    $text = $text.Replace('rigId: XX', 'rigId: D1').Replace('serialNumber: "00000000"', "serialNumber: '1'").Replace('rootPath: CHANGE-ME', "rootPath: $bin").Replace('RigConfigs\CHANGE-ME\', "RigConfigs\$([Environment]::MachineName)\")
    foreach ($left in 'CHANGE-ME', 'rigId: XX', 'serialNumber: "00000000"') {
        if ($text.Contains($left)) { throw "check-workflow: the template still holds '$left' after filling; update Write-FilledTemplate." }
    }
    [IO.File]::WriteAllText($rigYml, $text)
    return $rigYml
}

if ($Step -eq 'Build') {
    Initialize-Assemblies
    $workflowPath = Join-Path $Repo 'src\main.bonsai'
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
    function Redirect-ConfigRoots($graph) {
        foreach ($n in $graph) {
            $b = [Bonsai.Expressions.ExpressionBuilder]::Unwrap($n.Value)
            $owner = if ($b -is [Bonsai.Expressions.CombinatorBuilder]) { $b.Combinator } else { $b }
            # A node's inner Settings node mirrors its ConfigRoot and is already redirected with it.
            if ($owner -and $owner.GetType().Namespace -like 'OpenRigs.*' -and $owner.GetType().Name -notlike '*Settings' -and $owner.PSObject.Properties['ConfigRoot']) {
                $script:roots.Add("$($owner.GetType().Name)=$($owner.ConfigRoot)"); $owner.ConfigRoot = $configRoot
            }
            if ($b.GetType().Name -eq 'ExperimentManifest') {
                foreach ($file in $b.ConfigFiles -split ';') { if ($file -match '^(.*)\\%COMPUTERNAME%\\rig\.yml$') { $script:roots.Add("ExperimentManifest.ConfigFiles=$($Matches[1])") } }
                $b.ConfigFiles = $rigYml
            }
            # An include's workflow is not loaded before the build: its ConfigRoot is the include's own property, kept as XML until then.
            if ($b -is [Bonsai.Expressions.IncludeWorkflowBuilder]) {
                $p = [ComponentModel.TypeDescriptor]::GetProperties($b)['ConfigRoot']
                if ($p) { $v = $p.GetValue($b); if ($v -is [Xml.Linq.XElement]) { $v = $v.Value }; $script:roots.Add("$($b.Path)=$v") }
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
    "  ok    main.bonsai builds"
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
    $records = @($objects.Found | Where-Object { $_.GetType().FullName -eq 'OpenRigs.HarpDevices.RecordHarpDevice' })
    $got = @($records | ForEach-Object { "$($_.Name) $($_.PortName) $($_.UsbSerial) $($_.ExpectedWhoAmI)" } | Sort-Object)
    $want = @('BehaviorBoard-Main COM1 BG01WA0E 1216', 'LicketySplit-Left COM5 E664C856C360B937 1400', 'LicketySplit-Right COM6 E664C856C31F4637 1400', 'StepperDriver-A COM3 AQ03WRBG 1130', 'StepperDriver-B COM4 AQ03WW0D 1130', 'TimestampGeneratorGen3-Main COM2 AQ03WH45 1158')
    if (($got -join '; ') -eq ($want -join '; ')) { "  ok    RecordHarpDevices records the Behavior board, the timestamp generator, both stepper drivers and both Lickety Splits from rig.yml" }
    else { "  FAIL  RecordHarpDevices made: $($got -join '; ')"; $failed++ }
    $dumps = @($objects.Found | Where-Object { $_.GetType().FullName -eq 'OpenRigs.HarpDevices.RequestRegisterDump' }).Count
    if ($dumps -eq 6) { "  ok    ... and asks each for its dump on <Name>.Commands" } else { "  FAIL  $dumps dump request(s), not 6: is every board's command subject <Name>.Commands?"; $failed++ }
    $recorder = @($objects.Found | Where-Object { $_.GetType().Name -eq 'ManifestRecorder' })
    $planField = if ($recorder.Count) { $recorder[0].GetType().GetFields([Reflection.BindingFlags]'Instance,NonPublic,Public') | Where-Object { $_.FieldType.Name -eq 'ExpectedPlan' } | Select-Object -First 1 }
    # The devices' folders and the Harp records, from rig.yml; each log's entry (ending _) is workflow_lint.expected_files_problems' to check.
    $expected = if ($planField) { @($planField.GetValue($recorder[0]).Entries | ForEach-Object { $_.Path } | Where-Object { -not $_.EndsWith('_') }) } else { @() }
    $wantFiles = 'BehaviorBoard-Main/', 'TimestampGeneratorGen3-Main/', 'StepperDriver-A/', 'StepperDriver-B/', 'LicketySplit-Left/', 'LicketySplit-Right/', 'Camera-Face/', 'Camera-Pupil/', 'Camera-Body/',
        'BehaviorBoard-Main/BehaviorBoard-Main_device.json', 'BehaviorBoard-Main/BehaviorBoard-Main.yml',
        'TimestampGeneratorGen3-Main/TimestampGeneratorGen3-Main_device.json', 'TimestampGeneratorGen3-Main/TimestampGeneratorGen3-Main.yml',
        'StepperDriver-A/StepperDriver-A_device.json', 'StepperDriver-A/StepperDriver-A.yml',
        'StepperDriver-B/StepperDriver-B_device.json', 'StepperDriver-B/StepperDriver-B.yml',
        'LicketySplit-Left/LicketySplit-Left_device.json', 'LicketySplit-Right/LicketySplit-Right_device.json',
        'LicketySplit-Left/LicketySplit-Left.yml', 'LicketySplit-Right/LicketySplit-Right.yml'
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
    } else { "  FAIL  src\Extensions has no TrialRow, the trial log's row"; $failed++ }
    # config\main.layout's windows, on the nodes as the editor has them: Bonsai loads each group in topological
    # order, not file order, so a node fed from later in the file moves up and shifts the indices after it. A window
    # on the wrong node makes the editor offer to delete the layout, and --start waits. A visualizer type not in
    # $wanted fails until it is added with the output type it needs ('' for any).
    $layoutPath = Join-Path $Repo 'config\main.layout'
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
    } else { "  ok    no config\main.layout; nothing to check" }
    exit $(if ($failed) { 1 } else { 0 })
}

if ($Step -eq 'Checks') {
    Initialize-Assemblies
    foreach ($p in $script:resolveMap.Values) { if (Test-Path $p) { try { $null = [Reflection.Assembly]::LoadFrom($p) } catch {} } }
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($script:resolver)
    $failed = 0
    # Each task file as the workflow reads it at Go (the generated deserializer: no unknown key).
    $taskType = [AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq 'Extensions' } |
        ForEach-Object { $_.GetTypes() } | Where-Object { $_.IsPublic -and $_.Name -like '*TaskLogic' -and $_.Namespace -like '*Task' } | Select-Object -First 1
    if (-not $taskType) { "  FAIL  src\Extensions has no generated <Name>Task.<Name>TaskLogic"; exit 1 }
    foreach ($file in Get-ChildItem (Join-Path $Repo 'config') -Filter 'task*.yml') {
        try {
            $d = (New-Object YamlDotNet.Serialization.DeserializerBuilder).Build()
            $c = $d.Deserialize((New-Object IO.StringReader ([IO.File]::ReadAllText($file.FullName))), $taskType)
            if ($c.TaskParameters.TrainingStages.Count -gt 0) { "  ok    config\$($file.Name) reads as the workflow reads it at Go" }
            else { "  FAIL  config\$($file.Name) has no trainingStages"; $failed++ }
        } catch { $x = $_.Exception; while ($x.InnerException) { $x = $x.InnerException }; "  FAIL  config\$($file.Name): $($x.Message)"; $failed++ }
    }
    $wf = Get-Content (Join-Path $Repo 'src\main.bonsai') -Raw
    if ($wf -match '<Property Name="TaskPath"') { "  ok    main.bonsai exposes TaskPath" }
    else { "  FAIL  main.bonsai does not expose TaskPath"; $failed++ }
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
$log = & { $ErrorActionPreference = 'Continue'; & dotnet build (Join-Path $Repo 'src\Extensions.csproj') -o $bin -nologo -v q -p:LangVersion=5 @sourceArgs 2>&1 | ForEach-Object { "$_" } }
if ($LASTEXITCODE -ne 0) { $log | Where-Object { $_ -match 'error' } | Select-Object -Unique | ForEach-Object { "  $_" }; "  FAILED"; exit 1 }
"  ok"
"2. tests\Extensions.Tests..."
$tests = Join-Path $Repo 'tests\Extensions.Tests'
& { $ErrorActionPreference = 'Continue'; & dotnet restore $tests -v q @sourceArgs 2>&1 | Out-Null }
$log = & { $ErrorActionPreference = 'Continue'; & dotnet test $tests --no-restore -nologo 2>&1 | ForEach-Object { "$_" } }
$testExit = $LASTEXITCODE
$log | Where-Object { $_ -match 'Passed!|Failed|error' } | ForEach-Object { "  $_" }
if ($testExit -ne 0) { "  FAILED"; $bad++ }
"3. Building main.bonsai with Bonsai's builders, against the filled template..."
# RigHardware's cameras load SpinnakerNET, which needs the Spinnaker SDK's native Spinnaker_v140.dll on PATH. The SDK
# is a login-gated download, so a GitHub runner has none and main.bonsai cannot be read there ("There is an error in
# XML document" at RigHardware's first property; domes' CI, 15 Nov 2026). Skipped only then; every rig and lab
# machine with the SDK runs it, as update-workflows.ps1 does before each push.
$spinnaker = @($env:Path -split ';' | Where-Object { $_ -and (Test-Path (Join-Path $_ 'Spinnaker_v140.dll')) })
if (-not $spinnaker.Count) { "  SKIPPED: no Spinnaker SDK (Spinnaker_v140.dll on PATH), so main.bonsai cannot be built here" }
else {
    & { $ErrorActionPreference = 'Continue'; & powershell -NoProfile -ExecutionPolicy Bypass -File $self -Repo $Repo -Step Build 2>&1 | ForEach-Object { "$_" } }
    if ($LASTEXITCODE -ne 0) { "  FAILED"; $bad++ }
}
"4. The task files, the launch properties and Settings_Rig..."
& { $ErrorActionPreference = 'Continue'; & powershell -NoProfile -ExecutionPolicy Bypass -File $self -Repo $Repo -Step Checks 2>&1 | ForEach-Object { "$_" } }
if ($LASTEXITCODE -ne 0) { "  FAILED"; $bad++ }
if ($bad) { "check-workflow: $bad step(s) failed."; exit 1 }
if (-not $spinnaker.Count) { "check-workflow: steps 1, 2 and 4 passed; step 3 skipped (no Spinnaker SDK)." }
else { "check-workflow: all 4 steps passed." }
