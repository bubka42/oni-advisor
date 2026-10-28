@echo off
rem Starts the ONI Advisor companion by hand, with its window shown (normally the mod starts it in the
rem background when the game starts). The apphost exe has no console window; it finds the per-user .NET
rem install through DOTNET_ROOT.
set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
start "" "%~dp0Companion\bin\Release\net8.0-windows\OniAdvisorCompanion.exe" %*
