"""Verifica el paquete portable tal y como lo recibiría otro PC.

Extrae el ZIP en una carpeta aislada (con espacios y caracteres no ASCII), comprueba la huella de cada
archivo y repite allí el flujo completo de UI Automation sobre un sandbox, con las variables de .NET global
anuladas para demostrar que el ejecutable usa únicamente el runtime que lleva incluido.
"""
import hashlib, json, os, re, shutil, subprocess, sys, tempfile, uuid, zipfile
from pathlib import Path

base = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(base / 'tools'))
import project_info
version = project_info.version()
name = f'FFCleaner-{version}-win-x64'
archive = base / 'dist' / (name + '.zip')
if not archive.is_file():
    print('FAIL el paquete portable todavía no existe: ' + str(archive)); sys.exit(1)

work = Path(tempfile.gettempdir()) / ('ffcleaner-portable-' + uuid.uuid4().hex) / 'Portátil FFCleaner ñ á'
try:
    with zipfile.ZipFile(archive) as z:
        assert z.testzip() is None, 'CRC del ZIP'
        for item in z.infolist():
            path = Path(item.filename)
            assert not path.is_absolute() and '..' not in path.parts, 'entrada insegura en el ZIP: ' + item.filename
        work.mkdir(parents=True)
        z.extractall(work)
    extracted = work / name
    sums = json.loads((extracted / 'SHA256SUMS.json').read_text(encoding='utf-8'))
    for relative, digest in sums.items():
        assert hashlib.sha256((extracted / relative).read_bytes()).hexdigest() == digest, relative
    present = {p.relative_to(extracted).as_posix() for p in extracted.rglob('*') if p.is_file()} - {'SHA256SUMS.json'}
    assert present == set(sums), sorted(present ^ set(sums))[:5]

    # Dependencias incluidas: aplicación, módulos propios y runtime completo junto al ejecutable.
    for required in ['FFCleaner.exe', 'FFCleaner.dll', 'FFCleaner.Core.dll', 'FFCleaner.Cleaning.dll', 'FFCleaner.RegistryAnalysis.dll', 'LEEME.txt', 'FFCleaner.runtimeconfig.json',
                     'FFCleaner.Mcp.exe', 'FFCleaner.Mcp.dll', 'FFCleaner.Mcp.runtimeconfig.json']:
        assert (extracted / required).is_file(), required
    info = json.loads((extracted / 'bundle-info.json').read_text(encoding='utf-8'))
    runtime = extracted / 'runtime'
    assert (runtime / 'host/fxr' / info['runtime'] / 'hostfxr.dll').is_file()
    assert (runtime / 'shared/Microsoft.NETCore.App' / info['runtime'] / 'coreclr.dll').is_file()
    assert (runtime / 'shared/Microsoft.WindowsDesktop.App' / info['runtime'] / 'PresentationFramework.dll').is_file()
    deps = json.loads((extracted / 'FFCleaner.deps.json').read_text(encoding='utf-8-sig'))
    assert any('FFCleaner.Cleaning' in key for key in deps['libraries']) and any('FFCleaner.RegistryAnalysis' in key for key in deps['libraries'])

    # Ninguna ruta de la máquina de compilación dentro de los binarios propios.
    user = os.environ.get('USERNAME', '')
    for binary in ['FFCleaner.dll', 'FFCleaner.Core.dll', 'FFCleaner.Cleaning.dll', 'FFCleaner.RegistryAnalysis.dll', 'FFCleaner.exe', 'FFCleaner.Mcp.dll', 'FFCleaner.Mcp.exe']:
        data = (extracted / binary).read_bytes()
        for needle in [str(base), str(Path.home())] + ([f'Users\\{user}'] if user else []):
            assert needle.encode('utf-8') not in data and needle.encode('utf-16-le') not in data, f'{binary} contiene {needle}'

    print(f'PASS paquete extraído en {extracted}: {len(sums)} archivos con huella correcta, runtime {info["runtime"]} incluido')
    result = subprocess.run([sys.executable, str(base / 'tests/UiSmoke/run_smoke.py')], env={**os.environ, 'CLARO_PORTABLE_EXE': str(extracted / 'FFCleaner.exe')})
    if result.returncode:
        print('FAIL flujo de interfaz sobre el paquete extraído'); sys.exit(1)
    print('PASS el ejecutable portable inicia en una ubicación nueva con su propio runtime y completa ambos flujos')
    # El servidor MCP del paquete arranca con el runtime incluido (sin .NET global) y completa el flujo de agente.
    nowhere = str(work / 'sin-runtime-global')
    result = subprocess.run([sys.executable, str(base / 'tests/mcp_smoke.py')], env={**os.environ, 'FFCLEANER_MCP_EXE': str(extracted / 'FFCleaner.Mcp.exe'),
                            'DOTNET_ROOT': nowhere, 'DOTNET_ROOT_X64': nowhere, 'DOTNET_MULTILEVEL_LOOKUP': '0'})
    if result.returncode:
        print('FAIL servidor MCP del paquete'); sys.exit(1)
    print('PASS el servidor MCP portable funciona con su propio runtime')
finally:
    shutil.rmtree(work.parent, ignore_errors=True)
