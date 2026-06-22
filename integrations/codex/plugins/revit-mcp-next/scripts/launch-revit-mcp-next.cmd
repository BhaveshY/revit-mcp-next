@echo off
setlocal
set "LAUNCHER=%LOCALAPPDATA%\RevitMcpNext\launch-revit-mcp-next.cmd"
if not exist "%LAUNCHER%" (
  echo Revit MCP Next launcher not installed at %LAUNCHER%. Run installer\install-windows.ps1 first. 1>&2
  exit /b 127
)
cmd /c "%LAUNCHER%"
exit /b %ERRORLEVEL%

