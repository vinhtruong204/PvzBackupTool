@echo off
rem Launcher for the backup tool. Builds on first run if needed.
set EXE=%~dp0bin\Release\net10.0-windows\PvzBackupTool.exe

if not exist "%EXE%" (
    echo Building...
    dotnet build "%~dp0PvzBackupTool.csproj" -c Release -v minimal || goto :failed
)

start "" "%EXE%"
exit /b 0

:failed
echo.
echo Build failed. Make sure the .NET 10 SDK is installed.
pause
exit /b 1
