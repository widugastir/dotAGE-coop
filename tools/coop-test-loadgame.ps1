# Launch both DotAGE copies, local lobby, host loads the current save.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File C:\Unity\DotAGECoop\tools\coop-test-loadgame.ps1
#   powershell -ExecutionPolicy Bypass -File C:\Unity\DotAGECoop\tools\coop-test-loadgame.ps1 -KillExisting
#
# Host profile must already have a save (HasGameToLoad). Client joins and receives the transfer.

param(
    [string]$HostGame = "E:\Games\dotAGE",
    [string]$ClientGame = "E:\Games\dotAGE_2",
    [double]$SettleSeconds = 12,
    [double]$JoinTimeout = 90,
    [switch]$KillExisting
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "coop-test-common.ps1")

Start-CoopTestPair -Action loadgame `
    -HostGame $HostGame `
    -ClientGame $ClientGame `
    -SettleSeconds $SettleSeconds `
    -JoinTimeout $JoinTimeout `
    -KillExisting:$KillExisting
