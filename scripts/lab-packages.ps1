# The lab's packages and what each is built from, for build-package.ps1 and
# push-and-update-workflows.ps1 (one list, so the two never disagree). Dot-source it.

# A change whose lines are all comments or blank changes no package, so it neither blocks a push
# nor needs a version (86daefc, comment-only edits to Manifest, blocked every push until the
# check ignored them; audit-rigs set C, 6 Nov 2026).
$script:CommentOnly = @('-I', '^\s*//', '-I', '^\s*$')

function Get-LabPackages([string]$Repo) {
    # Only the lab's packages carry a <Version>; open-rigs' own are versioned when packed. A
    # package's source is its folder (not its READMEs), every folder it compiles files from
    # (..\Shared\*.cs) and the build files every project imports: build\ (Common.csproj.targets
    # names what dependency version each nuspec carries; 86daefc changed it and nothing was
    # flagged, audit-rigs set C) and the Directory.Build files.
    @(Get-ChildItem (Join-Path $Repo 'src') -Directory | ForEach-Object {
        $project = Join-Path $_.FullName "$($_.Name).csproj"
        if (-not (Test-Path $project)) { return }
        $xml = [xml](Get-Content $project -Raw)
        $version = $xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
        if (-not $version) { return }
        $inputs = @("src/$($_.Name)", ":!src/$($_.Name)/*.md") + @($xml.Project.ItemGroup.Compile.Include | Where-Object { $_ -match '^\.\.\\([^\\]+)\\' } |
            ForEach-Object { "src/$($Matches[1])" } | Sort-Object -Unique) + @('build', 'Directory.Build.props', 'Directory.Build.targets' | Where-Object { Test-Path (Join-Path $Repo $_) })
        [pscustomobject]@{ Id = $_.Name; Version = $version; Name = "$($_.Name) $version"; Nupkg = "local_packages/$($_.Name).$version.nupkg"; Inputs = $inputs } })
}
