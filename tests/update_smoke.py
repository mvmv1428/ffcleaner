"""Prueba de extremo a extremo de la actualización automática, sin GitHub ni Internet.

Instala FFCleaner en un sandbox y publica una «versión 9.9.9» falsa en un servidor HTTP local (127.0.0.1) con el
mismo formato que la API de GitHub. Comprueba que:
  1. si la huella SHA-256 no coincide, no se instala nada y la app sigue abierta;
  2. con otra ventana de FFCleaner abierta, o si esa versión falló hace poco, no se descarga ni se intenta (sin bucles);
  3. con la huella correcta, la app se cierra, el instalador sustituye la instalación y la vuelve a abrir, y la
     versión reabierta muestra el aviso «FFCleaner se actualizó» y retira la descarga de %TEMP%;
  4. si la sustitución falla a mitad (un archivo en uso), la versión instalada sigue intacta, se reabre y no se reintenta.
Todo ocurre bajo %TEMP%\\ffcleaner-sandbox-* y HKCU\\Software\\CleanerApp_TestSandbox.
"""
import functools, hashlib, http.server, json, os, shutil, subprocess, sys, tempfile, threading, time, uuid, winreg
from pathlib import Path

sys.stdout.reconfigure(errors='replace')
base = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(base / 'tools'))
import project_info

version = project_info.version()
portable_zip = base / 'dist' / f'FFCleaner-{version}-win-x64.zip'
if not portable_zip.is_file():
    print('FAIL falta el paquete; ejecuta tools/build_portable.py'); sys.exit(1)

run_id = uuid.uuid4().hex
sandbox = Path(tempfile.gettempdir()) / ('ffcleaner-sandbox-' + run_id)
reg_rel = r'Software\CleanerApp_TestSandbox' + '\\' + run_id
reg_root = reg_rel + r'\Uninstall'
dest = sandbox / 'Programs' / 'FFCleaner'
checks = []


def check(condition, message):
    if not condition:
        raise AssertionError(message)
    checks.append(message)
    print('OK ' + message)


def ps(script: str) -> str:
    r = subprocess.run(['powershell.exe', '-NoProfile', '-Command', script], capture_output=True, timeout=180)
    return r.stdout.decode('utf-8', 'replace').strip()


def app_pids():
    out = ps(f"Get-Process FFCleaner -ErrorAction SilentlyContinue | Where-Object {{ $_.Path -like '{dest}*' }} | ForEach-Object {{ $_.Id }}")
    return {int(x) for x in out.split() if x.isdigit()}


def wait_for(condition, seconds):
    end = time.time() + seconds
    while time.time() < end:
        value = condition()
        if value:
            return value
        time.sleep(0.5)
    return condition()


def toast_text(pid):
    return ps(
        "Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes; $AE=[System.Windows.Automation.AutomationElement]; $S=[System.Windows.Automation.TreeScope];"
        f"$end=(Get-Date).AddSeconds(40); do {{ $w=$AE::RootElement.FindFirst($S::Children,(New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty,{pid})));"
        "if($w){ $t=$w.FindFirst($S::Descendants,(New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty,'ToastTitle'))); if($t -and $t.Current.Name){ $t.Current.Name; break } };"
        "Start-Sleep -Milliseconds 300 } while((Get-Date) -lt $end)")


def close_all():
    for pid in app_pids():
        subprocess.run(['taskkill', '/PID', str(pid), '/F'], capture_output=True)


def reg_delete(path):
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path, 0, winreg.KEY_ALL_ACCESS) as h:
            for name in [winreg.EnumKey(h, i) for i in range(winreg.QueryInfoKey(h)[0])]:
                reg_delete(path + '\\' + name)
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, path)
    except FileNotFoundError:
        pass


