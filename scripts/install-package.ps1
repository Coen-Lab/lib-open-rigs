param(
    # The .nupkg to install.
    [Parameter(Mandatory)][string]$Nupkg,
    # A Bonsai environment root: a folder with .bonsai\Bonsai.config and local_packages\.
    [Parameter(Mandatory)][string]$Environment
)
# Installs one package into a Bonsai environment the way its Setup.ps1 would, and leaves it
# with no other version of that package:
#   - local_packages\: the .nupkg copied in, any other version of the same id deleted;
#   - .bonsai\Packages\: unpacked into <id>.<version>\, other versions removed (only where the
#     environment is deployed, so a check can run on it);
#   - .bonsai\Bonsai.config: the package's three entries updated, or added if it is new there,
#     as text so line endings, BOM and the order of everything else stay as they are;
#   - every .csproj in the environment that references the package: its Version updated, so the
#     workflow's own C# compiles against the same package Bonsai loads.
# Git is left to the caller. Used by build-package.ps1 -Install and update-workflows.ps1.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$Nupkg = (Resolve-Path $Nupkg).Path
$Environment = (Resolve-Path $Environment).Path
$config = Join-Path $Environment '.bonsai\Bonsai.config'
if (-not (Test-Path $config)) { throw "$Environment has no .bonsai\Bonsai.config" }

# Id, version and the assembly's path inside the package, read from the package itself.
$zip = [IO.Compression.ZipFile]::OpenRead($Nupkg)
try {
    $spec = $zip.Entries | Where-Object { $_.FullName -notmatch '/' -and $_.Name -like '*.nuspec' } | Select-Object -First 1
    $reader = New-Object IO.StreamReader($spec.Open())
    try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Close() }
    $id = $nuspec.package.metadata.id
    $version = $nuspec.package.metadata.version
    # net472 if the package has it, otherwise its newest .NET Framework build (Bonsai.FFmpeg is net462).
    $dll = $zip.Entries | Where-Object { $_.FullName -match "^lib/(net4\d+)/$([regex]::Escape($id))\.dll$" } |
        Sort-Object @{ Expression = { $_.FullName -match '^lib/net472/' }; Descending = $true }, @{ Expression = { $_.FullName }; Descending = $true } |
        Select-Object -First 1
} finally { $zip.Dispose() }
if (-not $dll) { throw "$Nupkg has no lib/net4*/$id.dll" }
$location = "Packages/$id.$version/$($dll.FullName)"
# Another version of this id: the id, a dot, then a version, which always starts with a digit.
$otherVersion = "^$([regex]::Escape($id))\.\d"
"  ${id} $version into $Environment"

$local = Join-Path $Environment 'local_packages'
New-Item -ItemType Directory -Force $local | Out-Null
Get-ChildItem $local -File -Filter '*.nupkg' | Where-Object { $_.Name -match $otherVersion -and $_.Name -ne "$id.$version.nupkg" } |
    ForEach-Object { Remove-Item $_.FullName; "    removed local_packages\$($_.Name)" }
Copy-Item $Nupkg (Join-Path $local "$id.$version.nupkg") -Force
"    local_packages\$id.$version.nupkg"

$packages = Join-Path $Environment '.bonsai\Packages'
if (Test-Path $packages) {
    Get-ChildItem $packages -Directory | Where-Object { $_.Name -match $otherVersion } | Remove-Item -Recurse -Force
    $target = Join-Path $packages "$id.$version"
    [IO.Compression.ZipFile]::ExtractToDirectory($Nupkg, $target)
    Copy-Item $Nupkg $target -Force
    "    unpacked into .bonsai\Packages\$id.$version"
}

$bytes = [IO.File]::ReadAllBytes($config)
$bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
$text = [IO.File]::ReadAllText($config)
$nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
$e = [regex]::Escape($id)
$entries = @(
    @("<Packages>", "    <Package id=`"$e`" version=`"[^`"]+`" />", "    <Package id=`"$id`" version=`"$version`" />"),
    @("<AssemblyReferences>", "    <AssemblyReference assemblyName=`"$e`" />", "    <AssemblyReference assemblyName=`"$id`" />"),
    @("<AssemblyLocations>", "    <AssemblyLocation assemblyName=`"$e`" processorArchitecture=`"MSIL`" location=`"[^`"]+`" />", "    <AssemblyLocation assemblyName=`"$id`" processorArchitecture=`"MSIL`" location=`"$location`" />")
)
# A package the environment already has keeps the entries it has; a new one gets all three.
$isNew = $text -notmatch $entries[0][1]
foreach ($entry in $entries) {
    $section, $pattern, $line = $entry
    if ($text -match $pattern) {
        $text = [regex]::Replace($text, $pattern, $line.Replace('$', '$$'))
    } elseif ($isNew) {
        # Alphabetical among its section's entries: the last line whose id sorts before ours.
        $lines = $text -split $nl
        $start = [array]::IndexOf($lines, ($lines | Where-Object { $_.Trim() -eq $section } | Select-Object -First 1))
        $insertAt = $start + 1
        for ($i = $start + 1; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^\s*</') { break }
            $name = [regex]::Match($lines[$i], '(?:id|assemblyName)="([^"]+)"').Groups[1].Value
            if ($name -and [string]::Compare($name, $id, $true) -lt 0) { $insertAt = $i + 1 }
        }
        $lines = $lines[0..($insertAt - 1)] + $line + $lines[$insertAt..($lines.Count - 1)]
        $text = $lines -join $nl
    }
}
[IO.File]::WriteAllText($config, $text, (New-Object Text.UTF8Encoding($bom)))
"    Bonsai.config: $version at $location"

$reference = "(<PackageReference\s+Include=`"$e`"\s+Version=`")[^`"]*(`")"
Get-ChildItem $Environment -Recurse -File -Filter '*.csproj' |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|\.bonsai|\.git)\\' } | ForEach-Object {
        $bytes = [IO.File]::ReadAllBytes($_.FullName)
        $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        $text = [IO.File]::ReadAllText($_.FullName)
        if ($text -match $reference) {
            $updated = [regex]::Replace($text, $reference, "`${1}$version`${2}")
            if ($updated -ne $text) {
                [IO.File]::WriteAllText($_.FullName, $updated, (New-Object Text.UTF8Encoding($bom)))
                "    $($_.FullName.Substring($Environment.TrimEnd('\').Length + 1)): PackageReference $version"
            }
        }
    }
