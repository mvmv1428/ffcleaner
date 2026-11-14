"""Construye el paquete portable de FFCleaner: publica, añade el runtime de .NET junto al ejecutable y crea el ZIP.

No instala nada en el sistema ni depende de un .NET global: el ejecutable busca su runtime en la carpeta
«runtime» que viaja con él. Todas las rutas se derivan de la ubicación de este script.

Uso:  python tools/build_portable.py [--replace]
"""
import hashlib, json, os, re, shutil, subprocess, sys, zipfile
from pathlib import Path

import project_info

base = Path(__file__).resolve().parents[1]
sdk = project_info.dotnet()
sdk_root = sdk.parent
project = base / 'src' / 'FFCleaner.Desktop' / 'FFCleaner.Desktop.csproj'
version = project_info.version()
name = f'FFCleaner-{version}-win-x64'
package = base / 'dist' / name
archive = base / 'dist' / (name + '.zip')

LEEME = f"""FFCLEANER {version} — LIMPIEZA, REGISTRO, INICIO DE WINDOWS Y AGENTES · WINDOWS 10/11 x64

INSTALAR O USAR SIN INSTALAR
- Con instalador: ejecuta FFCleaner-{version}-Setup.exe. Instala solo para tu usuario (sin administrador) en
  %LOCALAPPDATA%\\Programs\\FFCleaner, crea el acceso del menú Inicio (y, si quieres, del escritorio) y aparece en
  «Aplicaciones instaladas» de Windows, desde donde se desinstala. Al desinstalar, tus ajustes y los respaldos del
  Registro se conservan salvo que marques borrarlos. Volver a ejecutarlo actualiza la versión instalada. Todo
  ocurre en un asistente por pasos; el desinstalador es FFCleaner.Setup.exe.
- Sin instalar (portátil): extrae la carpeta completa donde quieras y haz doble clic en FFCleaner.exe. Conserva
  junto a él la carpeta «runtime» y las demás bibliotecas.
En ambos casos no necesita .NET instalado, no se ejecuta al iniciar Windows y no deja servicios residentes.

LIMPIEZA PERSONALIZADA
1. Elige una aplicación en la lista y marca sus categorías y perfiles. Cada navegador y perfil es independiente.
2. Pulsa Analizar. Solo se leen nombres y tamaños; no se borra nada.
3. La tarjeta superior muestra cuánto puedes liberar y en qué aplicaciones; la tabla, cada objetivo.
4. Pulsa Limpiar seleccionados y confirma. Si cambias cualquier casilla, hay que volver a analizar.
Por defecto («Marcar todo» incluido) solo se marcan cachés y temporales. Historial, descargas, cookies,
sesiones y formularios de cada navegador, y los modelos de IA, se marcan uno a uno a propósito. Esos datos se
borran solo con el navegador cerrado, dentro de sus bases (sin borrar el archivo) y tras una copia temporal
que se borra al terminar (solo se conserva si no se pudo comprobar que la base quedó bien). Las contraseñas, marcadores, tarjetas y direcciones guardadas nunca se tocan, ni nada de un
navegador o perfil sin marcar. Las categorías «Administrador» de Windows requieren reiniciar como administrador.
La limpieza es permanente (no pasa por la Papelera). Cada lote deja una bitácora.

REGISTRO
1. Elige familias y pulsa Analizar Registro (solo lectura).
2. Cada hallazgo indica su riesgo. Solo los «Seguro» se pueden marcar; los ambiguos, críticos del sistema o
   sin permisos se muestran pero no se modifican.
3. Reparar seleccionados guarda y verifica antes un respaldo (respaldo.json + registro.reg). Si el respaldo
   falla, no se cambia nada.
4. Restaurar respaldo devuelve las entradas eliminadas. No sobrescribe datos que hayan cambiado después.
Las entradas de HKLM requieren abrir FFCleaner como administrador (botón «Reiniciar como administrador»).
Limpiar el Registro no acelera el equipo; sirve para retirar referencias a programas que ya no existen.

INICIO DE WINDOWS
Lista los programas que arrancan al iniciar sesión, con su nombre e icono reales. El interruptor los activa o
desactiva con el mismo mecanismo que el Administrador de tareas: no borra la entrada ni el acceso directo y se
puede reactivar en cualquier momento. Los de todos los usuarios requieren reiniciar como administrador.

CONFIGURACIÓN
Tema (Sistema, Claro u Oscuro) e idioma (Español o English). Se aplican al momento y se guardan.

AGENTES DE IA (MCP)
FFCleaner.Mcp.exe es un servidor MCP para agentes de programación (Claude Code, Codex, Cursor…). Para Claude Code:
  claude mcp add ffcleaner -- "<carpeta>\\FFCleaner.Mcp.exe"
Configuración > Agentes de IA copia el comando o el JSON con la ruta correcta. El agente siempre analiza antes de
limpiar (analysis_id de un solo uso, 15 minutos) y debe confirmar con confirm=true. Borrar historial, cookies o
modelos de IA y cambiar el Registro solo es posible si lo activas en Configuración > Agentes de IA (desactivado por
defecto); aun así, el Registro solo repara entradas «Seguro» con respaldo verificado.

DÓNDE GUARDA SUS DATOS
%LOCALAPPDATA%\\FFCleaner\\settings.json          selección guardada
%LOCALAPPDATA%\\FFCleaner\\Journal\\               bitácoras de limpieza
%LOCALAPPDATA%\\FFCleaner\\RegistryBackups\\       respaldos del Registro
%LOCALAPPDATA%\\FFCleaner\\BrowserBackups\\        copias temporales de las bases del navegador
%LOCALAPPDATA%\\FFCleaner\\Logs\\ffcleaner.log     diagnóstico y tiempos por fase
Para desinstalar: borra esta carpeta y, si quieres, %LOCALAPPDATA%\\FFCleaner.
Si usabas la versión anterior (Claro), en el primer arranque se copian sus ajustes, bitácoras y respaldos
del Registro desde %LOCALAPPDATA%\\Claro; esa carpeta no se modifica y se puede borrar después.

PAQUETE
Runtime de Microsoft .NET incluido en «runtime» (licencias en runtime\\LICENSE.txt y ThirdPartyNotices.txt).
Compilación propia sin firma de editor: Windows puede mostrar un aviso de SmartScreen la primera vez.
SHA256SUMS.json lista la huella de cada archivo del paquete.
"""

