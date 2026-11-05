# One-time migration for existing local covers. Does NOT change book data except cover URL/key.
# Windows PowerShell 5.1 / PowerShell 7; no GUI credential popups.
param([string]$BaseUrl = 'http://127.0.0.1:5082')
$ErrorActionPreference = 'Stop'
$BaseUrl = $BaseUrl.TrimEnd('/')
$loginBody = $null
$login = $null
$headers = $null
$credential = $null
$secure = $null
try {
    Write-Host 'SMART LIBRARY - CLOUDINARY: CHUYEN ANH BIA CU' -ForegroundColor Cyan
    $health = Invoke-RestMethod -Uri "$BaseUrl/api/health" -Method Get -TimeoutSec 30
    if ($health.status -ne 'ok') { throw 'API chua san sang.' }
    Write-Host 'API da san sang. Chi ADMIN duoc phep chuyen anh.' -ForegroundColor Green
    $username = (Read-Host 'Ten dang nhap Admin').Trim()
    if ([string]::IsNullOrWhiteSpace($username)) { throw 'Chua nhap tai khoan Admin.' }
    $secure = Read-Host 'Mat khau Admin (khong hien ky tu)' -AsSecureString
    if ($secure.Length -eq 0) { throw 'Chua nhap mat khau.' }
    $credential = New-Object System.Management.Automation.PSCredential($username,$secure)
    $loginBody = @{username=$username;password=$credential.GetNetworkCredential().Password} | ConvertTo-Json -Compress
    $credential = $null
    $secure = $null
    $login = Invoke-RestMethod -Uri "$BaseUrl/api/auth/login" -Method Post `
        -ContentType 'application/json; charset=utf-8' -Body $loginBody -TimeoutSec 30
    $loginBody = $null
    if ($login.user.role -ne 'Admin' -or [string]::IsNullOrWhiteSpace($login.token)) {
        throw 'Tai khoan khong co quyen Admin.'
    }
    $headers = @{Authorization="Bearer $($login.token)"}
    $login = $null
    Write-Host 'Dang chuyen cac anh local da co vao Cloudinary. Khong xoa anh goc...' -ForegroundColor Yellow
    $result = Invoke-RestMethod -Uri "$BaseUrl/api/admin/book-covers/migrate-to-cloudinary" `
        -Headers $headers -Method Post -TimeoutSec 900
    $result | Format-List scanned,migrated,missingLocalFiles,failed,skipped,problemBookIds,message
    Write-Host 'Xem lai duong dan cover_url va cover_object_key trong DBeaver.' -ForegroundColor Green
}
catch {
    Write-Host "LOI: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
        Write-Host "HTTP: $([int]$_.Exception.Response.StatusCode)" -ForegroundColor Red
    }
    exit 1
}
finally {
    Remove-Variable loginBody,login,headers,credential,secure -ErrorAction SilentlyContinue
}
