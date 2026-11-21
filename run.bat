@echo off
if not exist "bin\Release\VideoProcessor.exe" (
    echo [INFO] VideoProcessor is not compiled. Running build first...
    call build.bat
    if %errorlevel% neq 0 (
        echo [ERROR] Build failed! Cannot start the server.
        pause
        exit /b %errorlevel%
    )
)
echo ===================================================
echo [INFO] Starting VideoProcessor Web Server...
echo ===================================================
bin\Release\VideoProcessor.exe
