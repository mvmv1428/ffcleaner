"""CLI de diagnóstico: solo lectura. Comprueba catálogo, análisis del Registro y ausencia de comandos destructivos."""
import json, subprocess, sys
from pathlib import Path

base = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import project_info
sdk = project_info.dotnet()
dll = base / 'src/FFCleaner.Cli/bin/Release/net10.0-windows/FFCleaner.Cli.dll'
if not dll.exists():
    print('FAIL falta el CLI compilado'); sys.exit(1)


def cli(*args):
    return subprocess.run([str(sdk), str(dll), *args], capture_output=True, text=True, encoding='utf-8')


p = cli('catalog'); assert p.returncode == 0, p.stderr
catalog = json.loads(p.stdout)
assert catalog['Version'] and isinstance(catalog['Apps'], list)
for app in catalog['Apps']:
    for category in app['Categories']:
        for target in category['Targets']:
            assert target['Key'].count('/') == 2, target
p = cli('registry', '--family', 'startup', '--family', 'apppaths'); assert p.returncode == 0, p.stderr
scan = json.loads(p.stdout)
assert scan['ScopeId'] == 'production' and len(scan['Stats']) == 2 and not scan['Cancelled']
for command in ['clean', 'delete', 'repair', 'restore']:
    p = cli(command); assert p.returncode == 1 and 'desconocido' in p.stderr, (command, p.stderr)
p = cli('analyze'); assert p.returncode == 1, 'analizar sin selección debe rechazarse'
print(f"PASS CLI de solo lectura: {len(catalog['Apps'])} aplicaciones en el catálogo, {len(scan['Findings'])} hallazgos de Registro, sin comandos destructivos")
