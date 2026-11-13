"""Genera dist/FFCleaner-<versión>-Setup.exe a partir del ZIP portable, con IExpress (incluido en Windows).

El .exe extrae payload.zip y setup.cmd en una carpeta temporal; setup.cmd descomprime el paquete con tar (incluido
en Windows) y abre el asistente FFCleaner.Setup.exe con el runtime del propio paquete. Sin dependencias nuevas. Uso:
    python tools/build_installer.py        (después de tools/build_portable.py, que ya lo llama)
"""
import hashlib, json, os, re, shutil, subprocess, sys
from pathlib import Path

import project_info

base = Path(__file__).resolve().parents[1]
version = project_info.version()
archive = base / 'dist' / f'FFCleaner-{version}-win-x64.zip'
target = base / 'dist' / f'FFCleaner-{version}-Setup.exe'
work = base / 'artifacts' / 'installer-build'
iexpress = Path(os.environ['WINDIR']) / 'System32' / 'iexpress.exe'


def main() -> None:
    if not archive.is_file():
        raise SystemExit(f'Falta {archive}: ejecuta antes tools/build_portable.py')
    if not iexpress.is_file():
        raise SystemExit('No se encuentra iexpress.exe (componente de Windows).')
    shutil.rmtree(work, ignore_errors=True)
    work.mkdir(parents=True)
    shutil.copy2(archive, work / 'payload.zip')
    # Lanzador: descomprime el paquete junto a él (IExpress borra esa carpeta temporal al terminar) y abre el
    # asistente FFCleaner.Setup.exe, que usa el runtime incluido. «start /wait» hace que IExpress espere al asistente.
    lines = ['@echo off',
             r'"%SystemRoot%\System32\tar.exe" -xf "%~dp0payload.zip" -C "%~dp0."',
             'start "" /wait "%~dp0FFCleaner-' + version + r'-win-x64\FFCleaner.Setup.exe"']
    (work / 'setup.cmd').write_text('\r\n'.join(lines) + '\r\n', encoding='ascii', newline='')
    files = ['payload.zip', 'setup.cmd']
    if target.exists():
        target.unlink()

    sed = work / 'ffcleaner.sed'
    strings = '\n'.join(f'FILE{i}="{name}"' for i, name in enumerate(files))
    sources = '\n'.join(f'%FILE{i}%=' for i in range(len(files)))
    sed.write_text(f'''[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=%InstallPrompt%
DisplayLicense=%DisplayLicense%
FinishMessage=%FinishMessage%
TargetName=%TargetName%
FriendlyName=%FriendlyName%
AppLaunched=%AppLaunched%
PostInstallCmd=%PostInstallCmd%
AdminQuietInstCmd=%AdminQuietInstCmd%
UserQuietInstCmd=%UserQuietInstCmd%
SourceFiles=SourceFiles
[Strings]
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName={target}
FriendlyName=FFCleaner {version}
AppLaunched=cmd.exe /c .\\setup.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
{strings}
[SourceFiles]
SourceFiles0={work}\\
[SourceFiles0]
{sources}
''', encoding='mbcs')
    result = subprocess.run([str(iexpress), '/N', '/Q', str(sed)], cwd=work, capture_output=True, timeout=600)
    if result.returncode or not target.is_file():
        raise SystemExit('IExpress no generó el instalador (código %d).' % result.returncode)
    report = {'instalador': str(target.relative_to(base)), 'version': version, 'bytes': target.stat().st_size,
              'MiB': round(target.stat().st_size / 1024 ** 2, 2), 'sha256': hashlib.sha256(target.read_bytes()).hexdigest()}
    (base / 'artifacts' / f'installer-{version}.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
