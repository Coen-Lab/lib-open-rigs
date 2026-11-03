# The hourly task's body (scripts\schedule.ps1 registers a task that runs this file), so a
# change to what the task does arrives with the clone's next pull and needs no new
# registration (audit, 3 Nov 2026: schedule.ps1's failed-run log shipped on 3 Nov, and
# PIPOFFICERIG's task still ran the registration from 2 Nov).
#
# Output of the run: %LOCALAPPDATA%\lib-open-rigs\update-workflows.log, overwritten each hour;
# a run that failed is appended in full to update-workflows-failed.log, which is never
# overwritten. This exits with the script's code, but conhost --headless, which the task runs
# it through for want of a window, reports 0 to Task Scheduler whatever the code (measured
# 3 Nov 2026), so the failed log is the record of a failure, not the task's history; the share
# copy below is where a person sees it.
#
# The run is from a clone of its own, %LOCALAPPDATA%\lib-open-rigs\self, set to origin/main each
# hour, never the working copy this file is in: commits not yet pushed there failed three runs in
# a row on 3 Nov 2026, and a branch checked out there would have pinned every workflow to it
# (audit-rigs set C). push.ps1 still runs from the working copy, where those refusals belong.
$logDir = Join-Path $env:LOCALAPPDATA 'lib-open-rigs'
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir 'update-workflows.log'
$failed = Join-Path $logDir 'update-workflows-failed.log'
$self = Join-Path $logDir 'self'
& {
    'Started ' + (Get-Date -Format 'yyyy-MM-dd HH:mm')
    $ErrorActionPreference = 'Continue'
    if (-not (Test-Path (Join-Path $self '.git'))) {
        $url = git -C (Split-Path $PSScriptRoot) remote get-url origin
        git clone -q $url $self 2>&1 | ForEach-Object { "$_" }
    }
    git -C $self fetch -q origin 2>&1 | ForEach-Object { "$_" }
    # On main, following origin/main, which update-workflows' checks read as @{u}.
    git -C $self checkout -q -f -B main origin/main 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { "Stopped: could not set $self to origin/main."; 'Exit 1'; return }
    & (Join-Path $self 'scripts\update-workflows.ps1')
    'Exit ' + $LASTEXITCODE
} *> $log
$code = $LASTEXITCODE
# A failed run is also copied to the share, where rig-setup's Machines window shows it beside
# the machine's update log; a run that passes removes it. A share not mapped leaves only the
# local logs.
$shared = "K:\config\$env:COMPUTERNAME\$($env:COMPUTERNAME)_WorkflowsFailed.txt"
if ($code) { Add-Content -LiteralPath $failed -Value (Get-Content -LiteralPath $log -Raw) }
try {
    if ($code) { Copy-Item -LiteralPath $log $shared -Force -ErrorAction Stop }
    elseif (Test-Path -LiteralPath $shared) { Remove-Item -LiteralPath $shared -ErrorAction Stop }
} catch { Add-Content -LiteralPath $log -Value "Could not update ${shared}: $($_.Exception.Message)" }
exit $code
