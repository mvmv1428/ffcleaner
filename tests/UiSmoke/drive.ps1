param([int]$TargetPid, [string]$RegistryId, [string]$ExpectedRuntime = "")
# Conduce la aplicación real mediante UI Automation: sin atajos internos ni controles ocultos.
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$OutputEncoding = [Console]::OutputEncoding
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]

$script:root = $null
$until = (Get-Date).AddSeconds(40)
while ((Get-Date) -lt $until) {
    $script:root = $AE::RootElement.FindFirst($Scope::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $TargetPid)))
    if ($script:root) { break }
    Start-Sleep -Milliseconds 250
}
if (!$script:root) { throw 'La ventana de FFCleaner no apareció' }

function Find([string]$id) { $script:root.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))) }
function Element([string]$id) { $x = Find $id; if (!$x) { throw "Falta el control: $id" }; return $x }
function Invoke([string]$id) {
    $end = (Get-Date).AddSeconds(15)
    do { $x = Element $id; if ($x.Current.IsEnabled) { break }; Start-Sleep -Milliseconds 100 } while ((Get-Date) -lt $end)
    if (!$x.Current.IsEnabled) { throw "Control deshabilitado: $id" }
    $x.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Toggle([string]$id) { (Element $id).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
function State([string]$id) { (Element $id).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState.ToString() }
function Text([string]$id) { (Element $id).Current.Name }
function Await([string]$id, [string]$needle, [int]$seconds = 60) {
    $end = (Get-Date).AddSeconds($seconds)
    do { $s = Text $id; if ($s -like "*$needle*") { Write-Output "$id => $s"; return }; Start-Sleep -Milliseconds 150 } while ((Get-Date) -lt $end)
    throw "No se alcanzó «$needle» en $id; actual: $s"
}
function AwaitEnabled([string]$id, [bool]$enabled) {
    $end = (Get-Date).AddSeconds(15)
    do { if ((Element $id).Current.IsEnabled -eq $enabled) { return }; Start-Sleep -Milliseconds 100 } while ((Get-Date) -lt $end)
    throw "$id debía quedar con IsEnabled=$enabled"
}
function Pick([string]$app) { (Element ('app-' + $app)).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 200 }
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Mouse {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  public static void Click(int x, int y) { SetCursorPos(x, y); mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
}
'@
# Los clics son reales: si otra ventana está en primer plano, el clic no llega a FFCleaner. Se comprueba antes
# de pulsar en lugar de dejar que falle una aserción que no explica el motivo.
function FocusSut() {
    $handle = [IntPtr]$script:root.Current.NativeWindowHandle
    $end = (Get-Date).AddSeconds(6)
    do {
        [Mouse]::SetForegroundWindow($handle) | Out-Null
        Start-Sleep -Milliseconds 150
        if ([Mouse]::GetForegroundWindow() -eq $handle) { return }
    } while ((Get-Date) -lt $end)
    throw 'No se pudo traer la ventana de FFCleaner al primer plano: otra ventana tiene el foco y los clics reales no llegarían'
}
# Clic real de ratón dentro de la tarjeta de una categoría, a la derecha de su casilla (sobre el texto).
function ClickCard([string]$id) {
    FocusSut
    $r = (Element $id).Current.BoundingRectangle
    [Mouse]::Click([int]($r.Right + 160), [int]($r.Top + $r.Height / 2 + 14)); Start-Sleep -Milliseconds 300
}
function Check([bool]$condition, [string]$message) { if (!$condition) { throw "FALLO: $message" }; Write-Output "OK $message" }

# U1: tres secciones (Limpieza, Registro e Inicio de Windows); ninguna de las páginas retiradas, ni renombrada ni oculta.
$buttons = $script:root.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
$nav = @($buttons | Where-Object { $_.Current.AutomationId -like 'Nav*' } | ForEach-Object { $_.Current.AutomationId })
Check (($nav -join ',') -eq 'NavCleanupButton,NavRegistryButton,NavStartupButton,NavSettingsButton') "navegación con cuatro secciones: $($nav -join ',')"
foreach ($old in 'NavScanButton', 'NavExclusionsButton', 'PreparePreviewButton', 'ExportPreviewButton', 'ValidateButton', 'SaveExclusionsButton', 'ExclusionsBox', 'PreviewText') {
    Check (-not (Find $old)) "control retirado ausente: $old"
}
$all = $script:root.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$names = @($all | ForEach-Object { $_.Current.Name }) -join "`n"
foreach ($word in 'Vista previa', 'Exclusiones', 'Analizar espacio') { Check ($names -notlike "*$word*") "sin texto de sección retirada: $word" }
Check ((Text 'ModeText') -like '*SANDBOX*') 'la aplicación declara el modo sandbox'

# U2 Limpieza: marcar → analizar → cambiar selección (invalida) → reanalizar → limpiar → resultado, en la misma pantalla.
Check (-not (Element 'CleanButton').Current.IsEnabled) 'sin análisis no se puede limpiar'
Check (-not (Element 'AnalyzeButton').Current.IsEnabled) 'sin selección no se puede analizar'
Invoke 'SelectAllButton'; Await 'SelectionText' '13 objetivos'
Check ((State 'chk-chrome') -eq 'On' -and (State 'chk-brave') -eq 'On') '«Marcar todo» marca todas las aplicaciones'
Invoke 'ClearSelectionButton'; Await 'SelectionText' 'Nada marcado'
Check ((State 'chk-chrome') -eq 'Off' -and (State 'chk-brave') -eq 'Off') '«Desmarcar todo» lo deja todo sin marcar'
# Clic real en la casilla de una aplicación: la marca y además muestra sus categorías.
FocusSut
$box = (Element 'chk-brave').Current.BoundingRectangle
[Mouse]::Click([int]($box.Left + $box.Width / 2), [int]($box.Top + $box.Height / 2)); Start-Sleep -Milliseconds 300
Check ((State 'chk-brave') -eq 'On' -and (Text 'SelectedAppTitle') -eq 'Brave') 'la casilla de la aplicación la marca y muestra sus categorías'
[Mouse]::Click([int]($box.Left + $box.Width / 2), [int]($box.Top + $box.Height / 2)); Start-Sleep -Milliseconds 300
Check ((State 'chk-brave') -eq 'Off') 'otro clic en la casilla la desmarca'
Await 'SelectionText' 'Nada marcado'
Pick 'chrome'
Check ((Text 'SelectedAppTitle') -eq 'Google Chrome') 'al elegir una aplicación se muestran sus categorías'
ClickCard 'chk-chrome-cache'
Check ((State 'chk-chrome-cache') -eq 'On') 'un clic en la tarjeta de la categoría la marca'
ClickCard 'chk-chrome-cache'
Check ((State 'chk-chrome-cache') -eq 'Off') 'otro clic en la tarjeta la desmarca'
Await 'SelectionText' 'Nada marcado'
Toggle 'chk-chrome-cache'
Await 'SelectionText' '2 objetivos'
Check ((State 'chk-chrome-cache-Default') -eq 'On' -and (State 'chk-chrome-cache-Profile_1') -eq 'On') 'la categoría marca sus perfiles'
Check ((State 'chk-chrome') -eq 'Indeterminate') 'Chrome queda en estado parcial'
Check (-not (Find 'chk-brave-cache')) 'solo se muestran las categorías de la aplicación elegida'
Pick 'brave'
Check ((State 'chk-brave') -eq 'Off' -and (State 'chk-brave-cache') -eq 'Off' -and (State 'chk-brave-cache-Default') -eq 'Off') 'marcar Chrome no marca Brave'
Invoke 'AnalyzeButton'; Await 'CleanupStatus' 'Análisis completo'
AwaitEnabled 'CleanButton' $true
Check ((Text 'HeroLabel') -eq 'Puedes liberar' -and (Text 'SizeText') -match '\d') "tras analizar se ve cuánto se puede liberar: $(Text 'SizeText')"
Check (-not (Find 'DetailGrid')) 'sin lista de archivos individuales'
Write-Output ("TIMINGS " + (Text 'TimingText'))
Toggle 'chk-brave-cache-Default'
Await 'CleanupStatus' 'selección cambió'
AwaitEnabled 'CleanButton' $false
Check ((Text 'FilesText') -eq '—') 'los resultados obsoletos se retiran'
Toggle 'chk-brave-cache-Default'
Pick 'chrome'
Check ((State 'chk-chrome-cache') -eq 'On') 'desmarcar Brave no desmarca Chrome'
AwaitEnabled 'CleanButton' $false
Write-Output 'OK volver a la selección anterior no revive el análisis'
Invoke 'AnalyzeButton'; Await 'CleanupStatus' 'Análisis completo'
Invoke 'CleanButton'
Await 'ConfirmCleanText' 'Se eliminarán'
Invoke 'ConfirmCleanButton'; Await 'CleanupStatus' 'Limpieza completada'
Check ((Text 'CleanupStatus') -like '*2 carpetas vac*retiradas*') 'las carpetas que quedaron vacías se retiran y se informa'
Await 'HeroHint' 'quedó limpia'
Check ((Text 'HeroHint') -like '*Comprobado*') 'tras limpiar se vuelve a analizar solo y la tabla muestra lo que queda'
AwaitEnabled 'CleanButton' $false

# U2/U4 Registro: familias → analizar → marcar seguros → reparar (respaldo) → restaurar, en la misma pantalla.
Invoke 'NavRegistryButton'
Check ((State 'fam-startup') -eq 'On') 'familias marcadas por defecto'
Check ((State 'OnlyRepairableBox') -eq 'On') '«Solo reparables» activado por defecto'
Check (-not (Find 'RegistryLog') -and -not (Find 'RegistrySummary')) 'sin la caja de resultado y resumen del análisis'
Check (-not (Element 'RepairButton').Current.IsEnabled) 'sin análisis no se puede reparar'
Invoke 'ScanRegistryButton'; Await 'RegistryStatus' 'Registro analizado'
Check ((Text 'RegistryStatus') -like '*6 reparables*1 solo informativos (ocultos*') 'el análisis separa reparables de informativos'
Check ((State 'OnlyRepairableBox') -eq 'On') 'con reparables e informativos mezclados se muestran primero solo los reparables'
Check (-not (Element 'RepairButton').Current.IsEnabled) 'sin hallazgos marcados no se puede reparar'
Invoke 'SelectSafeButton'; Await 'RegistryStatus' 'marcados para reparar'
Invoke 'RepairButton'
Await 'ConfirmRegistryText' 'respaldo'
Invoke 'ConfirmRegistryButton'; Await 'RegistryStatus' 'Reparación completada'
$run = "HKCU:\Software\CleanerApp_TestSandbox\$RegistryId\Registry64\HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
$after = Get-ItemProperty -LiteralPath $run
Check ($null -eq $after.Huerfano -and $null -ne $after.Valido) 'tras reparar: el huérfano no está y el válido sigue'
Write-Output 'REPAIRED_STATE_VERIFIED'
Invoke 'RestoreButton'
Await 'ConfirmRegistryText' 'Se restaurará'
Invoke 'ConfirmRegistryButton'; Await 'RegistryStatus' 'Respaldo restaurado'
Check ($null -ne (Get-ItemProperty -LiteralPath $run).Huerfano) 'tras restaurar: el valor vuelve a existir'
Toggle 'fam-startup'
Await 'RegistryStatus' 'familias cambiaron'
Toggle 'fam-startup'
# B1: sección propia «Inicio de Windows» (reversible, sin borrar la entrada), con el nombre real del programa.
Invoke 'NavStartupButton'
Await 'StartupSummary' 'programas'
Check (-not (Find 'RegistryGrid')) 'la sección Registro queda oculta'
$validExe = ((Get-ItemProperty -LiteralPath $run).Valido).Trim('"')
$expectedName = (Get-Item -LiteralPath $validExe).VersionInfo.FileDescription
if ($expectedName) {
    $end = (Get-Date).AddSeconds(15); $found = $null
    do {
        $found = $script:root.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $expectedName)))
        if ($found) { break }; Start-Sleep -Milliseconds 150
    } while ((Get-Date) -lt $end)
    Check ($null -ne $found) "se muestra el nombre real del programa: $expectedName"
}
Check ((State 'startup-Valido') -eq 'On') 'el programa de inicio aparece activo'
$approved = "HKCU:\Software\CleanerApp_TestSandbox\$RegistryId\Registry64\HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"
Toggle 'startup-Valido'
Await 'StartupStatus' 'ya no se iniciar'
Check ((Get-ItemProperty -LiteralPath $approved).Valido[0] -eq 3) 'desactivado en StartupApproved (03)'
Check ($null -ne (Get-ItemProperty -LiteralPath $run).Valido) 'la entrada de Run sigue intacta'
Toggle 'startup-Valido'
Await 'StartupStatus' 'se iniciar'
Check ((Get-ItemProperty -LiteralPath $approved).Valido[0] -eq 2) 'reactivado (02)'
Check ((Text 'StartupDisabledText') -eq '0') 'el contador de desactivados vuelve a cero'
Invoke 'NavRegistryButton'
Check ((Element 'RegistryGrid').Current.IsOffscreen -eq $false) 'vuelve a la sección Registro'
Invoke 'NavCleanupButton'
Check ((Text 'CleanupStatus') -like '*Limpieza completada*') 'la sección de limpieza conserva su resultado'
Check ((Text 'HeroLabel') -eq 'Liberado') 'tras limpiar se ve lo liberado'
Check ((Text 'TotalFreedText') -like 'Total liberado con FFCleaner: * en 1 limpieza*') "se muestra el total acumulado: $(Text 'TotalFreedText')"