class Quiet(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


server = None
try:
    for folder in ['Local', 'Roaming', 'Temp', 'State', 'Home', 'LocalLow', 'ProgramData', 'Windows']:
        (sandbox / folder).mkdir(parents=True)
    (sandbox / '.ffcleaner-sandbox').write_text('Carpeta de pruebas de FFCleaner. Se puede borrar.', encoding='utf-8')

    # Versión actual instalada en el sandbox.
    pkg = sandbox / 'pkg'
    pkg.mkdir()
    subprocess.run([os.path.join(os.environ['SystemRoot'], 'System32', 'tar.exe'), '-xf', str(portable_zip), '-C', str(pkg)], check=True)
    wizard = pkg / f'FFCleaner-{version}-win-x64' / 'FFCleaner.Setup.exe'
    r = subprocess.run([str(wizard), '--quiet', '--no-launch', '--dest', str(dest), '--start-menu', str(sandbox / 'Start'), '--no-desktop',
                        '--registry-root', reg_root, '--data-dir', str(sandbox / 'State')], capture_output=True, timeout=600)
    check(r.returncode == 0 and (dest / 'FFCleaner.exe').is_file(), f'versión {version} instalada en el sandbox')

    # «Versión 9.9.9» publicada en un servidor local con el formato de la API de GitHub.
    feed = sandbox / 'feed'
    (feed / 'releases').mkdir(parents=True)
    asset = feed / 'FFCleaner-9.9.9-win-x64.zip'
    shutil.copy2(portable_zip, asset)
    digest = hashlib.sha256(asset.read_bytes()).hexdigest()
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(Quiet, directory=str(feed)))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    port = server.server_address[1]

    def publish(sha, tag='v9.9.9'):
        (feed / 'releases' / 'latest').write_text(json.dumps({'tag_name': tag, 'draft': False, 'prerelease': False, 'assets': [
            {'name': asset.name, 'browser_download_url': f'http://127.0.0.1:{port}/{asset.name}', 'size': asset.stat().st_size, 'digest': 'sha256:' + sha}]}), encoding='utf-8')

    args = ['--sandbox', str(sandbox), '--sandbox-registry', run_id, '--update-feed', f'http://127.0.0.1:{port}/releases/latest']
    log = sandbox / 'State' / 'Logs' / 'ffcleaner.log'

    # 1. Huella incorrecta: no se instala nada y la app sigue abierta.
    publish('0' * 64)
    (dest / 'LEEME.txt').unlink()
    first = subprocess.Popen([str(dest / 'FFCleaner.exe'), *args])
    found = wait_for(lambda: log.exists() and 'no coincide' in log.read_text(encoding='utf-8', errors='replace'), 120)
    check(found and first.poll() is None, 'con una huella SHA-256 incorrecta no se instala nada y la app sigue abierta')
    check(not (dest / 'LEEME.txt').exists(), 'la instalación no se tocó')
    first.kill(); first.wait(30)

    def log_text():
        return log.read_text(encoding='utf-8', errors='replace') if log.exists() else ''

    def updates_in_temp():
        return {p.name for p in Path(tempfile.gettempdir()).glob('ffcleaner-update-*')}

    # 1b. Con otra ventana abierta no se descarga nada (antes, el instalador fallaba y reabría FFCleaner en bucle).
    publish(digest, tag='v0.0.1')
    other = subprocess.Popen([str(dest / 'FFCleaner.exe'), *args])
    time.sleep(3)
    check(other.poll() is None, 'una primera ventana abierta (sin versión nueva)')
    publish(digest)
    temp_before = updates_in_temp()
    second_window = subprocess.Popen([str(dest / 'FFCleaner.exe'), *args])
    skipped = wait_for(lambda: 'está abierto otra ventana de FFCleaner' in log_text(), 60)
    time.sleep(3)
    check(skipped and second_window.poll() is None and other.poll() is None, 'con otra ventana abierta no se actualiza y la segunda se abre normal')
    check(updates_in_temp() == temp_before and not (dest / 'LEEME.txt').exists(), 'ni se descarga ni se toca la instalación')
    second_window.kill(); second_window.wait(30); other.kill(); other.wait(30)

    # 1c. Si esta versión falló hace menos de 24 horas, no se reintenta.
    marker = sandbox / 'State' / 'update-failed.json'
    marker.write_text(json.dumps({'version': '9.9.9', 'utc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}), encoding='utf-8')
    retry = subprocess.Popen([str(dest / 'FFCleaner.exe'), *args])
    waited = wait_for(lambda: 'falló hace menos de 24 horas' in log_text(), 60)
    time.sleep(2)
    check(waited and retry.poll() is None and updates_in_temp() == temp_before, 'una versión que falló hace poco no se reintenta y la app se abre')
    retry.kill(); retry.wait(30)
    marker.unlink()

    # 2. Huella correcta: se cierra, se actualiza y se vuelve a abrir con el aviso.
    publish(digest)
    leftovers_before = {p.name for p in Path(tempfile.gettempdir()).glob('ffcleaner-update-*')}
    second = subprocess.Popen([str(dest / 'FFCleaner.exe'), *args])
    seen = ps(
        "Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes; $AE=[System.Windows.Automation.AutomationElement]; $S=[System.Windows.Automation.TreeScope];"
        "$end=(Get-Date).AddSeconds(90); do { $w=$AE::RootElement.FindFirst($S::Children,(New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty,'Actualizando FFCleaner')));"
        "if($w){ 'VISTA'; break }; Start-Sleep -Milliseconds 100 } while((Get-Date) -lt $end)")
    check('VISTA' in seen, 'se muestra la ventana «Actualizando FFCleaner» antes de abrir la app')
    check(wait_for(lambda: second.poll() is not None, 180), 'el proceso inicial termina sin abrir la ventana principal y deja paso al instalador')
    reopened = wait_for(lambda: app_pids(), 180)
    check(bool(reopened) and (dest / 'LEEME.txt').is_file(), 'el instalador sustituyó la instalación y volvió a abrir FFCleaner')
    title = toast_text(next(iter(reopened)))
    check('se actualiz' in title, f'la versión reabierta muestra el aviso: «{title}»')
    text = log.read_text(encoding='utf-8', errors='replace')
    check('descargada y verificada' in text and 'instalador lanzado' in text, 'el registro de diagnóstico documenta la descarga verificada')
    left = wait_for(lambda: not ({p.name for p in Path(tempfile.gettempdir()).glob('ffcleaner-update-*')} - leftovers_before), 60)
    check(left, 'la descarga temporal se retira al reabrir')
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, reg_root + r'\FFCleaner') as k:
        check(winreg.QueryValueEx(k, 'InstallLocation')[0].lower() == str(dest).lower(), 'sigue registrada en la misma carpeta (con sus elecciones)')
    check([p.name for p in dest.parent.iterdir()] == [dest.name], 'no quedan carpetas de preparación ni versiones apartadas')
    close_all()
    time.sleep(1)

    # 3. La sustitución falla a mitad (un archivo de la carpeta en uso): la versión instalada sigue intacta y funcionando,
    #    el instalador la vuelve a abrir y anota el fallo para no repetirlo en cada arranque.
    sums = json.loads((dest / 'SHA256SUMS.json').read_text(encoding='utf-8'))
    exe_hash = hashlib.sha256((dest / 'FFCleaner.exe').read_bytes()).hexdigest()
    with open(dest / 'LEEME.txt', 'rb'):
        failing = subprocess.Popen([str(dest / 'FFCleaner.exe'), *args])
        check(wait_for(lambda: failing.poll() is not None, 180), 'la app deja paso al instalador')
        reopened = wait_for(lambda: marker.exists() and app_pids(), 120)
    check(bool(reopened), 'tras el fallo, el instalador vuelve a abrir la versión instalada')
    check(json.loads(marker.read_text(encoding='utf-8'))['version'] == '9.9.9', 'y anota la versión que falló')
    check(hashlib.sha256((dest / 'FFCleaner.exe').read_bytes()).hexdigest() == exe_hash and
          all((dest / n).is_file() for n in sums) and (dest / 'install.json').is_file(), 'la versión instalada sigue completa e intacta')
    check([p.name for p in dest.parent.iterdir()] == [dest.name], 'sin restos de la sustitución fallida')
    title = toast_text(next(iter(reopened)))
    check('se actualiz' not in title, 'la versión reabierta no dice que se actualizó')
    close_all()
    time.sleep(1)
    seen = log_text().count('falló hace menos de 24 horas')
    again = subprocess.Popen([str(dest / 'FFCleaner.exe'), *args])
    check(wait_for(lambda: log_text().count('falló hace menos de 24 horas') > seen, 60) and again.poll() is None, 'al volver a abrirla no reintenta en bucle')
    again.kill(); again.wait(30)

    print('PASS actualización automática')
    print(json.dumps({'status': 'PASS', 'checks': len(checks)}, ensure_ascii=False))
finally:
    close_all()
    if server:
        server.shutdown()
    reg_delete(reg_rel)
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Software\CleanerApp_TestSandbox') as h:
            empty = winreg.QueryInfoKey(h)[0] == 0
        if empty:
            winreg.DeleteKey(winreg.HKEY_CURRENT_USER, r'Software\CleanerApp_TestSandbox')
    except FileNotFoundError:
        pass
    time.sleep(1)
    if sandbox.name.startswith('ffcleaner-sandbox-') and (sandbox / '.ffcleaner-sandbox').is_file():
        shutil.rmtree(sandbox, ignore_errors=True)
