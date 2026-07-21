@echo off
cd /d "%~dp0"
start "DotAgeCoop Load Game" /min powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0coop-test-loadgame.ps1" -KillExisting
exit /b
