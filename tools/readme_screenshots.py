"""Genera las capturas del README (screenshots/*.png) con datos de demostración, sin datos reales ni personales.

Crea un sandbox (%TEMP%\\ffcleaner-sandbox-* y HKCU\\Software\\CleanerApp_TestSandbox) con navegadores, cachés y
entradas del Registro inventadas en rutas genéricas (C:\\Program Files\\…), abre FFCleaner sobre él y captura la
ventana con UI Automation. No borra ni repara nada: solo analiza. Uso:  python tools/readme_screenshots.py
"""
import os, shutil, subprocess, sys, tempfile, uuid, winreg
from pathlib import Path

sys.stdout.reconfigure(errors='replace')
base = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(base / 'tools'))
import project_info

out = base / 'screenshots'
run_id = uuid.uuid4().hex
sandbox = Path(tempfile.gettempdir()) / ('ffcleaner-sandbox-' + run_id)
root = r'Software\CleanerApp_TestSandbox' + '\\' + run_id
cv = r'Software\Microsoft\Windows\CurrentVersion'


def write(path: Path, size: int) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(os.urandom(size))
    os.utime(path, (1_700_000_000, 1_700_000_000))


def reg(hive, key, name, value, kind=winreg.REG_SZ):
    with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, root + '\\Registry64\\' + hive + '\\' + key, 0, winreg.KEY_WRITE) as h:
        winreg.SetValueEx(h, name, 0, kind, value)


def reg_delete(path):
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path, 0, winreg.KEY_ALL_ACCESS) as h:
            for n in [winreg.EnumKey(h, i) for i in range(winreg.QueryInfoKey(h)[0])]:
                reg_delete(path + '\\' + n)
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, path)
    except FileNotFoundError:
        pass


DRIVER = r'''
param([int]$TargetPid, [string]$Out)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
$AE = [System.Windows.Automation.AutomationElement]; $S = [System.Windows.Automation.TreeScope]
$root = $null; $until = (Get-Date).AddSeconds(60)
while (-not $root -and (Get-Date) -lt $until) { $root = $AE::RootElement.FindFirst($S::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $TargetPid))); Start-Sleep -Milliseconds 250 }
function El($id) { $end = (Get-Date).AddSeconds(15); do { $e = $root.FindFirst($S::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))); if ($e) { return $e }; Start-Sleep -Milliseconds 200 } while ((Get-Date) -lt $end); throw "falta $id" }
function Click($id) { (El $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 400 }
function Pick($app) { (El ('app-' + $app)).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 300 }
function Choose($id) { (El $id).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 600 }
function Toggle($id) { (El $id).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
function Wait($id, $needle) { $end = (Get-Date).AddSeconds(90); do { if ((El $id).Current.Name -like "*$needle*") { return }; Start-Sleep -Milliseconds 200 } while ((Get-Date) -lt $end); throw "sin $needle" }
function NoToast { $t = $root.FindFirst($S::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'ToastCloseButton'))); if ($t -and -not $t.Current.IsOffscreen) { try { $t.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch {} }; Start-Sleep -Milliseconds 700 }
function Shot($name) { NoToast; $r = $root.Current.BoundingRectangle; $b = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height); $g = [System.Drawing.Graphics]::FromImage($b); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $b.Size); $b.Save((Join-Path $Out $name)); $g.Dispose(); $b.Dispose() }
try { $root.SetFocus() } catch {}

# Limpieza: navegadores, IA y herramientas de desarrollo marcados y analizados.
foreach ($app in 'chrome', 'brave', 'claude', 'dev') { Toggle ('chk-' + $app) }
Pick 'brave'
Click 'AnalyzeButton'; Wait 'CleanupStatus' 'lisis completo'
Shot 'limpieza.png'

# Inicio de Windows.
Click 'NavStartupButton'; Wait 'StartupSummary' 'programas'; Start-Sleep 2
Shot 'inicio.png'
'ok'
'''

