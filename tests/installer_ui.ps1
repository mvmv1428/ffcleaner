param([string]$Setup, [string]$Dest, [string]$Start, [string]$Desk, [string]$Reg, [string]$Data, [string]$RealSetup)
# Recorre el asistente real con UI Automation: instalar → terminado → finalizar; desinstalar → terminado → cerrar.
# Comprueba que todo ocurre dentro de la misma ventana (ninguna ventana emergente).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]; $S = [System.Windows.Automation.TreeScope]

function Window([string]$title) {
    $end = (Get-Date).AddSeconds(60)
    do {
        $w = $AE::RootElement.FindFirst($S::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $title)))
        if ($w) { return $w }; Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $end)
    throw "No apareció la ventana «$title»"
}
function El($w, [string]$id) { $w.FindFirst($S::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))) }
function Click($w, [string]$id) {
    $end = (Get-Date).AddSeconds(30)
    do { $e = El $w $id; if ($e -and $e.Current.IsEnabled) { break }; Start-Sleep -Milliseconds 150 } while ((Get-Date) -lt $end)
    $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Await($w, [string]$id, [string]$needle) {
    $end = (Get-Date).AddSeconds(120)
    do { $e = El $w $id; if ($e -and $e.Current.Name -like "*$needle*") { return $e.Current.Name }; Start-Sleep -Milliseconds 200 } while ((Get-Date) -lt $end)
    throw "No se alcanzó «$needle» en $id"
}
function WindowsOf([int]$processId) {
    @($AE::RootElement.FindAll($S::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $processId)))).Count
}

# El Setup.exe real (IExpress → lanzador → asistente) abre el asistente; se cancela sin instalar nada.
if ($RealSetup) {
    $real = Start-Process $RealSetup -PassThru
    $w = Window 'Instalar FFCleaner'
    Click $w 'SecondaryButton'
    if (-not $real.WaitForExit(60000)) { throw 'El Setup.exe no terminó al cancelar el asistente' }
    Write-Output 'REAL_SETUP_OPENS_WIZARD'
}

# Instalación con el asistente.
$p = Start-Process $Setup -ArgumentList @('--dest', "`"$Dest`"", '--start-menu', "`"$Start`"", '--desktop-dir', "`"$Desk`"", '--registry-root', "`"$Reg`"", '--data-dir', "`"$Data`"", '--no-launch') -PassThru
$w = Window 'Instalar FFCleaner'
if ((El $w 'PrimaryButton').Current.Name -ne 'Instalar') { throw 'El botón principal debía decir Instalar' }
Click $w 'PrimaryButton'
$done = Await $w 'DoneTitle' 'está instalado'
Write-Output "INSTALL_DONE $done"
if ((WindowsOf $p.Id) -ne 1) { throw 'Apareció una ventana emergente al terminar la instalación' }
if ((El $w 'LaunchBox').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne 'Off') { throw 'Con --no-launch, «Abrir FFCleaner» debía estar desmarcado' }
if ((El $w 'PrimaryButton').Current.Name -ne 'Finalizar') { throw 'El botón final debía decir Finalizar' }
Click $w 'PrimaryButton'
if (-not $p.WaitForExit(15000)) { throw 'El asistente no se cerró al pulsar Finalizar' }
Write-Output 'WIZARD_INSTALL_OK'

# Desinstalación con el asistente (se relanza desde una copia temporal).
Start-Process (Join-Path $Dest 'FFCleaner.Setup.exe') -ArgumentList '--uninstall' | Out-Null
$w = Window 'Desinstalar FFCleaner'
$pid2 = $w.Current.ProcessId
Click $w 'PrimaryButton'
$done = Await $w 'DoneTitle' 'se desinstaló'
Write-Output "UNINSTALL_DONE $done"
if ((WindowsOf $pid2) -ne 1) { throw 'Apareció una ventana emergente al terminar la desinstalación' }
Click $w 'PrimaryButton'
Write-Output 'WIZARD_UNINSTALL_OK'
