@echo off
rem 에디터를 테스트 모드로 띄운다 (자세한 설명은 run_test.sh). 두 번 누르거나: run_test.cmd [x.ir.json ^| 세션.alesav]
rem 이 파일은 cp949(한국어 Windows 명령 창 기본)로 저장한다. UTF-8로 저장하면 cmd가 한글 줄을 잘못 읽어 아래 줄이 깨진다.
rem 빌드(build.sh)에는 sh가 필요하다: PATH에 없으면 Git for Windows(git.exe 옆 bin\sh.exe)를 찾아 쓴다.
setlocal
cd /d "%~dp0"
set "SH="
for /f "delims=" %%s in ('where sh 2^>nul') do if not defined SH set "SH=%%s"
if not defined SH for /f "delims=" %%g in ('where git 2^>nul') do if not defined SH if exist "%%~dpg..\bin\sh.exe" set "SH=%%~dpg..\bin\sh.exe"
if not defined SH if exist "%ProgramFiles%\Git\bin\sh.exe" set "SH=%ProgramFiles%\Git\bin\sh.exe"
if defined SH (
  "%SH%" run_test.sh %*
  goto :end
)
echo [run_test] sh를 찾지 못해 빌드 없이 바로 띄워요 (Git for Windows를 설치하면 바뀐 소스를 자동으로 빌드해요).
if not exist bin\av\VrcProtoAv.dll (
  echo [run_test] bin\av\VrcProtoAv.dll 이 없어요. Git Bash에서 sh build.sh av 로 먼저 빌드하세요.
  goto :end
)
set "FILE=%~1"
if "%FILE%"=="" set "FILE=sample\sample.ir.json"
if not exist bin\av\texts mkdir bin\av\texts
copy /y texts\*.tsv bin\av\texts\ >nul
dotnet bin\av\VrcProtoAv.dll --test --texts texts "%FILE%"
:end
if errorlevel 1 pause
endlocal