proc = None
try:
    for d in ['Local', 'Roaming', 'Temp', 'State', 'Home', 'LocalLow', 'ProgramData', 'Windows']:
        (sandbox / d).mkdir(parents=True)
    (sandbox / '.ffcleaner-sandbox').write_text('Carpeta de pruebas de FFCleaner. Se puede borrar.', encoding='utf-8')
    (sandbox / 'State' / 'settings.json').write_text('{"SelectedTargets":[],"Exclusions":[],"RegistryFamilies":[],"Theme":"dark","Language":"es","AutoUpdate":false}', encoding='utf-8')
    # Navegadores con perfiles y cachés de tamaños distintos.
    for user_data, profiles, scale in [('Local/Google/Chrome/User Data', ['Default', 'Profile 1'], 3), ('Local/BraveSoftware/Brave-Browser/User Data', ['Default'], 5)]:
        for prof in profiles:
            p = sandbox / user_data / prof
            for n in ['Preferences', 'Cookies', 'History', 'Login Data', 'Bookmarks']:
                write(p / n, 512)
            for i in range(80):
                write(p / 'Cache/Cache_Data' / f'f_{i:06x}', 40000 * scale)
            for i in range(40):
                write(p / 'Code Cache/js' / f'{i:016x}_0', 30000 * scale)
            write(p / 'GPUCache/data_1', 900000)
            write(p / 'Service Worker/CacheStorage/a/b', 3_000_000 * scale)
    for leaf in ['Cache/Cache_Data/f_1', 'Code Cache/js/a', 'GPUCache/data_1', 'logs/main.log']:
        write(sandbox / 'Roaming/Claude' / leaf, 6_000_000)
    write(sandbox / 'Local/pip/Cache/http/a/b', 25_000_000)
    write(sandbox / 'Roaming/npm-cache/_cacache/content-v2/sha512/a', 18_000_000)
    write(sandbox / 'Temp/instalador-antiguo.tmp', 4_000_000)

    # Registro con referencias a programas que ya no existen, en rutas genéricas.
    gone = r'C:\Program Files\EditorAntiguo'
    reg('HKCU', cv + r'\App Paths\editor-antiguo.exe', '', gone + r'\editor.exe')
    reg('HKCU', cv + r'\Uninstall\VisorAntiguo', 'DisplayName', 'Visor Antiguo')
    reg('HKCU', cv + r'\Uninstall\VisorAntiguo', 'UninstallString', r'"C:\Program Files\VisorAntiguo\unins000.exe"')
    reg('HKCU', cv + r'\Uninstall\VisorAntiguo', 'InstallLocation', r'C:\Program Files\VisorAntiguo')
    reg('HKCU', r'Software\Classes\.visorantiguo', '', 'VisorAntiguo.Documento')
    reg('HKCU', r'Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache', r'C:\Program Files\Juego Antiguo\juego.exe.FriendlyAppName', 'Juego Antiguo')
    reg('HKLM', cv + r'\SharedDLLs', r'C:\Program Files\Comun Antiguo\comun.dll', 1, winreg.REG_DWORD)
    windir = os.environ['SystemRoot']
    reg('HKCU', cv + r'\Run', 'ActualizadorAntiguo', r'"C:\Program Files\ActualizadorAntiguo\update.exe" --background')
    reg('HKCU', cv + r'\Run', 'BlocDeNotas', '"' + os.path.join(windir, 'System32', 'notepad.exe') + '"')
    reg('HKCU', cv + r'\Run', 'Calculadora', os.path.join(windir, 'System32', 'calc.exe'))
    reg('HKLM', cv + r'\Run', 'Explorador', '"' + os.path.join(windir, 'explorer.exe') + '"')

    out.mkdir(exist_ok=True)
    dll = base / 'src/FFCleaner.Desktop/bin/Release/net10.0-windows/FFCleaner.dll'
    proc = subprocess.Popen([str(project_info.dotnet()), str(dll), '--sandbox', str(sandbox), '--sandbox-registry', run_id])
    script = sandbox / 'capturas.ps1'
    script.write_bytes(b'\xef\xbb\xbf' + DRIVER.encode('utf-8'))
    r = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(script), '-TargetPid', str(proc.pid), '-Out', str(out)],
                       capture_output=True, timeout=300)
    print(r.stdout.decode('utf-8', 'replace').strip(), r.stderr.decode('utf-8', 'replace')[-800:])
    if r.returncode:
        sys.exit(1)
    print('Capturas en', out)
finally:
    if proc and proc.poll() is None:
        proc.terminate()
        proc.wait(15)
    reg_delete(root)
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Software\CleanerApp_TestSandbox') as h:
            empty = winreg.QueryInfoKey(h)[0] == 0
        if empty:
            winreg.DeleteKey(winreg.HKEY_CURRENT_USER, r'Software\CleanerApp_TestSandbox')
    except FileNotFoundError:
        pass
    if sandbox.name.startswith('ffcleaner-sandbox-') and (sandbox / '.ffcleaner-sandbox').is_file():
        shutil.rmtree(sandbox, ignore_errors=True)
