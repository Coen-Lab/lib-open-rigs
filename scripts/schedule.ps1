# Registers the hourly run of update-workflows.ps1 on this machine, as the signed-in user (git
# pushes with their stored credentials, so it runs only while they are logged on), with no
# window. Output of the last run: %LOCALAPPDATA%\lib-open-rigs\update-workflows.log; every
# failed run's, appended: update-workflows-failed.log.
# Run again after moving this clone; -Remove takes the task away.
param([switch]$Remove)
$ErrorActionPreference = 'Stop'
$name = 'lib-open-rigs update-workflows'
if ($Remove) { Unregister-ScheduledTask -TaskName $name -Confirm:$false; "Removed '$name'."; exit 0 }

$script = Join-Path $PSScriptRoot 'update-workflows.ps1'
$logDir = Join-Path $env:LOCALAPPDATA 'lib-open-rigs'
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir 'update-workflows.log'
# A run that failed is kept in full in a second log that is never overwritten, and the task
# ends with the script's exit code, so Task Scheduler's history says so too (the hourly log
# alone was overwritten within the hour, and the task read as succeeded whatever happened).
$failed = Join-Path $logDir 'update-workflows-failed.log'
# conhost --headless runs the console with no window at all; -WindowStyle Hidden still flashes one.
$command = "& { 'Started ' + (Get-Date -Format 'yyyy-MM-dd HH:mm'); & '$script'; 'Exit ' + `$LASTEXITCODE } *> '$log'; " +
    "if (`$LASTEXITCODE) { Add-Content -LiteralPath '$failed' -Value (Get-Content -LiteralPath '$log' -Raw) }; exit `$LASTEXITCODE"
$action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -Command `"$command`""
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date.AddHours((Get-Date).Hour + 1) -RepetitionInterval (New-TimeSpan -Hours 1)
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 1)
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive
Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
"Registered '$name': every hour from $($trigger.StartBoundary), log $log"
