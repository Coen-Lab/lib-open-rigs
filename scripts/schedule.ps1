# Registers the hourly run of update-workflows.ps1 on this machine, as the signed-in user (git
# pushes with their stored credentials, so it runs only while they are logged on), with no
# window. The task runs scripts\hourly.ps1, which holds what the run does (its logs are
# described there), so a change to it arrives with a pull and this is run again only after
# moving the clone. -Remove takes the task away.
param([switch]$Remove)
$ErrorActionPreference = 'Stop'
$name = 'lib-open-rigs update-workflows'
if ($Remove) { Unregister-ScheduledTask -TaskName $name -Confirm:$false; "Removed '$name'."; exit 0 }

$hourly = Join-Path $PSScriptRoot 'hourly.ps1'
# conhost --headless runs the console with no window at all; -WindowStyle Hidden still flashes one.
$action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$hourly`""
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date.AddHours((Get-Date).Hour + 1) -RepetitionInterval (New-TimeSpan -Hours 1)
# Three hours: a run may wait 40 minutes for another, then set up Bonsai for a new repo or
# branch at up to 20 minutes each; a run stopped by the limit writes no failed log.
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 3)
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive
Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
"Registered '$name': every hour from $($trigger.StartBoundary), running $hourly"
