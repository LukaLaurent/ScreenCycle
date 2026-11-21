@echo off
rem ScreenCycle - created by Luka LAURENT. Opens the launch / set-up window.
start "" "%SystemRoot%\System32\conhost.exe" --headless "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0app\ScreenCycle.ps1" --welcome
