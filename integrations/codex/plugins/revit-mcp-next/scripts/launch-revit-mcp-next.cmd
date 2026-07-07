@echo off
setlocal
if defined REVIT_MCP_NEXT_LAUNCHER (
  if exist "%REVIT_MCP_NEXT_LAUNCHER%" (
    set "LAUNCHER=%REVIT_MCP_NEXT_LAUNCHER%"
    goto run
  )
)
if defined REVIT_MCP_NEXT_INSTALL_ROOT (
  if exist "%REVIT_MCP_NEXT_INSTALL_ROOT%\launch-revit-mcp-next.cmd" (
    set "LAUNCHER=%REVIT_MCP_NEXT_INSTALL_ROOT%\launch-revit-mcp-next.cmd"
    goto run
  )
)
if exist "%LOCALAPPDATA%\RevitMcpNext\launch-revit-mcp-next.cmd" (
  set "LAUNCHER=%LOCALAPPDATA%\RevitMcpNext\launch-revit-mcp-next.cmd"
  goto run
)
if exist "%APPDATA%\Autodesk\Revit\Addins\2024\RevitMcpNext\launch-revit-mcp-next.cmd" (
  set "LAUNCHER=%APPDATA%\Autodesk\Revit\Addins\2024\RevitMcpNext\launch-revit-mcp-next.cmd"
  goto run
)
echo Revit MCP Next launcher not installed. Run installer\install-windows.ps1 or set REVIT_MCP_NEXT_INSTALL_ROOT. 1>&2
exit /b 127

:run
cmd /c "%LAUNCHER%"
exit /b %ERRORLEVEL%
