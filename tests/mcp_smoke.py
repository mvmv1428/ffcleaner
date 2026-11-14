"""Prueba de extremo a extremo del servidor MCP por stdio, como lo usaría un agente.

Todo ocurre en un sandbox: archivos bajo %TEMP%\\ffcleaner-sandbox-* y Registro bajo
HKCU\\Software\\CleanerApp_TestSandbox\\<id>. Con FFCLEANER_MCP_EXE se prueba el ejecutable del paquete portable.
"""
import hashlib, json, os, shutil, subprocess, sys, tempfile, uuid, winreg
from pathlib import Path

base = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import project_info
sdk = project_info.dotnet()
dll = base / 'src/FFCleaner.Mcp/bin/Release/net10.0-windows/FFCleaner.Mcp.dll'
exe = os.environ.get('FFCLEANER_MCP_EXE')
launch = [exe] if exe else [str(sdk), 'exec', str(dll)]
if not Path(exe or dll).is_file():
    print('FAIL falta el servidor MCP compilado'); sys.exit(1)

run_id = uuid.uuid4().hex
sandbox = Path(tempfile.gettempdir()) / ('ffcleaner-sandbox-' + run_id)
registry_root = r'Software\CleanerApp_TestSandbox' + '\\' + run_id
cv = r'Software\Microsoft\Windows\CurrentVersion'
checks = []


def check(condition, message):
    if not condition:
        raise AssertionError(message)
    checks.append(message)
    print('OK ' + message)


def write(path: Path, size: int = 512) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(hashlib.sha256(str(path).encode()).digest() * (size // 32 + 1))
    os.utime(path, (1_700_000_000, 1_700_000_000))


def reg_set(hive, key, name, value):
    with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, registry_root + '\\Registry64\\' + hive + '\\' + key, 0, winreg.KEY_WRITE) as handle:
        winreg.SetValueEx(handle, name, 0, winreg.REG_SZ, value)


def reg_get(hive, key, name):
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, registry_root + '\\Registry64\\' + hive + '\\' + key) as handle:
            return winreg.QueryValueEx(handle, name)[0]
    except FileNotFoundError:
        return None


def reg_delete(path):
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path, 0, winreg.KEY_ALL_ACCESS) as handle:
            for name in [winreg.EnumKey(handle, i) for i in range(winreg.QueryInfoKey(handle)[0])]:
                reg_delete(path + '\\' + name)
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, path)
    except FileNotFoundError:
        pass


class Client:
    def __init__(self, process):
        self.process, self.next_id = process, 0

    def request(self, method, params=None):
        self.next_id += 1
        message = {'jsonrpc': '2.0', 'id': self.next_id, 'method': method}
        if params is not None:
            message['params'] = params
        self.process.stdin.write((json.dumps(message) + '\n').encode('utf-8'))
        self.process.stdin.flush()
        line = self.process.stdout.readline()
        if not line:
            raise AssertionError('el servidor cerró la salida: ' + self.process.stderr.read().decode('utf-8', 'replace'))
        response = json.loads(line)
        assert response.get('id') == self.next_id, response
        return response

    def notify(self, method):
        self.process.stdin.write((json.dumps({'jsonrpc': '2.0', 'method': method}) + '\n').encode('utf-8'))
        self.process.stdin.flush()

    def call(self, name, arguments=None):
        response = self.request('tools/call', {'name': name, 'arguments': arguments or {}})
        assert 'result' in response, response
        result = response['result']
        return result['structuredContent'], result['isError']


