@echo off
rem Build and publish a single-file exe into dist\VbeComplete.exe
rem Keep echo text free of bare parentheses, and keep this file CRLF.
setlocal
cd /d "%~dp0src"
echo [build] compiling...
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ..\dist
if errorlevel 1 goto fail
echo.
echo [build] done: %~dp0dist\VbeComplete.exe
goto end
:fail
echo.
echo [build] FAILED - see the errors above
echo [build] if it says Access denied, run stop-cs.bat first
:end
endlocal
pause
