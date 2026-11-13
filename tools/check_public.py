"""Revisión de privacidad antes de publicar: examina los archivos que Git subiría y falla si encuentra datos
personales o del equipo (nombre de usuario, ruta del perfil, correos) o archivos que no deben ir al repositorio.

Uso:  python tools/check_public.py        (ejecútalo antes de cada «git commit» / «git push»)
"""
import os, re, subprocess, sys
from pathlib import Path

base = Path(__file__).resolve().parents[1]
MAX_BYTES = 2 * 1024 * 1024
ALLOWED_EMAIL = re.compile(r'noreply@|@example\.|@users\.noreply\.github\.com', re.I)
EMAIL = re.compile(r'[\w.+-]+@[\w-]+\.[\w.-]+')


def published_files() -> list[Path]:
    out = subprocess.run(['git', 'ls-files', '--cached', '--others', '--exclude-standard'], cwd=base, capture_output=True, text=True)
    if out.returncode:
        raise SystemExit('No es un repositorio Git: ejecuta «git init» en ' + str(base))
    return [base / line for line in out.stdout.splitlines() if line.strip()]


def main() -> int:
    user = os.environ.get('USERNAME', '')
    home = str(Path.home())
    needles = [n for n in {home, home.replace('\\', '/'), home.replace('\\', '\\\\')} if n]
    if user and len(user) >= 4:
        needles.append('Users\\' + user)
        needles.append('Users/' + user)
    problems = []
    files = published_files()
    for path in files:
        rel = path.relative_to(base).as_posix()
        if rel.split('/')[0] in ('dist', 'artifacts', 'docs') or rel in ('agents.md', 'plan.md') or '/bin/' in rel or '/obj/' in rel:
            problems.append(f'{rel}: no debe publicarse (revisa .gitignore)')
            continue
        if path.stat().st_size > MAX_BYTES:
            problems.append(f'{rel}: archivo de más de 2 MiB')
            continue
        try:
            text = path.read_text(encoding='utf-8')
        except (UnicodeDecodeError, OSError):
            continue
        for needle in needles:
            if needle.lower() in text.lower():
                problems.append(f'{rel}: contiene la ruta o el usuario de este equipo ({needle})')
        for email in EMAIL.findall(text):
            if not ALLOWED_EMAIL.search(email) and not email.endswith(('.ps1', '.exe', '.dll', '.json', '.cs', '.py')):
                problems.append(f'{rel}: contiene un correo ({email})')
    if problems:
        print('NO PUBLICAR: se encontraron datos que no deben ir al repositorio público:')
        for p in sorted(set(problems)):
            print('  - ' + p)
        return 1
    print(f'OK: {len(files)} archivos revisados; sin datos personales ni archivos internos.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
