Write-Host "=========================================" -ForegroundColor Cyan
Write-Host "Starting Programmatic App Validation..." -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan

# 1. Compile the app
Write-Host "Step 1: Compiling application..." -ForegroundColor Yellow
$buildResult = Start-Process cmd.exe -ArgumentList "/c build.bat" -Wait -NoNewWindow -PassThru
if ($buildResult.ExitCode -ne 0) {
    Write-Error "Build failed!"
    exit 1
}
Write-Host "[SUCCESS] Compilation verified." -ForegroundColor Green

# 2. Launch server
# We launch the server on port 5001 in the background to avoid ports conflict
Write-Host "Step 2: Launching web server in background on port 5001..." -ForegroundColor Yellow
$process = Start-Process "bin\Release\VideoProcessor.exe" -ArgumentList "5001" -PassThru -NoNewWindow

# 3. Dynamic polling loop
Write-Host "Step 3: Polling http://localhost:5001/ until online (up to 60s)..." -ForegroundColor Yellow
$maxAttempts = 60
$online = $false
$attempt = 1
$response = $null

while ($attempt -le $maxAttempts) {
    try {
        $response = Invoke-WebRequest -Uri "http://localhost:5001/" -UseBasicParsing -TimeoutSec 2
        if ($response.StatusCode -eq 200) {
            $online = $true
            break
        }
    }
    catch {
        # Expected while server is downloading FFmpeg or booting up
    }
    Write-Host "Waiting for server to boot (Attempt $attempt/$maxAttempts)..." -ForegroundColor Gray
    Start-Sleep -Seconds 1
    $attempt++
}

try {
    if ($online -and $response.Content -like "*Video & Music Fusion Engine*") {
        Write-Host "[SUCCESS] Web Server root endpoint is fully online and serving the custom frontend!" -ForegroundColor Green
    } else {
        throw "Invalid response or timeout from server."
    }
}
catch {
    Write-Host "Validation failed: $_" -ForegroundColor Red
    # Make sure we stop the process
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    exit 1
}

# 4. Cleanup
Write-Host "Step 4: Shutting down background server..." -ForegroundColor Yellow
Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
Write-Host "=========================================" -ForegroundColor Green
Write-Host "PROGRAMMATIC VALIDATION COMPLETED: SUCCESS!" -ForegroundColor Green
Write-Host "=========================================" -ForegroundColor Green
