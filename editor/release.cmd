@echo off
rem release.cmd --version 0.1.0 --repo owner/name  (see build_release.sh). Close the editor and renderer first.
setlocal
cd /d "%~dp0"
set "SH="
for /f "delims=" %%s in ('where sh 2^>nul') do if not defined SH set "SH=%%s"
for /f "delims=" %%g in ('where git 2^>nul') do if not defined SH if exist "%%~dpg..\bin\sh.exe" set "SH=%%~dpg..\bin\sh.exe"
if not defined SH if exist "%ProgramFiles%\Git\bin\sh.exe" set "SH=%ProgramFiles%\Git\bin\sh.exe"
if not defined SH (
  echo [release] sh.exe not found. Install Git for Windows.
  exit /b 1
)
"%SH%" build_release.sh %*
if errorlevel 1 (
  echo [release] FAILED
  exit /b 1
)
endlocal
