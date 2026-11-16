# Instala o Civil3dMcp (servidor MCP do Civil 3D 2027 para o Claude) a partir do pacote pronto.
# Uso (PowerShell, Civil 3D FECHADO):
#   powershell -ExecutionPolicy Bypass -File .\install.ps1
#   powershell -ExecutionPolicy Bypass -File .\install.ps1 -ConfigPath "C:\...\claude_desktop_config.json"
#   powershell -ExecutionPolicy Bypass -File .\install.ps1 -AllUsers      # todos os usuarios do Windows (admin)
#   powershell -ExecutionPolicy Bypass -File .\install.ps1 -SkipConfig    # so o plugin, sem mexer no Claude Desktop
param(
    [string]$ConfigPath,
    [switch]$AllUsers,
    [switch]$SkipConfig,
    [int]$Port = 5101
)
$ErrorActionPreference = 'Stop'

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# 1. Pre-requisitos
$c3d = 'C:\Program Files\Autodesk\AutoCAD 2027\C3D\AeccDbMgd.dll'
if (-not (Test-Path $c3d)) { Write-Warning "Civil 3D 2027 nao encontrado em $c3d. O plugin so carrega no Civil 3D 2027." }
if (Get-Process acad -ErrorAction SilentlyContinue) { throw 'Feche o Civil 3D (acad.exe) antes de instalar.' }

# Pacote descompactado (bundle ao lado do script) ou raiz do repositorio depois do package.ps1 (dist\Civil3dMcp).
$src = @(
    (Join-Path $PSScriptRoot 'Civil3dMcp.bundle'),
    (Join-Path $PSScriptRoot 'dist\Civil3dMcp\Civil3dMcp.bundle')
) | Where-Object { Test-Path (Join-Path $_ 'Contents\Civil3dMcp.Loader.dll') } | Select-Object -First 1
if (-not $src) {
    throw 'Plugin compilado nao encontrado. Use o zip do pacote, ou no repositorio rode antes .\package.ps1 (exige .NET 10 SDK).'
}

# 2. Plugin (bundle)
$pluginRoot = if ($AllUsers) { 'C:\ProgramData\Autodesk\ApplicationPlugins' } else { Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins' }
$dest = Join-Path $pluginRoot 'Civil3dMcp.bundle'
Step "Copiando plugin para $dest"
New-Item -ItemType Directory -Force $pluginRoot | Out-Null
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
Copy-Item $src $dest -Recurse -Force
Get-ChildItem $dest -Recurse | Unblock-File   # arquivos vindos de zip/download
if ($Port -ne 5101) { [Environment]::SetEnvironmentVariable('CIVIL3D_MCP_PORT', "$Port", 'User') }

if ($SkipConfig) { Step 'Plugin instalado (configuracao do Claude Desktop ignorada).'; return }

# 3. uv / uvx (ponte stdio -> HTTP para o Claude Desktop)
$uvx = (Get-Command uvx -ErrorAction SilentlyContinue).Source
if (-not $uvx) { $cand = Join-Path $env:USERPROFILE '.local\bin\uvx.exe'; if (Test-Path $cand) { $uvx = $cand } }
if (-not $uvx) {
    throw "uv nao encontrado. Instale com:  winget install --id astral-sh.uv -e   (ou: powershell -c `"irm https://astral.sh/uv/install.ps1 | iex`")  e rode este script de novo."
}
Step "uvx: $uvx"
Step 'Baixando a ponte mcp-proxy para o cache (primeira vez demora um pouco)'
# No PowerShell 5.1 o stderr do uvx (mensagens de download) viraria erro fatal com 'Stop'.
$ErrorActionPreference = 'Continue'
& $uvx --with 'mcp<2' mcp-proxy --help 2>$null | Out-Null
$code = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($code -ne 0) { throw "Falha ao preparar o mcp-proxy via uvx (codigo $code). Verifique a internet e rode: uvx --with `"mcp<2`" mcp-proxy --help" }

# 4. Configuracao do Claude Desktop
if (-not $ConfigPath) {
    $cands = @()
    $cands += Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Claude-Data') -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'claude_desktop_config.json' }
    $cands += Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'LocalCache\Roaming\Claude\claude_desktop_config.json' }
    $cands += Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'
    $found = @($cands | Where-Object { Test-Path $_ })
    if ($found.Count -eq 0) {
        throw "Nao achei o claude_desktop_config.json. No Claude Desktop abra Configuracoes > Desenvolvedor > Editar configuracao (isso cria o arquivo) e rode de novo com -ConfigPath <caminho>."
    }
    if ($found.Count -gt 1) {
        $found | ForEach-Object { Write-Host "  $_" }
        throw 'Achei mais de um claude_desktop_config.json (listados acima). Rode de novo com -ConfigPath apontando para o certo.'
    }
    $ConfigPath = $found[0]
}
Step "Configuracao: $ConfigPath"

$raw = [IO.File]::ReadAllText($ConfigPath, [Text.Encoding]::UTF8)
if ([string]::IsNullOrWhiteSpace($raw)) { $raw = '{}' }
$cfg = $raw | ConvertFrom-Json
Copy-Item $ConfigPath "$ConfigPath.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"

if (-not $cfg.PSObject.Properties['mcpServers']) {
    $cfg | Add-Member -NotePropertyName mcpServers -NotePropertyValue ([pscustomobject]@{})
}
$entry = [pscustomobject]@{
    command = $uvx
    args    = @('--with', 'mcp<2', 'mcp-proxy', '--transport', 'streamablehttp', "http://127.0.0.1:$Port/mcp")
}
if ($cfg.mcpServers.PSObject.Properties['civil3d']) { $cfg.mcpServers.civil3d = $entry }
else { $cfg.mcpServers | Add-Member -NotePropertyName civil3d -NotePropertyValue $entry }

$json = $cfg | ConvertTo-Json -Depth 100
$null = $json | ConvertFrom-Json   # valida antes de gravar
[IO.File]::WriteAllText($ConfigPath, $json, (New-Object Text.UTF8Encoding($false)))

Step 'Pronto. Agora:'
Write-Host '  1. Feche o Claude Desktop pela bandeja do sistema (Sair) e abra de novo.'
Write-Host '  2. Abra o Civil 3D 2027 e digite C3DMCP na linha de comando para ver o status.'
Write-Host "  3. No navegador, http://127.0.0.1:$Port/ deve responder 'Civil3dMcp ativo'."
