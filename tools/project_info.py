"""Datos comunes para los scripts: versión del proyecto y SDK de .NET a usar.

El SDK portátil del proyecto (../.tools/dotnet10) se usa si existe; si no, el «dotnet» del sistema (por ejemplo,
en GitHub Actions o en otro equipo con el SDK de .NET 10 instalado).
"""
import re, shutil
from pathlib import Path

base = Path(__file__).resolve().parents[1]


def version() -> str:
    return re.search(r'<Version>([^<]+)</Version>', (base / 'Directory.Build.props').read_text(encoding='utf-8')).group(1)


def dotnet() -> Path:
    local = base.parent / '.tools' / 'dotnet10' / 'dotnet.exe'
    if local.is_file():
        return local
    found = shutil.which('dotnet')
    if not found:
        raise SystemExit('No se encuentra el SDK de .NET 10: instálalo (https://dotnet.microsoft.com) o colócalo en ../.tools/dotnet10.')
    return Path(found).resolve()
