@echo off
REM Wrapper: runs publish.ps1 with a matching execution policy.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
exit /b %ERRORLEVEL%
