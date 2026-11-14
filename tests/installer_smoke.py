"""Prueba del instalador y el desinstalador en un sandbox (carpetas bajo %TEMP%\\ffcleaner-sandbox-* y Registro
bajo HKCU\\Software\\CleanerApp_TestSandbox). Nunca instala en el perfil real ni toca «Aplicaciones instaladas»."""
import hashlib, json, os, re, shlex, shutil, subprocess, sys, tempfile, time, uuid, winreg
from pathlib import Path

sys.stdout.reconfigure(errors='replace')
base = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(base / 'tools'))
import project_info
version = project_info.version()
setup_exe = base / 'dist' / f'FFCleaner-{version}-Setup.exe'
portable_zip = base / 'dist' / f'FFCleaner-{version}-win-x64.zip'
if not setup_exe.is_file():
    print('FAIL falta el instalador; ejecuta tools/build_portable.py'); sys.exit(1)

run_id = uuid.uuid4().hex
sandbox = Path(tempfile.gettempdir()) / ('ffcleaner-sandbox-' + run_id)
reg_rel = r'Software\CleanerApp_TestSandbox' + '\\' + run_id
reg_root = reg_rel + r'\Uninstall'
dest, start, desk, data = sandbox / 'Programs' / 'FFCleaner', sandbox / 'Start', sandbox / 'Desktop', sandbox / 'Data'
checks = []


def check(condition, message):
    if not condition:
        raise AssertionError(message)
    checks.append(message)
    print('OK ' + message)


def install(*extra, target=None, expect_ok=True):
    r = subprocess.run([str(wizard), '--quiet', '--no-launch', '--dest', str(target or dest), '--start-menu', str(start), '--desktop-dir', str(desk),
                        '--registry-root', reg_root, '--data-dir', str(data), *extra], capture_output=True, timeout=600)
    if expect_ok and r.returncode:
        raise AssertionError('instalación falló: ' + r.stderr.decode('utf-8', 'replace'))
    return r.returncode


def reg_values():
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, reg_root + r'\FFCleaner') as k:
            return {winreg.EnumValue(k, i)[0]: winreg.EnumValue(k, i)[1] for i in range(winreg.QueryInfoKey(k)[1])}
    except FileNotFoundError:
        return None


def link_target(link: Path) -> str:
    out = subprocess.run(['powershell.exe', '-NoProfile', '-Command', f"(New-Object -ComObject WScript.Shell).CreateShortcut('{link}').TargetPath"], capture_output=True)
    return out.stdout.decode('utf-8', 'replace').strip()


def relocated_copies():
    return {p.name for p in Path(tempfile.gettempdir()).glob('ffcleaner-uninstall-*')}


def uninstall_quiet(*extra):
    """Ejecuta el desinstalador silencioso registrado en Windows y espera a que su copia temporal termine."""
    before = relocated_copies()
    parts = [p.strip('"') for p in shlex.split(reg_values()['QuietUninstallString'], posix=False)]
    r = subprocess.run(parts + list(extra), capture_output=True, timeout=120)
    if r.returncode:
        raise AssertionError('desinstalación falló: ' + r.stderr.decode('utf-8', 'replace'))
    end = time.time() + 90
    while time.time() < end and (reg_values() is not None or (dest / 'FFCleaner.exe').exists()):
        time.sleep(0.5)
    end = time.time() + 30
    while time.time() < end and relocated_copies() - before:
        time.sleep(0.5)
    return relocated_copies() - before


def reg_delete(path):
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path, 0, winreg.KEY_ALL_ACCESS) as h:
            for name in [winreg.EnumKey(h, i) for i in range(winreg.QueryInfoKey(h)[0])]:
                reg_delete(path + '\\' + name)
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, path)
    except FileNotFoundError:
        pass