# Configuración: tema oscuro e idioma inglés se aplican al momento y se guardan; luego se vuelve atrás.
Invoke 'NavSettingsButton'
(Element 'ThemeDark').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Await 'SettingsStatus' 'Tema guardado'
(Element 'LangEn').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Await 'SettingsStatus' 'Language saved'
Check ((Text 'NavCleanupButton') -eq 'Custom cleanup') 'la interfaz cambia a inglés sin reiniciar'
Invoke 'NavCleanupButton'
Check ((Text 'CleanupStatus') -like '*Cleaning complete*') 'el resultado de la limpieza se rehace en inglés'
Invoke 'NavSettingsButton'
(Element 'LangEs').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Await 'SettingsStatus' 'Idioma guardado'
(Element 'ThemeLight').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Await 'SettingsStatus' 'Tema guardado'
Check ((Text 'NavCleanupButton') -eq 'Limpieza personalizada') 'vuelve al español'
Write-Output 'SETTINGS_VERIFIED'

if ($ExpectedRuntime) {
    $modules = @((Get-Process -Id $TargetPid).Modules | Where-Object { $_.ModuleName -in @('hostfxr.dll', 'coreclr.dll', 'PresentationNative_cor3.dll', 'wpfgfx_cor3.dll') })
    if (!($modules | Where-Object { $_.ModuleName -eq 'coreclr.dll' })) { throw 'No se pudo verificar el runtime cargado' }
    foreach ($module in $modules) {
        if (!$module.FileName.StartsWith($ExpectedRuntime.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw ('Runtime ajeno al paquete: ' + $module.FileName) }
        Write-Output ('RUNTIME_LOCAL ' + $module.ModuleName + ' ' + $module.FileName)
    }
}
Write-Output 'PASS flujo completo por UI Automation'
