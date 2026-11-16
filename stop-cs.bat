@echo off
rem Stop VbeComplete.
taskkill /IM VbeComplete.exe /F >nul 2>&1
if errorlevel 1 goto norun
echo VbeComplete stopped.
exit /b 0
:norun
echo No VbeComplete process is running.
exit /b 1
