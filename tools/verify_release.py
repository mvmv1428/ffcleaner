"""Ejecuta todas las suites y guarda la salida exacta de cada una como evidencia.

Las pruebas destructivas usan solo sandboxes: carpetas %TEMP%\\ffcleaner-sandbox-* y la clave
HKCU\\Software\\CleanerApp_TestSandbox. Uso:  python tools/verify_release.py [--portable]
"""
import json, os, re, subprocess, sys, uuid
from pathlib import Path

sys.stdout.reconfigure(errors='replace')   # una consola cp1252 no debe tumbar el informe de un fallo
base = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(base / 'tools'))
import project_info
sdk = project_info.dotnet()
folder = base / 'artifacts' / ('verification-' + uuid.uuid4().hex)
folder.mkdir(parents=True)
env = {**os.environ, 'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_NOLOGO': '1'}


def dotnet(project):
    return [str(sdk), 'run', '--project', str(base / project), '-c', 'Release']


cases = [
    ('build_desktop', [str(sdk), 'build', str(base / 'src/FFCleaner.Desktop/FFCleaner.Desktop.csproj'), '-c', 'Release', '-warnaserror']),
    ('build_cli', [str(sdk), 'build', str(base / 'src/FFCleaner.Cli/FFCleaner.Cli.csproj'), '-c', 'Release', '-warnaserror']),
    ('build_setup', [str(sdk), 'build', str(base / 'src/FFCleaner.Setup/FFCleaner.Setup.csproj'), '-c', 'Release', '-warnaserror']),
    ('build_mcp', [str(sdk), 'build', str(base / 'src/FFCleaner.Mcp/FFCleaner.Mcp.csproj'), '-c', 'Release', '-warnaserror']),
    ('core', dotnet('tests/CoreTests')),
    ('cleaning_sandbox', dotnet('tests/CleaningTests')),
    ('registry_sandbox', dotnet('tests/RegistryTests')),
    ('registry_safety', dotnet('tests/RegistrySafetyTests')),
    ('desktop_policy', dotnet('tests/DesktopPolicyTests')),
    ('cli_smoke', [sys.executable, str(base / 'tests/cli_smoke.py')]),
    ('ui_smoke', [sys.executable, str(base / 'tests/UiSmoke/run_smoke.py')]),
    ('mcp_smoke', [sys.executable, str(base / 'tests/mcp_smoke.py')]),
]
if '--portable' in sys.argv[1:]:
    cases.append(('portable_package', [sys.executable, str(base / 'tests/portable_smoke.py')]))
    cases.append(('installer', [sys.executable, str(base / 'tests/installer_smoke.py')]))
    cases.append(('auto_update', [sys.executable, str(base / 'tests/update_smoke.py')]))

report = {'evidence': str(folder.relative_to(base)), 'tests': []}
for name, command in cases:
    p = subprocess.run(command, cwd=base, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=900)
    text = p.stdout.decode('utf-8', errors='replace')
    (folder / (name + '.log')).write_text(text, encoding='utf-8')
    entry = {'name': name, 'exit_code': p.returncode, 'pass_lines': len(re.findall(r'^PASS\b', text, re.M)),
             'skip_lines': len(re.findall(r'^SKIP\b', text, re.M)), 'fail_lines': len(re.findall(r'^FAIL\b', text, re.M))}
    report['tests'].append(entry)
    print(json.dumps(entry))
    if p.returncode:
        print(text[-3000:])
report['passed'] = all(x['exit_code'] == 0 and x['fail_lines'] == 0 for x in report['tests'])
(folder / 'result.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print('EVIDENCE ' + str(folder / 'result.json'))
sys.exit(0 if report['passed'] else 1)
