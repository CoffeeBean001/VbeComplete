@echo off
rem Launch VbeComplete (no main window, tray only). Wait 2s and prove the
rem process is alive; if it died, say so and keep the window open.
rem Never put a bare parenthesis in echo text - cmd would drop the whole block.
setlocal
set "EXE=%~dp0dist\VbeComplete.exe"

if not exist "%EXE%" goto nobuild

start "" "%EXE%"
ping -n 3 127.0.0.1 >nul 2>nul

rem findstr, not find: Git for Windows ships a Unix find that wins on PATH.
tasklist /fi "imagename eq VbeComplete.exe" /fo csv /nh 2>nul | findstr /i /c:"VbeComplete.exe" >nul
if errorlevel 1 goto died

echo.
echo   VbeComplete is running.
echo   No main window - the icon sits in the tray near the clock.
echo   Right-click it to exit, or run stop-cs.bat.
echo.
ping -n 4 127.0.0.1 >nul
endlocal
exit /b 0

:died
echo.
echo [VbeComplete] Launch failed - no process after 2 seconds.
echo ------------------------------------------------------------
echo A crash pops up an error dialog with the message - check it,
echo then run again.
echo ------------------------------------------------------------
pause
endlocal
exit /b 1

:nobuild
echo.
echo [VbeComplete] dist\VbeComplete.exe not found. Run build.bat first.
echo.
pause
endlocal
exit /b 1
