[CmdletBinding()] param (
    [string[]]$LibrarySources,
    [bool]$UseGalleryForWorkflowsDirectory=$false,
    [bool]$UseGalleryForExamplesDirectory=$true,
    [string]$OutputFolder=$null
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

if ($OutputFolder) {
    $OutputFolder = Join-Path (Get-Location) $OutputFolder
}

function Process-Workflow-Collection([bool]$useGallery, [string]$workflowPath, [string]$environmentPath) {
    $libPath = $LibrarySources

    if ($useGallery) {
        $libPath = @()
        $galleryPath = Join-Path $environmentPath 'Gallery'
        $null = New-Item -ItemType Directory -Path $galleryPath -Force
        foreach ($librarySource in $LibrarySources) {
            Get-ChildItem -Path $librarySource -Filter *.nupkg | Copy-Item -Destination $galleryPath
        }
    }

    $bootstrapperPath = (Join-Path $environmentPath 'Bonsai.exe')
    .\openrigs-docfx\modules\Export-Image.ps1 -libPath $libPath -workflowPath $workflowPath -bootstrapperPath $bootstrapperPath -outputFolder $OutputFolder -documentationRoot $PSScriptRoot
}

function Render-Referenced-Workflows {
    Import-Module (Join-Path $PSScriptRoot 'openrigs-docfx/modules/Export-Tools.psm1') -Verbose:$false
    $bootstrapperPath = Join-Path $PSScriptRoot '../.bonsai/Bonsai.exe'
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

    # Matches image+data-bonsai pairs where the .bonsai source is under src/
    # ~/assets/... resolves relative to $PSScriptRoot (docs/)
    # ~/src/...   resolves relative to $repoRoot
    $pattern = '!\[[^\]]*\]\(~/([^)]+\.svg)\)\{[^}]*data-bonsai="~/(src/[^"]+\.bonsai)"'

    foreach ($mdFile in Get-ChildItem -Path (Join-Path $PSScriptRoot 'bonsai-operators') -Filter '*.md' -Recurse -File) {
        $content = Get-Content $mdFile.FullName -Raw
        foreach ($m in [regex]::Matches($content, $pattern)) {
            $svgRelPath    = $m.Groups[1].Value   # e.g. assets/workflows/devices/LicketySplit.svg
            $bonsaiRelPath = $m.Groups[2].Value   # e.g. src/OpenRigs.Devices/LicketySplit.bonsai

            $svgPath    = if ($OutputFolder) {
                Join-Path $OutputFolder $svgRelPath
            } else {
                Join-Path $PSScriptRoot $svgRelPath
            }
            $bonsaiPath = Join-Path $repoRoot $bonsaiRelPath

            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $svgPath) -Force

            $bootstrapperArgs = @()
            foreach ($path in $LibrarySources) {
                $bootstrapperArgs += '--lib'
                $bootstrapperArgs += (Resolve-Path $path).Path
            }
            $bootstrapperArgs += '--export-image'
            $bootstrapperArgs += $svgPath
            $bootstrapperArgs += $bonsaiPath

            Write-Host "Exporting $svgRelPath"
            Write-Verbose "Source: $bonsaiPath"
            & $bootstrapperPath $bootstrapperArgs

            if ($LASTEXITCODE -eq 0 -and (Test-Path $svgPath)) {
                Convert-Svg $svgPath
            } elseif ($LASTEXITCODE -ne 0) {
                Write-Warning "Bonsai failed to export $svgRelPath (exit $LASTEXITCODE)"
            }
        }
    }
}

Push-Location $PSScriptRoot
try {
    if (Test-Path -Path 'workflows/') {
        Process-Workflow-Collection $UseGalleryForWorkflowsDirectory './workflows' '../.bonsai/'
    }

    if (Test-Path -Path 'examples/') {
        foreach ($environment in (Get-ChildItem -Path 'examples/' -Filter '.bonsai' -Recurse -FollowSymlink -Directory)) {
            Process-Workflow-Collection $UseGalleryForExamplesDirectory ($environment.Parent.FullName) ($environment.FullName)
        }
    }

    Render-Referenced-Workflows
} finally {
    Pop-Location
}
