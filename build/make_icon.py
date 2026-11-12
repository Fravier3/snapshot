"""Genera el icono original de Fran Capture Studio (marco de captura con una F)."""
import sys, io, struct
from PIL import Image, ImageDraw

S = 1024
def render():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    # fondo grafito con degradado vertical
    grad = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(grad)
    top, bot = (38, 44, 56), (13, 16, 22)
    for y in range(S):
        t = y / (S - 1)
        gd.line([(0, y), (S, y)], fill=tuple(int(top[i] + (bot[i] - top[i]) * t) for i in range(3)) + (255,))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([40, 40, S - 40, S - 40], radius=220, fill=255)
    img.paste(grad, (0, 0), mask)
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([40, 40, S - 40, S - 40], radius=220, outline=(70, 80, 98, 255), width=10)
    cyan = (0, 184, 255, 255)
    # esquinas del marco de captura
    m, L, T = 190, 190, 54
    for (cx, cy, sx, sy) in [(m, m, 1, 1), (S - m, m, -1, 1), (m, S - m, 1, -1), (S - m, S - m, -1, -1)]:
        d.rounded_rectangle(sorted_box(cx, cy, cx + sx * L, cy + sy * T), radius=T // 2, fill=cyan)
        d.rounded_rectangle(sorted_box(cx, cy, cx + sx * T, cy + sy * L), radius=T // 2, fill=cyan)
    # letra F
    white = (245, 248, 252, 255)
    x0, y0, w, h, t = 392, 300, 270, 424, 92
    d.rounded_rectangle([x0, y0, x0 + t, y0 + h], radius=22, fill=white)
    d.rounded_rectangle([x0, y0, x0 + w, y0 + t], radius=22, fill=white)
    d.rounded_rectangle([x0, y0 + 166, x0 + w - 62, y0 + 166 + t], radius=22, fill=white)
    # punto de grabación
    d.ellipse([x0 + w - 40, y0 + h - 96, x0 + w + 56, y0 + h], fill=(255, 69, 58, 255))
    return img

def sorted_box(x0, y0, x1, y1):
    return [min(x0, x1), min(y0, y1), max(x0, x1), max(y0, y1)]

def main(out_dir):
    big = render()
    big.resize((512, 512), Image.LANCZOS).save(f"{out_dir}/icon-512.png")
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    blobs = []
    for s in sizes:
        b = io.BytesIO(); big.resize((s, s), Image.LANCZOS).save(b, "PNG"); blobs.append(b.getvalue())
    with open(f"{out_dir}/app.ico", "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(sizes)))
        off = 6 + 16 * len(sizes)
        for s, b in zip(sizes, blobs):
            f.write(struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(b), off)); off += len(b)
        for b in blobs: f.write(b)

if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else ".")
