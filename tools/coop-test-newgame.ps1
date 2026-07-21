# Launch both DotAGE copies, local lobby, host deletes save + New Game (defaults).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File C:\Unity\DotAGECoop\tools\coop-test-newgame.ps1
#   powershell -ExecutionPolicy Bypass -File C:\Unity\DotAGECoop\tools\coop-test-newgame.ps1 -KillExisting
#
# Optional:
#   -HostGame / -ClientGame  override install paths
#   -SettleSeconds 12        wait after Game.I ready before lobby
#   -JoinTimeout 90

param(
    [string]$HostGame = "E:\Games\dotAGE",
    [string]$ClientGame = "E:\Games\dotAGE_2",
    [double]$SettleSeconds = 12,
    [double]$JoinTimeout = 90,
    [switch]$KillExisting
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "coop-test-common.ps1")

Start-CoopTestPair -Action newgame `
    -HostGame $HostGame `
    -ClientGame $ClientGame `
    -SettleSeconds $SettleSeconds `
    -JoinTimeout $JoinTimeout `
    -KillExisting:$KillExisting