process = None
try:
    # ── Fixture: perfiles de navegador, caché de Claude y Registro de prueba ──
    for folder in ['Local', 'Roaming', 'Temp', 'State', 'Home', 'LocalLow', 'ProgramData', 'Windows']:
        (sandbox / folder).mkdir(parents=True)
    (sandbox / '.ffcleaner-sandbox').write_text('Carpeta de pruebas de FFCleaner. Se puede borrar.', encoding='utf-8')
    chrome = sandbox / 'Local/Google/Chrome/User Data/Default'
    for personal in ['Preferences', 'Cookies', 'History', 'Login Data', 'Web Data', 'Bookmarks', 'Network/Cookies']:
        write(chrome / personal)
    for i in range(20):
        write(chrome / 'Cache' / 'Cache_Data' / f'f_{i:06x}', 4096)
    write(sandbox / 'Roaming/Claude/Cache/data', 20000)
    write(sandbox / 'Roaming/Claude/Code Cache/protegido', 20000)
    # Ajustes del usuario: una ruta protegida (exclusión) y, de momento, ningún permiso para los agentes.
    settings_path = sandbox / 'State' / 'settings.json'

    def settings(**permissions):
        settings_path.write_text(json.dumps({'SelectedTargets': [], 'RegistryFamilies': [],
                                             'Exclusions': [str(sandbox / 'Roaming' / 'Claude' / 'Code Cache')], **permissions}), encoding='utf-8')
    settings()
    personal_before = {p: (chrome / p).read_bytes() for p in ['Cookies', 'History', 'Login Data', 'Web Data', 'Bookmarks']}
    missing = str(sandbox / 'desinstalado')
    reg_set('HKCU', cv + r'\Run', 'Huerfano', f'"{missing}\\inicio.exe"')
    reg_set('HKCU', cv + r'\Run', 'Valido', f'"{sys.executable}"')

    process = subprocess.Popen([*launch, '--sandbox', str(sandbox), '--sandbox-registry', run_id],
                               stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, cwd=str(sandbox))
    client = Client(process)

    # ── Protocolo ──
    init = client.request('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {}, 'clientInfo': {'name': 'mcp_smoke', 'version': '1'}})
    check(init['result']['protocolVersion'] == '2025-06-18' and init['result']['serverInfo']['name'] == 'ffcleaner', 'initialize negocia el protocolo')
    client.notify('notifications/initialized')
    tools = {t['name']: t for t in client.request('tools/list')['result']['tools']}
    check(set(tools) == {'list_cleanup_targets', 'analyze_cleanup', 'clean', 'scan_registry', 'repair_registry', 'list_registry_backups',
                         'restore_registry_backup', 'list_startup_programs', 'set_startup_program'}, 'nueve herramientas publicadas')
    check(tools['clean']['annotations']['destructiveHint'] and tools['analyze_cleanup']['annotations']['readOnlyHint'], 'anotaciones de solo lectura y destructivas')
    check('error' in client.request('no/existe'), 'método desconocido devuelve error JSON-RPC')

    # ── Limpieza ──
    catalog, error = client.call('list_cleanup_targets')
    keys = [c['key'] for a in catalog['apps'] for c in a['categories']]
    check(not error and 'chrome/cache' in keys and 'chrome/history' in keys, 'el catálogo incluye caché e historial de Chrome' + ('' if 'chrome/cache' in keys else ': ' + str(keys)[:400]))
    _, error = client.call('analyze_cleanup')
    check(error, 'analizar sin selección se rechaza (nada implícito)')
    refused, error = client.call('analyze_cleanup', {'targets': ['chrome/history']})
    check(error and 'allow_personal_data' in refused['error'], 'el historial exige permiso expreso')
    check(catalog['agent_permissions'] == {'personal_data_and_models': False, 'registry_changes': False,
                                           'how_to_change': catalog['agent_permissions']['how_to_change']}, 'los agentes empiezan sin permisos especiales')
    refused, error = client.call('analyze_cleanup', {'targets': ['chrome/history'], 'allow_personal_data': True})
    check(error and 'Configuración' in refused['error'], 'allow_personal_data no basta: el usuario debe permitirlo en Configuración')
    analysis, error = client.call('analyze_cleanup', {'preset': 'safe'})
    target_keys = [t['key'] for t in analysis['targets']]
    check(not error and analysis['freeable_bytes'] > 0 and analysis['nothing_deleted_yet'], f"preset safe analiza sin borrar: {analysis['freeable']}")
    check(not any('/history/' in k or '/cookies/' in k for k in target_keys), 'el preset safe no incluye datos personales')
    check(len(list((chrome / 'Cache/Cache_Data').iterdir())) == 20, 'analizar no borra nada')
    _, error = client.call('clean', {'analysis_id': analysis['analysis_id']})
    check(error, 'limpiar sin confirm=true se rechaza')
    _, error = client.call('clean', {'analysis_id': 'inventado', 'confirm': True})
    check(error, 'limpiar con un analysis_id inventado se rechaza')
    cleaned, error = client.call('clean', {'analysis_id': analysis['analysis_id'], 'confirm': True})
    check(not error and cleaned['freed_bytes'] > 0 and cleaned['deleted'] >= 21, f"limpieza real: {cleaned['freed']} en {cleaned['deleted']} archivos")
    check(not (chrome / 'Cache/Cache_Data').exists() or not any((chrome / 'Cache/Cache_Data').iterdir()), 'la caché de Chrome desapareció')
    check(all((chrome / p).read_bytes() == data for p, data in personal_before.items()), 'cookies, historial, contraseñas y marcadores intactos')
    check((sandbox / 'Roaming/Claude/Code Cache/protegido').is_file() and not (sandbox / 'Roaming/Claude/Cache/data').exists(),
          'el servidor respeta las rutas protegidas del usuario (y limpia lo demás)')
    _, error = client.call('clean', {'analysis_id': analysis['analysis_id'], 'confirm': True})
    check(error, 'un análisis solo sirve para una limpieza')

    # ── Registro ──
    scan, error = client.call('scan_registry', {'families': ['startup']})
    orphan = [f for f in scan['findings'] if f['value'] == 'Huerfano']
    check(not error and orphan and orphan[0]['can_repair'] and scan['nothing_changed'], 'el análisis del Registro detecta el huérfano sin cambiar nada')
    refused, error = client.call('repair_registry', {'scan_id': scan['scan_id'], 'all_safe': True, 'confirm': True})
    check(error and 'Configuración' in refused['error'] and reg_get('HKCU', cv + r'\Run', 'Huerfano') is not None,
          'sin el permiso del usuario, reparar no cambia nada aunque el agente confirme')
    settings(AgentsMayChangeRegistry=True)
    _, error = client.call('repair_registry', {'scan_id': scan['scan_id'], 'all_safe': True})
    check(error and reg_get('HKCU', cv + r'\Run', 'Huerfano') is not None, 'reparar sin confirm=true no cambia nada')
    repaired, error = client.call('repair_registry', {'scan_id': scan['scan_id'], 'all_safe': True, 'confirm': True})
    check(not error and repaired['repaired'] >= 1 and repaired['backup_id'], 'reparación con respaldo verificado')
    check(reg_get('HKCU', cv + r'\Run', 'Huerfano') is None and reg_get('HKCU', cv + r'\Run', 'Valido') is not None, 'se borra el huérfano y el válido sigue')
    backups, _ = client.call('list_registry_backups')
    check(any(b['backup_id'] == repaired['backup_id'] and b['valid'] for b in backups['backups']), 'el respaldo aparece y es válido')
    restored, error = client.call('restore_registry_backup', {'backup_id': repaired['backup_id'], 'confirm': True})
    check(not error and restored['restored'] >= 1 and reg_get('HKCU', cv + r'\Run', 'Huerfano') is not None, 'restaurar devuelve la entrada')

    # ── Inicio de Windows ──
    startup, error = client.call('list_startup_programs')
    valid = [p for p in startup['programs'] if p['name'] == 'Valido']
    check(not error and valid and valid[0]['enabled'], 'lista los programas de inicio')
    approved = cv + r'\Explorer\StartupApproved\Run'
    _, error = client.call('set_startup_program', {'id': valid[0]['id'], 'enabled': False})
    check(not error and reg_get('HKCU', approved, 'Valido')[0] == 3, 'desactivar escribe 03 en StartupApproved')
    _, error = client.call('set_startup_program', {'id': valid[0]['id'], 'enabled': True})
    check(not error and reg_get('HKCU', approved, 'Valido')[0] == 2 and reg_get('HKCU', cv + r'\Run', 'Valido'), 'reactivar escribe 02 y la entrada sigue')

    process.stdin.close()
    check(process.wait(timeout=20) == 0, 'el servidor termina limpio al cerrar la entrada')
    print('PASS servidor MCP por stdio')
    print(json.dumps({'status': 'PASS', 'modo': 'portable' if exe else 'desarrollo', 'checks': len(checks)}, ensure_ascii=False))
finally:
    if process is not None and process.poll() is None:
        process.kill()
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