try:
    sandbox.mkdir(parents=True)
    (sandbox / '.ffcleaner-sandbox').write_text('Carpeta de pruebas de FFCleaner. Se puede borrar.', encoding='utf-8')
    data.mkdir()
    (data / 'settings.json').write_text('{}', encoding='utf-8')

    # 1. El .exe de IExpress lleva el ZIP portable verificado y el lanzador del asistente.
    extracted = sandbox / 'extraido'
    r = subprocess.run([str(setup_exe), '/C', f'/T:{extracted}', '/Q'], timeout=600)
    names = sorted(p.name for p in extracted.iterdir()) if extracted.exists() else []
    check(r.returncode == 0 and names == ['payload.zip', 'setup.cmd'], f'el instalador contiene el paquete y el lanzador: {names}')
    check(hashlib.sha256((extracted / 'payload.zip').read_bytes()).hexdigest() == hashlib.sha256(portable_zip.read_bytes()).hexdigest(), 'la carga es idéntica al ZIP portable verificado')
    cmd = (extracted / 'setup.cmd').read_text(encoding='ascii')
    check('tar.exe' in cmd and f'FFCleaner-{version}-win-x64\\FFCleaner.Setup.exe' in cmd and 'start "" /wait' in cmd, 'el lanzador descomprime y espera al asistente')
    subprocess.run([os.path.join(os.environ['SystemRoot'], 'System32', 'tar.exe'), '-xf', str(extracted / 'payload.zip'), '-C', str(extracted)], check=True)
    wizard = extracted / f'FFCleaner-{version}-win-x64' / 'FFCleaner.Setup.exe'
    check(wizard.is_file(), 'el asistente FFCleaner.Setup.exe viaja en el paquete')

    # 2. Instalación silenciosa con acceso en el escritorio.
    install('--desktop')
    sums = json.loads((dest / 'SHA256SUMS.json').read_text(encoding='utf-8'))
    bad = [n for n, h in sums.items() if hashlib.sha256((dest / n).read_bytes()).hexdigest() != h]
    check(not bad and len(sums) > 400, f'{len(sums)} archivos instalados con su huella correcta')
    exe = dest / 'FFCleaner.exe'
    check(link_target(start / 'FFCleaner.lnk').lower() == str(exe).lower() and link_target(desk / 'FFCleaner.lnk').lower() == str(exe).lower(), 'accesos directos en Inicio y escritorio')
    values = reg_values()
    uninstaller = str(dest / 'FFCleaner.Setup.exe')
    check(values and values['DisplayName'] == 'FFCleaner' and values['DisplayVersion'] == version and values['EstimatedSize'] > 50_000 and
          values['UninstallString'] == f'"{uninstaller}" --uninstall' and values['QuietUninstallString'] == f'"{uninstaller}" --uninstall --quiet' and values['NoModify'] == 1,
          'registrado en «Aplicaciones instaladas» con un desinstalador .exe')

    # 3. Lo instalado funciona con su propio runtime: el servidor MCP responde.
    mcp = subprocess.run([str(dest / 'FFCleaner.Mcp.exe'), '--sandbox', str(sandbox), '--sandbox-registry', run_id],
                         input=b'{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}\n', capture_output=True, timeout=120)
    check(b'"serverInfo"' in mcp.stdout, 'el servidor MCP instalado arranca y responde')

    # 4. Actualizar sin escritorio; un archivo propio del usuario en la carpeta se respeta.
    (dest / 'mis-notas.txt').write_text('mío', encoding='utf-8')
    install('--no-desktop')
    check(not (desk / 'FFCleaner.lnk').exists() and (start / 'FFCleaner.lnk').exists(), 'al actualizar sin escritorio se retira ese acceso')
    check((dest / 'mis-notas.txt').read_text(encoding='utf-8') == 'mío' and exe.is_file(), 'actualizar no borra archivos ajenos a FFCleaner')
    check([p.name for p in dest.parent.iterdir()] == [dest.name], 'la actualización no deja carpetas de preparación ni versiones apartadas')

    # 4b. Si la carpeta instalada no se puede sustituir (un archivo en uso), la versión instalada sigue intacta.
    installed = {n: hashlib.sha256((dest / n).read_bytes()).hexdigest() for n in ['FFCleaner.exe', 'FFCleaner.dll', 'install.json']}
    with open(dest / 'mis-notas.txt', 'rb'):
        code = install('--no-desktop', expect_ok=False)
    check(code != 0 and all(hashlib.sha256((dest / n).read_bytes()).hexdigest() == h for n, h in installed.items()) and
          all((dest / n).is_file() for n in sums), 'una actualización que no puede sustituir la carpeta deja la versión instalada intacta')
    check([p.name for p in dest.parent.iterdir()] == [dest.name], 'y no deja restos de la versión nueva')

    # 4c. install.json se puede editar: el desinstalador solo borra accesos «FFCleaner.lnk» que abren este FFCleaner.
    ajeno = sandbox / 'Documentos' / 'tesis.txt'
    ajeno.parent.mkdir()
    ajeno.write_text('no tocar', encoding='utf-8')
    manifest = json.loads((dest / 'install.json').read_text(encoding='utf-8'))
    manifest['Shortcuts'].append(str(ajeno))
    (dest / 'install.json').write_text(json.dumps(manifest), encoding='utf-8')

    # 5. Desinstalar con el comando silencioso registrado: el .exe se copia a %TEMP%, borra la instalación y se borra solo.
    leftovers = uninstall_quiet()
    check(reg_values() is None and not (start / 'FFCleaner.lnk').exists(), 'el desinstalador .exe quita registro y accesos directos')
    check(not exe.exists() and sorted(p.name for p in dest.iterdir()) == ['mis-notas.txt'], 'quita todos los archivos instalados, incluido el propio desinstalador')
    check((data / 'settings.json').exists(), 'los datos y respaldos del usuario se conservan por defecto')
    check(not leftovers, 'la copia temporal del desinstalador se borra sola')
    check(ajeno.read_text(encoding='utf-8') == 'no tocar', 'un archivo añadido a install.json como «acceso directo» no se borra')
    (dest / 'mis-notas.txt').unlink()

    # 6. Reinstalar y desinstalar borrando también los datos.
    install()
    uninstall_quiet('--remove-data')
    check(not dest.exists() and not data.exists(), 'con --remove-data se borran también los datos y la carpeta')

    # 7. Asistente con clics reales: todo en la misma ventana, sin ventanas emergentes.
    data.mkdir()
    ui = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(base / 'tests' / 'installer_ui.ps1'),
                         '-Setup', str(wizard), '-Dest', str(dest), '-Start', str(start), '-Desk', str(desk), '-Reg', reg_root, '-Data', str(data),
                         '-RealSetup', str(setup_exe)],
                        capture_output=True, timeout=600)
    out = ui.stdout.decode('utf-8', 'replace')
    print(out.strip())
    if ui.returncode:
        raise AssertionError('asistente: ' + ui.stderr.decode('utf-8', 'replace')[-1500:])
    check('REAL_SETUP_OPENS_WIZARD' in out, 'el Setup.exe real abre el asistente (y Cancelar lo cierra sin instalar)')
    check('WIZARD_INSTALL_OK' in out and 'WIZARD_UNINSTALL_OK' in out, 'asistente de instalación y de desinstalación por pasos, sin ventanas emergentes')
    end = time.time() + 60
    while time.time() < end and (dest / 'FFCleaner.exe').exists():
        time.sleep(0.5)
    check(not (dest / 'FFCleaner.exe').exists() and reg_values() is None, 'el asistente de desinstalación deja el equipo como estaba')

    # 8. No instala encima de una carpeta ajena con contenido.
    foreign = sandbox / 'Ajena'
    foreign.mkdir()
    (foreign / 'documento.txt').write_text('no tocar', encoding='utf-8')
    code = install(target=foreign, expect_ok=False)
    check(code != 0 and (foreign / 'documento.txt').read_text(encoding='utf-8') == 'no tocar' and len(list(foreign.iterdir())) == 1, 'rechaza una carpeta no vacía que no es de FFCleaner')

    print('PASS instalador y desinstalador')
    print(json.dumps({'status': 'PASS', 'checks': len(checks), 'instalador_MiB': round(setup_exe.stat().st_size / 1024 ** 2, 2)}, ensure_ascii=False))
finally:
    reg_delete(reg_rel)
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Software\CleanerApp_TestSandbox') as h:
            empty = winreg.QueryInfoKey(h)[0] == 0
        if empty:
            winreg.DeleteKey(winreg.HKEY_CURRENT_USER, r'Software\CleanerApp_TestSandbox')
    except FileNotFoundError:
        pass
    if sandbox.name.startswith('ffcleaner-sandbox-') and (sandbox / '.ffcleaner-sandbox').is_file():
        shutil.rmtree(sandbox, ignore_errors=True)
