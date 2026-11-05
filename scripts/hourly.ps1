# The hourly task's body (scripts\schedule.ps1 registers a task that runs this file from the
# clone below), so a change to what the task does arrives with the next push to main and needs
# no new registration (audit, 3 Nov 2026: schedule.ps1's failed-run log shipped on 3 Nov, and
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
# hour, never a working copy: commits not yet pushed there failed three runs in a row on 3 Nov
# 2026, and a branch checked out there would have pinned every workflow to it (audit-rigs set C).
# The task runs this file from that clone too, so the reset below rewrites the file running:
# PowerShell parses a -File script whole before running it, so this run finishes as it began
# and the next takes the new one. push-and-update-workflows.ps1 still runs from the working copy, where its refusals
# belong.
$logDir = Join-Path $env:LOCALAPPDATA 'lib-open-rigs'
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir 'update-workflows.log'
$failed = Join-Path $logDir 'update-workflows-failed.log'
$self = Join-Path $logDir 'self'
function UtcNow { [DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture) }
$started = UtcNow
& {
    'Started ' + (Get-Date -Format 'yyyy-MM-dd HH:mm')
    $ErrorActionPreference = 'Continue'
    if (-not (Test-Path (Join-Path $self '.git'))) {
        $url = git -C (Split-Path $PSScriptRoot) remote get-url origin
        git clone -q $url $self 2>&1 | ForEach-Object { "$_" }
    }
    git -C $self fetch -q origin 2>&1 | ForEach-Object { "$_" }
    # Unchecked, a failed fetch ran last hour's origin/main again and could pass (audit-rigs set C).
    if ($LASTEXITCODE -ne 0) { 'Stopped: fetch failed.'; 'Exit 1'; return }
    # On main, following origin/main, which update-workflows' checks read as @{u}.
    git -C $self checkout -q -f -B main origin/main 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { "Stopped: could not set $self to origin/main."; 'Exit 1'; return }
    # A child, as push-and-update-workflows.ps1 runs it: called in this process, a script that does not parse left
    # $LASTEXITCODE at git's 0, and the run was recorded as a pass (audit-rigs set B, 4 Nov 2026).
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $self 'scripts\update-workflows.ps1') 2>&1 | ForEach-Object { "$_" }
    'Exit ' + $LASTEXITCODE
} *> $log
$code = $LASTEXITCODE
# A failed run is also copied to the share, where a person can read it without this machine; a
# run that passes removes it. A share not mapped leaves only the local logs.
$shared = "K:\config\$env:COMPUTERNAME\$($env:COMPUTERNAME)_WorkflowsFailed.txt"
if ($code) { Add-Content -LiteralPath $failed -Value (Get-Content -LiteralPath $log -Raw) }
try {
    # Rewritten as UTF-8, not copied: *> writes the log as UTF-16 under PowerShell 5.1, and
    # ClaudioVisual reads the share copy as UTF-8 and got a NUL after every character.
    if ($code) { [IO.File]::WriteAllText($shared, [IO.File]::ReadAllText($log), (New-Object Text.UTF8Encoding $false)) }
    elseif (Test-Path -LiteralPath $shared) { Remove-Item -LiteralPath $shared -ErrorAction Stop }
} catch { Add-Content -LiteralPath $log -Value "Could not update ${shared}: $($_.Exception.Message)" }
# Every run, passed or not, so a task that stopped running is seen too: it runs only while Pip is
# signed in here, and the task's history reads 0 whatever happened. rig-setup's rig-status report
# and Machines window read this file and flag it when exitCode is not 0 or finished is over 3 h
# old, and ClaudioVisual's rig health DMs the same failures and a finished over 26 hours old
# (audit-rigs sets A to C, 4 Nov 2026). lastPass is this run's finish when it passed, else kept from the last file.
$machine = $env:COMPUTERNAME
$statusDir = "K:\config\$machine"
$status = Join-Path $statusDir "$($machine)_WorkflowsUpdate.json"
$finished = UtcNow
try {
    $lastPass = 'null'
    if ([int]$code -eq 0) { $lastPass = "`"$finished`"" }
    elseif (Test-Path -LiteralPath $status) {
        $previous = [IO.File]::ReadAllText($status)
        if ($previous -match '"lastPass":\s*"([^"]+)"') { $lastPass = "`"$($Matches[1])`"" }
    }
    $json = "{`"machine`": `"$machine`", `"started`": `"$started`", `"finished`": `"$finished`", `"exitCode`": $([int]$code), `"lastPass`": $lastPass}"
    New-Item -ItemType Directory -Force $statusDir -ErrorAction Stop | Out-Null
    # Written beside it, then renamed over it, so rig-setup never reads half a file.
    $tmp = "$status.tmp"
    [IO.File]::WriteAllText($tmp, $json + "`n", (New-Object Text.UTF8Encoding $false))
    if (Test-Path -LiteralPath $status) { [IO.File]::Replace($tmp, $status, [NullString]::Value) } else { [IO.File]::Move($tmp, $status) }
} catch {
    Add-Content -LiteralPath $log -Value "Could not write ${status}: $($_.Exception.Message)"
    Remove-Item -LiteralPath "$status.tmp" -ErrorAction SilentlyContinue
}
exit $code
