# The hourly task's body (scripts\schedule.ps1 registers a task that runs this file), so a
# change to what the task does arrives with the clone's next pull and needs no new
# registration (audit, 3 Nov 2026: schedule.ps1's failed-run log shipped on 3 Nov, and
# PIPOFFICERIG's task still ran the registration from 2 Nov).
#
# Output of the run: %LOCALAPPDATA%\lib-open-rigs\update-workflows.log, overwritten each hour;
# a run that failed is appended in full to update-workflows-failed.log, which is never
# overwritten. This exits with the script's code, but conhost --headless, which the task runs
# it through for want of a window, reports 0 to Task Scheduler whatever the code (measured
# 3 Nov 2026), so the failed log is the record of a failure, not the task's history.
$script = Join-Path $PSScriptRoot 'update-workflows.ps1'
$logDir = Join-Path $env:LOCALAPPDATA 'lib-open-rigs'
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir 'update-workflows.log'
$failed = Join-Path $logDir 'update-workflows-failed.log'
& { 'Started ' + (Get-Date -Format 'yyyy-MM-dd HH:mm'); & $script; 'Exit ' + $LASTEXITCODE } *> $log
if ($LASTEXITCODE) { Add-Content -LiteralPath $failed -Value (Get-Content -LiteralPath $log -Raw) }
exit $LASTEXITCODE
