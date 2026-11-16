# Gera o pacote distribuivel dist\Civil3dMcp-<versao>.zip (bundle compilado + install.ps1 + docs).
# Quem recebe o zip NAO precisa do .NET SDK.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'

foreach ($proj in "$root\Civil3dMcp.csproj", "$root\Loader\Civil3dMcp.Loader.csproj") {
    & $dotnet build $proj -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Build falhou: $proj" }
}

$version = ([xml](Get-Content "$root\Civil3dMcp.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$stage = Join-Path $root 'dist\Civil3dMcp'
$contents = Join-Path $stage 'Civil3dMcp.bundle\Contents'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $contents | Out-Null

$out = Join-Path $root 'bin\Release\net10.0-windows'
$loaderOut = Join-Path $root 'Loader\bin\Release\net10.0-windows'
Copy-Item "$root\PackageContents.xml" (Join-Path $stage 'Civil3dMcp.bundle') -Force
Copy-Item "$out\*.dll", "$out\*.pdb", "$out\*.json" $contents -Force
Copy-Item "$loaderOut\Civil3dMcp.Loader.dll", "$loaderOut\Civil3dMcp.Loader.pdb" $contents -Force
Copy-Item "$root\install.ps1", "$root\Civil3D.md", "$root\README.md" $stage -Force
New-Item -ItemType Directory -Force (Join-Path $stage 'tests') | Out-Null
Copy-Item "$root\tests\smoke_test.py" (Join-Path $stage 'tests') -Force

$zip = Join-Path $root "dist\Civil3dMcp-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip
Write-Host "Pacote: $zip"
