# Compila o Civil3dMcp (plugin + loader) e instala como bundle em %APPDATA%\Autodesk\ApplicationPlugins.
# O Civil 3D precisa estar FECHADO (o acad.exe trava as DLLs).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'

if (Get-Process acad -ErrorAction SilentlyContinue) {
    throw 'Feche o Civil 3D (acad.exe) antes do deploy.'
}

foreach ($proj in "$root\Civil3dMcp.csproj", "$root\Loader\Civil3dMcp.Loader.csproj") {
    & $dotnet build $proj -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Build falhou: $proj" }
}

$out = Join-Path $root 'bin\Release\net10.0-windows'
$loaderOut = Join-Path $root 'Loader\bin\Release\net10.0-windows'
$bundle = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\Civil3dMcp.bundle'
$contents = Join-Path $bundle 'Contents'

if (Test-Path $contents) { Remove-Item $contents -Recurse -Force }
New-Item -ItemType Directory -Force $contents | Out-Null
Copy-Item "$root\PackageContents.xml" $bundle -Force
Copy-Item "$out\*.dll", "$out\*.pdb", "$out\*.json" $contents -Force
Copy-Item "$loaderOut\Civil3dMcp.Loader.dll", "$loaderOut\Civil3dMcp.Loader.pdb" $contents -Force

Write-Host "Instalado em $bundle"
Get-ChildItem $contents | Select-Object Name, Length | Format-Table -AutoSize
