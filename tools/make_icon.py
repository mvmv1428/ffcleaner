"""Genera el icono de FFCleaner: src/FFCleaner.Desktop/Assets/ffcleaner.ico (16–256 px) y ffcleaner.png.

Diseño: cuadrado de esquinas suaves con el degradado de la marca (índigo → cian) y una ligadura «FF»
blanca cuyo segundo travesaño se prolonga como un trazo de barrido; un destello marca «limpio».
Se dibuja en vectores a 1024 px y se reduce a cada tamaño, sin el destello en los más pequeños.
Uso:  python tools/make_icon.py
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

BASE = Path(__file__).resolve().parents[1]
OUT = BASE / 'src' / 'FFCleaner.Desktop' / 'Assets'
S = 1024
INDIGO, CYAN, VIOLET = (99, 102, 241), (6, 182, 212), (79, 70, 229)


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def gradient(size):
    img = Image.new('RGB', (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            t = (x * 0.55 + y * 0.45) / size
            px[x, y] = lerp(VIOLET, CYAN, max(0.0, min(1.0, (t - 0.05) / 0.95))) if t > 0.05 else VIOLET
    return img


def draw(size_hint):
    k = S / 1024
    tile = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    mask = Image.new('L', (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle((56, 56, S - 56, S - 56), radius=236, fill=255)
    tile.paste(gradient(S).convert('RGBA'), (0, 0), mask)

    # Brillo superior muy sutil para dar volumen.
    gloss = Image.new('L', (S, S), 0)
    ImageDraw.Draw(gloss).ellipse((-200, -620, S + 200, 430), fill=46)
    gloss = Image.composite(gloss, Image.new('L', (S, S), 0), mask).filter(ImageFilter.GaussianBlur(30))
    tile = Image.alpha_composite(tile, Image.merge('RGBA', (Image.new('L', (S, S), 255),) * 3 + (gloss,)))

    glyph = Image.new('L', (S, S), 0)
    g = ImageDraw.Draw(glyph)
    r = 26  # esquinas suaves de los trazos
    stem = 118
    top, mid_y, bottom = 262, 470, 770
    x1, x2 = 236, 488
    # Primera F
    g.rounded_rectangle((x1, top, x1 + stem, bottom), radius=r, fill=255)
    g.rounded_rectangle((x1, top, x2 + stem, top + 112), radius=r, fill=255)       # travesaño superior común (ligadura)
    g.rounded_rectangle((x1, mid_y, x1 + 230, mid_y + 100), radius=r, fill=255)
    # Segunda F
    g.rounded_rectangle((x2, top, x2 + stem, bottom), radius=r, fill=255)
    g.rounded_rectangle((x2, top, 806, top + 112), radius=r, fill=255)
    # Travesaño medio prolongado en trazos de barrido decrecientes.
    g.rounded_rectangle((x2, mid_y, 742, mid_y + 100), radius=r, fill=255)
    if size_hint >= 32:
        g.rounded_rectangle((770, mid_y + 6, 834, mid_y + 50), radius=22, fill=225)
        g.rounded_rectangle((770, mid_y + 62, 806, mid_y + 96), radius=17, fill=190)

    shadow = glyph.filter(ImageFilter.GaussianBlur(18))
    tile = Image.alpha_composite(tile, Image.merge('RGBA', (Image.new('L', (S, S), 30),) * 3 + (shadow.point(lambda v: v * 0.35),)))
    tile = Image.alpha_composite(tile, Image.merge('RGBA', (Image.new('L', (S, S), 255),) * 3 + (glyph,)))

    if size_hint >= 40:
        # Destello de cuatro puntas, abajo a la derecha.
        cx, cy, R, w = 772, 724, 118, 30
        star = Image.new('L', (S, S), 0)
        ImageDraw.Draw(star).polygon([(cx, cy - R), (cx + w, cy - w), (cx + R, cy), (cx + w, cy + w), (cx, cy + R), (cx - w, cy + w), (cx - R, cy), (cx - w, cy - w)], fill=255)
        glow = star.filter(ImageFilter.GaussianBlur(26)).point(lambda v: v * 0.6)
        tile = Image.alpha_composite(tile, Image.merge('RGBA', (Image.new('L', (S, S), 255),) * 3 + (glow,)))
        tile = Image.alpha_composite(tile, Image.merge('RGBA', (Image.new('L', (S, S), 255),) * 3 + (star,)))
    return tile


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    frames = [draw(s).resize((s, s), Image.LANCZOS) for s in sizes]
    frames[-1].save(OUT / 'ffcleaner.ico', format='ICO', sizes=[(s, s) for s in sizes], append_images=frames[:-1])
    draw(256).resize((256, 256), Image.LANCZOS).save(OUT / 'ffcleaner.png')
    preview = Image.new('RGBA', (sum(sizes) + 20 * len(sizes) + 20, 300), (244, 246, 251, 255))
    x = 20
    for s, f in zip(sizes, frames):
        preview.paste(f, (x, 150 - s // 2), f)
        x += s + 20
    preview.save(BASE / 'artifacts' / 'icon-preview.png')
    print('icono:', OUT / 'ffcleaner.ico')


if __name__ == '__main__':
    main()
