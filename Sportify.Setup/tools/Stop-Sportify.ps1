<#
  Stops the local Sportify API that setup or the launcher started (it holds files in the install folder, so the uninstaller and an update stop it first).
  Only the Sportify.Api.exe that runs from THIS install folder is stopped.
#>
$app = Split-Path -Parent $PSScriptRoot
$api = (Join-Path $app "api\Sportify.Api.exe")
Get-Process -Name "Sportify.Api" -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq $api } catch { $false } } | ForEach-Object { try { $_.Kill(); $_.WaitForExit(5000) | Out-Null } catch { } }
exit 0
