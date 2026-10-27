@echo off
rem Starts the ONI Advisor companion with the per-user .NET install (no PATH or DOTNET_ROOT needed).
set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"
start "" "%DOTNET%" "%~dp0Companion\bin\Release\net8.0-windows\OniAdvisorCompanion.dll" %*
