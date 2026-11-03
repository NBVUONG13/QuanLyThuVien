# Smart Library Step 85: Kiem tra nhac han cho Reader (khong tao phieu muon gia).
# Yêu cầu: API Development chạy ở 127.0.0.1:5082, Aiven kết nối được.
$ErrorActionPreference = 'Stop'
$baseUrl = 'http://127.0.0.1:5082'
try {
    $credential = Get-Credential -Message 'Nhap tai khoan Reader Smart Library'
    if ($null -eq $credential) { throw 'Da huy dang nhap.' }
    $loginBody = @{
        username = $credential.UserName
        password = $credential.GetNetworkCredential().Password
    } | ConvertTo-Json -Compress
    $login = Invoke-RestMethod -Uri "$baseUrl/api/auth/login" -Method Post -ContentType 'application/json; charset=utf-8' -Body $loginBody
    Remove-Variable loginBody,credential -ErrorAction SilentlyContinue
    if ($login.user.role -ne 'Reader' -or -not $login.token) {
        throw 'Can tai khoan Reader dang nhap hop le.'
    }
    $headers = @{ Authorization = "Bearer $($login.token)" }
    Remove-Variable login -ErrorAction SilentlyContinue

    Write-Host 'Lan 1: kiem tra nhac han' -ForegroundColor Cyan
    $first = Invoke-RestMethod -Uri "$baseUrl/api/notifications/sync-due" -Method Post -Headers $headers -ContentType 'application/json' -Body '{}'
    $first | Format-List created,message

    Write-Host 'Lan 2: goi lai de kiem tra khong tao thong bao trung' -ForegroundColor Cyan
    $second = Invoke-RestMethod -Uri "$baseUrl/api/notifications/sync-due" -Method Post -Headers $headers -ContentType 'application/json' -Body '{}'
    $second | Format-List created,message
    if ($second.created -ne 0) { Write-Warning 'Lan 2 van tao thong bao: can kiem tra idempotency.' }

    Write-Host 'Kiem tra so thong bao chua doc' -ForegroundColor Cyan
    $notes = Invoke-RestMethod -Uri "$baseUrl/api/notifications" -Method Get -Headers $headers
    Write-Host "UnreadCount: $($notes.unreadCount)"
    Write-Host 'Neu khong co sach sap den han, created=0 la binh thuong.' -ForegroundColor Green
}
catch {
    Write-Host "LOI: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    Remove-Variable loginBody,credential,login,headers -ErrorAction SilentlyContinue
}
