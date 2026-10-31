@echo off
setlocal

pushd "%~dp0..\.."
if errorlevel 1 exit /b 1

git submodule update --init --recursive
if errorlevel 1 goto submodule_failed

dotnet.exe build -c Tools
set "RESULT=%ERRORLEVEL%"
goto finish

:submodule_failed
set "RESULT=%ERRORLEVEL%"

:finish
popd
if not "%RESULT%"=="0" echo Build failed with exit code %RESULT%.
pause
exit /b %RESULT%
