"""Prueba de extremo a extremo de la aplicación real mediante UI Automation.

Todo ocurre en un sandbox: archivos bajo %TEMP%\\ffcleaner-sandbox-* y Registro bajo
HKCU\\Software\\CleanerApp_TestSandbox\\<id>. No se toca ningún perfil ni clave real.
Con CLARO_PORTABLE_EXE se prueba el paquete portable usando solo su runtime incluido.
"""
import hashlib, json, os, shutil, subprocess, sys, tempfile, uuid, winreg
sys.stdout.reconfigure(errors="replace")
from pathlib import Path

base = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'tools'))
import project_info
sdk = project_info.dotnet()
dll = base / 'src/FFCleaner.Desktop/bin/Release/net10.0-windows/FFCleaner.dll'
app_exe = os.environ.get('CLARO_PORTABLE_EXE')
launch = [app_exe] if app_exe else [str(sdk), str(dll)]
if not Path(app_exe or dll).is_file():
    print('FAIL falta el ejecutable de la aplicación'); sys.exit(1)

run_id = uuid.uuid4().hex
sandbox = Path(tempfile.gettempdir()) / ('ffcleaner-sandbox-' + run_id)
registry_root = r'Software\CleanerApp_TestSandbox' + '\\' + run_id
counter = [0]


def write(path: Path, size: int = 256) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    counter[0] += 1
    path.write_bytes(hashlib.sha256(str(counter[0]).encode()).digest() * (size // 32 + 1))
    old = 1_700_000_000  # fecha antigua fija
    os.utime(path, (old, old))


def profile(user_data: Path, name: str) -> None:
    p = user_data / name
    for personal in ['Preferences', 'Cookies', 'History', 'Login Data', 'Web Data', 'Bookmarks', 'Network/Cookies',
                     'Sessions/Session_1', 'Local Storage/leveldb/000005.ldb', 'Extensions/abc/1.0_0/manifest.json']:
        write(p / personal)
    for i in range(40):
        write(p / 'Cache' / 'Cache_Data' / f'f_{i:06x}', 2048)
    write(p / 'Cache' / 'Cache_Data' / 'index', 512)
    for i in range(10):
        write(p / 'Code Cache' / 'js' / f'{i:016x}_0', 700)
    write(p / 'GPUCache' / 'data_0', 4096)


def tree(path: Path) -> dict:
    return {str(f.relative_to(path)): hashlib.sha256(f.read_bytes()).hexdigest() for f in sorted(path.rglob('*')) if f.is_file()}


def reg_set(hive: str, key: str, name: str, value, kind=winreg.REG_SZ) -> None:
    full = registry_root + '\\Registry64\\' + hive + '\\' + key
    with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, full, 0, winreg.KEY_WRITE) as handle:
        winreg.SetValueEx(handle, name, 0, kind, value)


def reg_dump(sub: str) -> dict:
    result = {}

    def walk(path: str) -> None:
        try:
            handle = winreg.OpenKey(winreg.HKEY_CURRENT_USER, path)
        except FileNotFoundError:
            return
        with handle:
            subkeys, values, _ = winreg.QueryInfoKey(handle)
            result[path] = sorted((winreg.EnumValue(handle, i)[0], repr(winreg.EnumValue(handle, i)[1]), winreg.EnumValue(handle, i)[2]) for i in range(values))
            for name in [winreg.EnumKey(handle, i) for i in range(subkeys)]:
                walk(path + '\\' + name)

    walk(registry_root + '\\' + sub)
    return result


def reg_delete(path: str) -> None:
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path, 0, winreg.KEY_ALL_ACCESS) as handle:
            for name in [winreg.EnumKey(handle, i) for i in range(winreg.QueryInfoKey(handle)[0])]:
                reg_delete(path + '\\' + name)
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, path)
    except FileNotFoundError:
        pass