repository: list[str] = []


def run(*command: str) -> None:
    env = {**os.environ, 'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_NOLOGO': '1'}
    result = subprocess.run(command, cwd=base, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if result.returncode:
        print(result.stdout.decode('utf-8', errors='replace'))
        raise SystemExit('Falló: ' + ' '.join(command))


def main() -> None:
    replace = '--replace' in sys.argv[1:]
    global repository
    # En GitHub Actions el flujo de publicación pasa el repositorio: así las versiones publicadas saben dónde buscar actualizaciones.
    repo = os.environ.get('FFCLEANER_UPDATE_REPOSITORY', '').strip()
    repository = [f'-p:FFCleanerUpdateRepository={repo}'] if repo else []
    # Si la versión anterior está abierta, Windows no deja borrar sus archivos y el paquete quedaría a medias.
    running = subprocess.run(['powershell.exe', '-NoProfile', '-Command',
                              "Get-Process FFCleaner, FFCleaner.Mcp -ErrorAction SilentlyContinue | ForEach-Object { $_.Path }"],
                             stdout=subprocess.PIPE, stderr=subprocess.DEVNULL).stdout.decode('utf-8', 'replace')
    if str(package).lower() in running.lower():
        raise SystemExit('FFCleaner está abierto desde ' + str(package) + ': ciérralo y vuelve a ejecutar este script.')
    for target in (package, archive):
        if target.exists():
            if not replace:
                raise SystemExit(f'Ya existe {target}. Usa --replace para regenerar esta versión.')
            shutil.rmtree(target) if target.is_dir() else target.unlink()

    # 1. Publicación: el ejecutable buscará .NET únicamente en la carpeta «runtime» contigua.
    run(str(sdk), 'publish', str(project), '-c', 'Release', '-o', str(package), '--self-contained', 'false',
        '-p:AppHostDotNetSearch=AppRelative', '-p:AppHostRelativeDotNet=runtime', *repository)
    # El servidor MCP para agentes se publica en la misma carpeta y comparte módulos y runtime.
    run(str(sdk), 'publish', str(base / 'src/FFCleaner.Mcp/FFCleaner.Mcp.csproj'), '-c', 'Release', '-o', str(package), '--self-contained', 'false',
        '-p:AppHostDotNetSearch=AppRelative', '-p:AppHostRelativeDotNet=runtime', *repository)
    # Instalador/desinstalador (asistente): viaja dentro del paquete y usa el mismo runtime.
    run(str(sdk), 'publish', str(base / 'src/FFCleaner.Setup/FFCleaner.Setup.csproj'), '-c', 'Release', '-o', str(package), '--self-contained', 'false',
        '-p:AppHostDotNetSearch=AppRelative', '-p:AppHostRelativeDotNet=runtime', *repository)
    for required in ('FFCleaner.exe', 'FFCleaner.dll', 'FFCleaner.Core.dll', 'FFCleaner.Cleaning.dll', 'FFCleaner.RegistryAnalysis.dll', 'FFCleaner.Mcp.exe', 'FFCleaner.Mcp.dll', 'FFCleaner.Setup.exe', 'FFCleaner.Setup.dll'):
        if not (package / required).is_file():
            raise SystemExit('Falta en la publicación: ' + required)

    # 2. Runtime local: se copia el ya verificado del SDK portátil del proyecto, sin descargar nada.
    runtime = package / 'runtime'
    # La versión más alta de .NET 10 que esté completa (host, runtime base y de escritorio) en el SDK usado.
    def found(folder):
        return {p.name for p in folder.iterdir() if p.is_dir()} if folder.is_dir() else set()
    common = found(sdk_root / 'host' / 'fxr') & found(sdk_root / 'shared' / 'Microsoft.NETCore.App') & found(sdk_root / 'shared' / 'Microsoft.WindowsDesktop.App')
    candidates = sorted((v for v in common if v.startswith('10.') and '-' not in v), key=lambda v: tuple(int(x) for x in v.split('.')))
    if not candidates:
        raise SystemExit('El SDK no incluye un runtime de .NET 10 completo (base y escritorio) para empaquetar.')
    runtime_version = candidates[-1]
    shutil.copytree(sdk_root / 'host' / 'fxr' / runtime_version, runtime / 'host' / 'fxr' / runtime_version)
    for framework in ('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App'):
        shutil.copytree(sdk_root / 'shared' / framework / runtime_version, runtime / 'shared' / framework / runtime_version)
    for notice in ('LICENSE.txt', 'ThirdPartyNotices.txt'):
        shutil.copy2(sdk_root / notice, runtime / notice)

    (package / 'LEEME.txt').write_text(LEEME, encoding='utf-8-sig')
    info = {'producto': 'FFCleaner', 'version': version, 'runtime': runtime_version, 'busqueda_runtime': 'AppRelative:runtime',
            'frameworks': ['Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App'], 'instalacion_global': False,
            'modulos': ['FFCleaner.Core', 'FFCleaner.Cleaning', 'FFCleaner.RegistryAnalysis'], 'servidor_mcp': 'FFCleaner.Mcp.exe'}
    (package / 'bundle-info.json').write_text(json.dumps(info, indent=2, ensure_ascii=False), encoding='utf-8')

    # 3. Huellas y ZIP.
    files = sorted(p for p in package.rglob('*') if p.is_file())
    sums = {p.relative_to(package).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
    (package / 'SHA256SUMS.json').write_text(json.dumps(sums, indent=2), encoding='utf-8')
    with zipfile.ZipFile(archive, 'x', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for p in sorted(p for p in package.rglob('*') if p.is_file()):
            z.write(p, name + '/' + p.relative_to(package).as_posix())
    with zipfile.ZipFile(archive) as z:
        if z.testzip() is not None:
            raise SystemExit('El ZIP no supera la comprobación CRC')

    report = {'paquete': str(package.relative_to(base)), 'zip': str(archive.relative_to(base)), 'version': version, 'runtime': runtime_version,
              'archivos': len(sums) + 1, 'bytes_zip': archive.stat().st_size, 'MiB_zip': round(archive.stat().st_size / 1024 ** 2, 2),
              'sha256_zip': hashlib.sha256(archive.read_bytes()).hexdigest()}
    (base / 'artifacts').mkdir(exist_ok=True)
    (base / 'artifacts' / f'release-{version}.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report, indent=2))
    # 4. Instalador para Windows a partir del mismo ZIP (IExpress + PowerShell, ambos incluidos en Windows).
    result = subprocess.run([sys.executable, str(base / 'tools' / 'build_installer.py')], cwd=base)
    if result.returncode:
        raise SystemExit('Falló la generación del instalador.')


if __name__ == '__main__':
    main()
