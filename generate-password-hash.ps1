# Script para gerar hash de senha para autenticação
# Uso: .\generate-password-hash.ps1

Write-Host "=== Gerador de Hash de Senha ===" -ForegroundColor Cyan
Write-Host ""

$senha = Read-Host "Digite a senha" -AsSecureString
$senhaTexto = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($senha)
)

$bytes = [System.Text.Encoding]::UTF8.GetBytes($senhaTexto)
$sha256 = [System.Security.Cryptography.SHA256]::Create()
$hash = $sha256.ComputeHash($bytes)
$hashString = [Convert]::ToBase64String($hash)

Write-Host ""
Write-Host "Hash gerado (cole no config.json):" -ForegroundColor Green
Write-Host $hashString -ForegroundColor Yellow
Write-Host ""
Write-Host "Exemplo de uso no config.json:" -ForegroundColor Cyan
Write-Host '{' -ForegroundColor Gray
Write-Host '  "ClientId": "cliente",' -ForegroundColor Gray
Write-Host '  "ClientName": "Nome do Cliente",' -ForegroundColor Gray
Write-Host '  "ZabbixServer": null,' -ForegroundColor Gray
Write-Host '  "ZabbixApiToken": null,' -ForegroundColor Gray
Write-Host '  "Username": "usuario",' -ForegroundColor Gray
Write-Host "  `"PasswordHash`": `"$hashString`"" -ForegroundColor Yellow
Write-Host '}' -ForegroundColor Gray
Write-Host ""

# Limpa a senha da memória
$senhaTexto = $null
$bytes = $null
