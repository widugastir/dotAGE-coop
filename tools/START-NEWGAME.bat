@echo off
cd /d "%~dp0"
start "DotAgeCoop New Game" /min powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0coop-test-newgame.ps1" -KillExisting
exit /b
