param([string]$Target = "")
# Crea un acceso directo en el Escritorio al FFCleaner.exe del paquete portable más reciente junto a este script,
# o al ejecutable indicado en -Target. No usa rutas fijas: todo se resuelve en tiempo de ejecución.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
if (!$Target) {
    $dist = Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'
    $latest = Get-ChildItem -LiteralPath $dist -Directory -Filter 'FFCleaner-*-win-x64' | Sort-Object Name -Descending | Select-Object -First 1
    if (!$latest) { throw 'No hay ningún paquete portable en dist' }
    $Target = Join-Path $latest.FullName 'FFCleaner.exe'
}
$Target = (Resolve-Path -LiteralPath $Target).Path
if (!(Test-Path -LiteralPath $Target -PathType Leaf)) { throw 'Falta el ejecutable' }
$desktop = [Environment]::GetFolderPath('Desktop')
if (!$desktop -or !(Test-Path -LiteralPath $desktop -PathType Container)) { throw 'Escritorio no disponible' }
$link = Join-Path $desktop 'FFCleaner.lnk'
if (Test-Path -LiteralPath $link) { throw 'El acceso directo ya existe; no se sobrescribe' }
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($link)
$shortcut.TargetPath = $Target
$shortcut.WorkingDirectory = Split-Path -Parent $Target
$shortcut.Description = 'FFCleaner: limpieza personalizada y Registro'
$shortcut.IconLocation = $Target + ',0'
$shortcut.Save()
$readback = $shell.CreateShortcut($link)
if ($readback.TargetPath -ne $Target) { throw 'Falló la verificación del acceso directo' }
[PSCustomObject]@{ Shortcut = $link; Target = $readback.TargetPath; Verified = $true } | ConvertTo-Json -Compress
