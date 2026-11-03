@echo off
setlocal
pushd "%~dp0"

dotnet run --project "Content.Server\Content.Server.csproj" -- %*
set "exitCode=%ERRORLEVEL%"

popd
if not "%exitCode%"=="0" (
    echo.
    echo O servidor foi encerrado com o codigo %exitCode%.
)
pause
exit /b %exitCode%
