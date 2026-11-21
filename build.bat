@echo off
echo =========================================
echo Building VideoProcessor...
echo =========================================
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\msbuild.exe VideoProcessor.csproj /p:Configuration=Release /t:Rebuild
if %errorlevel% neq 0 (
    echo [ERROR] Build failed!
    exit /b %errorlevel%
)
echo [SUCCESS] Build succeeded!
