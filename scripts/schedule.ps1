# Registers the hourly run of update-workflows.ps1 on this machine, as the signed-in user (git
# pushes with their stored credentials, so it runs only while they are logged on), with no
# window. The task runs scripts\hourly.ps1 from %LOCALAPPDATA%\lib-open-rigs\self, which holds
# what the run does (its logs are described there), so a change to it arrives with a push to
# main and this is run again only to change the task itself. -Remove takes the task away.
param([switch]$Remove)
$ErrorActionPreference = 'Stop'
$name = 'lib-open-rigs update-workflows'
if ($Remove) { Unregister-ScheduledTask -TaskName $name -Confirm:$false; "Removed '$name'."; exit 0 }

# From hourly.ps1's own clone, which it sets to origin/main each hour, as it runs update-workflows
# from there: run from this working copy, the task ran whatever was checked out or not yet pushed
# here (audit-rigs set C). Cloned now if missing, as hourly.ps1 would, so the file exists.
$self = Join-Path $env:LOCALAPPDATA 'lib-open-rigs\self'
if (-not (Test-Path (Join-Path $self '.git'))) {
    $url = git -C (Split-Path $PSScriptRoot) remote get-url origin
    & { $ErrorActionPreference = 'Continue'; & git clone -q $url $self 2>&1 | ForEach-Object { "$_" } }
    if ($LASTEXITCODE -ne 0) { throw "git clone of $url into $self failed." }
}
$hourly = Join-Path $self 'scripts\hourly.ps1'
# conhost --headless runs the console with no window at all; -WindowStyle Hidden still flashes one.
$action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$hourly`""
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date.AddHours((Get-Date).Hour + 1) -RepetitionInterval (New-TimeSpan -Hours 1)
# Three hours: a run may wait 40 minutes for another, then set up Bonsai for a new repo or
# branch at up to 20 minutes each; a run stopped by the limit writes no failed log.
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 3)
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive
Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
"Registered '$name': every hour from $($trigger.StartBoundary), running $hourly"
