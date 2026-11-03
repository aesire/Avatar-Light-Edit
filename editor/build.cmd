@echo off
rem 빌드를 PowerShell·명령 프롬프트·탐색기 더블클릭에서 (안에서 Git for Windows의 sh로 build.sh·build_renderer.sh를 부른다).
rem   build.cmd            에디터(전부) + 렌더러
rem   build.cmd av         에디터만        build.cmd renderer   렌더러만
rem 빌드 전에 에디터와 렌더러 창을 닫는다 (실행 중이면 파일이 잠겨 있다).
rem 이 파일은 cp949(한국어 Windows 명령 창 기본)로 저장한다.
setlocal
cd /d "%~dp0"
set "SH="
for /f "delims=" %%s in ('where sh 2^>nul') do if not defined SH set "SH=%%s"
if not defined SH for /f "delims=" %%g in ('where git 2^>nul') do if not defined SH if exist "%%~dpg..\bin\sh.exe" set "SH=%%~dpg..\bin\sh.exe"
if not defined SH if exist "%ProgramFiles%\Git\bin\sh.exe" set "SH=%ProgramFiles%\Git\bin\sh.exe"
if not defined SH (
  echo [build] sh를 찾지 못했어요. Git for Windows를 설치해 주세요: https://git-scm.com/download/win
  set ERR=1
  goto :end
)
set ERR=0
if /i "%~1"=="renderer" goto :renderer
"%SH%" build.sh %* || (set "ERR=1" & goto :end)
if not "%~1"=="" goto :end
:renderer
"%SH%" build_renderer.sh || set ERR=1
:end
if "%ERR%"=="1" (
  echo [build] 실패했어요. 위 메시지를 확인해 주세요.
  pause
  exit /b 1
)
echo [build] 끝났어요.
endlocal
