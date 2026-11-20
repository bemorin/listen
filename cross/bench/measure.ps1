# Measures what Task Manager shows in its "Memory" column (private working set) for an exe.
# Usage: pwsh -File measure.ps1 <path-to-exe> [seconds-to-wait] [args...]
param([string]$Exe, [int]$Wait = 4, [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest)
$p = if ($Rest) { Start-Process -FilePath $Exe -ArgumentList $Rest -PassThru } else { Start-Process -FilePath $Exe -PassThru }
Start-Sleep -Seconds $Wait
$p.Refresh()
$name = [IO.Path]::GetFileNameWithoutExtension($Exe)
$pws = (Get-CimInstance Win32_PerfFormattedData_PerfProc_Process | Where-Object { $_.IDProcess -eq $p.Id }).WorkingSetPrivate
"{0}: size={1:N0} KB  privateWS={2:N1} MB  WS={3:N1} MB  commit={4:N1} MB" -f $name, ((Get-Item $Exe).Length / 1KB), ($pws / 1MB), ($p.WorkingSet64 / 1MB), ($p.PrivateMemorySize64 / 1MB)
if (-not $env:KEEP) { Stop-Process -Id $p.Id -Force }