process = None
try:
    # ── Fixture de archivos ──
    for folder in ['Local', 'Roaming', 'Temp', 'State']:
        (sandbox / folder).mkdir(parents=True)
    (sandbox / '.ffcleaner-sandbox').write_text('Carpeta de pruebas de FFCleaner. Se puede borrar.', encoding='utf-8')
    chrome = sandbox / 'Local/Google/Chrome/User Data'
    brave = sandbox / 'Local/BraveSoftware/Brave-Browser/User Data'
    for user_data in (chrome, brave):
        profile(user_data, 'Default')
        profile(user_data, 'Profile 1')
    before_chrome, before_brave = tree(chrome), tree(brave)

    # ── Fixture de Registro (solo bajo la clave de pruebas) ──
    missing = str(sandbox / 'desinstalado')
    present = sys.executable
    cv = r'Software\Microsoft\Windows\CurrentVersion'
    reg_set('HKCU', cv + r'\Run', 'Huerfano', f'"{missing}\\inicio.exe" --min')
    reg_set('HKCU', cv + r'\Run', 'Valido', f'"{present}"')
    reg_set('HKCU', cv + r'\Run', 'EnRed', r'\\servidor-inexistente\recurso\app.exe')   # ambiguo: se muestra, no se repara
    reg_set('HKCU', cv + r'\App Paths\huerfana.exe', '', missing + r'\huerfana.exe')
    reg_set('HKCU', cv + r'\App Paths\valida.exe', '', present)
    reg_set('HKCU', cv + r'\Uninstall\AppHuerfana', 'UninstallString', f'"{missing}\\unins000.exe"')
    reg_set('HKCU', cv + r'\Uninstall\AppHuerfana', 'DisplayName', 'Aplicación Huérfana')
    reg_set('HKCU', cv + r'\Uninstall\AppHuerfana', 'InstallLocation', missing)
    reg_set('HKCU', r'Software\Classes\.clarohuerfana', '', 'FFCleanerTest.Ausente')
    reg_set('HKCU', r'Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache', missing + r'\viejo.exe.FriendlyAppName', 'Viejo')
    reg_set('HKLM', cv + r'\SharedDLLs', missing + r'\compartida.dll', 1, winreg.REG_DWORD)
    reg_set('HKLM', cv + r'\SharedDLLs', present, 1, winreg.REG_DWORD)
    before_user, before_machine = reg_dump(r'Registry64\HKCU'), reg_dump(r'Registry64\HKLM\Software\Microsoft')

    env = dict(os.environ)
    if app_exe:
        # El paquete portable debe arrancar con su propio runtime: se anulan las rutas a cualquier .NET global.
        nowhere = str(sandbox / 'sin-runtime-global')
        env.update({'DOTNET_ROOT': nowhere, 'DOTNET_ROOT_X64': nowhere, 'DOTNET_MULTILEVEL_LOOKUP': '0'})
    process = subprocess.Popen([*launch, '--sandbox', str(sandbox), '--sandbox-registry', run_id], cwd=str(sandbox), env=env)
    driver = subprocess.run(['powershell.exe', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', str(Path(__file__).with_name('drive.ps1')),
                             '-TargetPid', str(process.pid), '-RegistryId', run_id,
                             '-ExpectedRuntime', str(Path(app_exe).parent / 'runtime') if app_exe else ''], capture_output=True, timeout=300)
    out = driver.stdout.decode('utf-8', errors='backslashreplace')
    print(out)
    if driver.returncode:
        print(driver.stderr.decode('cp850', errors='replace'))
        raise AssertionError('UI Automation falló')

    # ── Verificación de archivos: solo la caché de Chrome desapareció ──
    after_chrome, after_brave = tree(chrome), tree(brave)
    assert after_brave == before_brave, 'Brave cambió'
    removed = set(before_chrome) - set(after_chrome)
    assert removed and all(('\\Cache\\' in name) for name in removed), sorted(removed)[:5]
    assert len(removed) == 2 * 41, len(removed)
    assert not [n for n in after_chrome if '\\Cache\\' in n], 'quedó caché de Chrome'
    assert all(after_chrome[n] == before_chrome[n] for n in after_chrome), 'algún archivo conservado de Chrome cambió'
    for name in ['Cookies', 'History', 'Login Data', 'Network\\Cookies', 'Sessions\\Session_1']:
        assert ('Default\\' + name) in after_chrome and ('Profile 1\\' + name) in after_chrome, name
    assert any('Code Cache' in n for n in after_chrome) and any('GPUCache' in n for n in after_chrome), 'se limpió una categoría no marcada'
    journals = list((sandbox / 'State/Journal').glob('limpieza-*.jsonl'))
    assert len(journals) == 1 and '"evento":"fin"' in journals[0].read_text(encoding='utf-8'), 'bitácora de limpieza'

    # ── Verificación de Registro: reparación real, respaldo verificado y restauración al estado original ──
    assert 'REPAIRED_STATE_VERIFIED' in out
    backups = list((sandbox / 'State/RegistryBackups').glob('*/respaldo.json'))
    assert len(backups) == 1, backups
    document = json.loads(backups[0].read_text(encoding='utf-8'))
    assert len(document['Entries']) == 6 and len(document['ContentSha256']) == 64, document['Entries']
    repair = json.loads((backups[0].parent / 'reparacion.json').read_text(encoding='utf-8'))
    assert repair['Repaired'] == 6, repair
    assert (backups[0].parent / 'registro.reg').read_bytes()[:2] == b'\xff\xfe'
    assert list(backups[0].parent.glob('restauracion-*.json')), 'registro de la restauración'
    after_user = reg_dump(r'Registry64\HKCU')
    approved = {k: v for k, v in after_user.items() if 'StartupApproved' in k}
    # Fuera de la marca de inicio (y de las claves vacías que la contienen), todo vuelve a su estado original.
    rest = {k: v for k, v in after_user.items() if 'StartupApproved' not in k and (k in before_user or v)}
    assert rest == before_user, 'HKCU del sandbox no volvió al estado original'
    run_approved = [v for k, v in approved.items() if k.endswith('StartupApproved\\Run')][0]
    assert run_approved[0][0] == 'Valido' and run_approved[0][1].startswith("b'\\x02"), run_approved   # reactivado: 02
    assert reg_dump(r'Registry64\HKLM\Software\Microsoft') == before_machine, 'HKLM del sandbox no volvió al estado original'
    log = (sandbox / 'State/Logs/ffcleaner.log').read_text(encoding='utf-8')
    assert 'descubrimiento' in log and 'recorrido' in log and 'metadatos' in log and 'interfaz' in log, 'tiempos por fase en el log'
    settings = json.loads((sandbox / 'State/settings.json').read_text(encoding='utf-8'))
    assert 'chrome/cache/Default' in settings['SelectedTargets'], settings
    assert 'SETTINGS_VERIFIED' in out and settings.get('Theme') == 'light' and settings.get('Language') == 'es', settings

    result = {'status': 'PASS', 'modo': 'portable' if app_exe else 'desarrollo', 'archivos_eliminados': len(removed), 'entradas_reparadas_y_restauradas': 6,
              'checks': ['cuatro_secciones', 'configuracion_tema_idioma', 'espacio_liberable_visible', 'inicio_de_windows_reversible', 'paginas_retiradas_ausentes', 'seleccion_aislada', 'invalidacion_al_cambiar_casilla', 'limpieza_real_solo_cache_chrome',
                         'datos_personales_y_brave_intactos', 'registro_analizar_reparar_respaldar_restaurar', 'estado_original_del_registro']}
    print(json.dumps(result, indent=2, ensure_ascii=False))
finally:
    if process is not None and process.poll() is None:
        process.terminate()
        process.wait(timeout=15)
    reg_delete(registry_root)
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Software\CleanerApp_TestSandbox') as handle:
            empty = winreg.QueryInfoKey(handle)[0] == 0
        if empty:
            winreg.DeleteKey(winreg.HKEY_CURRENT_USER, r'Software\CleanerApp_TestSandbox')
    except FileNotFoundError:
        pass
    if sandbox.name.startswith('ffcleaner-sandbox-') and (sandbox / '.ffcleaner-sandbox').is_file():
        shutil.rmtree(sandbox, ignore_errors=True)
