"""Genera app.ico e icon-512.png a partir del logo suministrado (build/logo/logo-original.webp).
Recorta el cuadrado redondeado azul y deja transparente el fondo."""
import sys, io, struct
from PIL import Image, ImageDraw, ImageFilter

BOX = (110, 112, 1138, 1140)   # cuadrado del icono dentro de la imagen original (1254 px)
RADIUS = 232

def render(src):
    im = Image.open(src).convert("RGBA").crop(BOX)
    s = im.size[0]
    scale = 4
    mask = Image.new("L", (s * scale, s * scale), 0)
    ImageDraw.Draw(mask).rounded_rectangle([9 * scale, 9 * scale, (s - 9) * scale, (s - 31) * scale], radius=(RADIUS - 4) * scale, fill=255)
    mask = mask.resize((s, s), Image.LANCZOS)
    im.putalpha(mask)
    # recentrar verticalmente (el original deja sitio a la sombra inferior)
    out = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    out.paste(im, (0, 11), im)
    return out

def main(out_dir, src="build/logo/logo-original.webp"):
    big = render(src)
    big.resize((512, 512), Image.LANCZOS).save(f"{out_dir}/icon-512.png")
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    blobs = []
    for s in sizes:
        img = big.resize((s, s), Image.LANCZOS)
        if s <= 48: img = img.filter(ImageFilter.UnsharpMask(radius=0.6, percent=120, threshold=0))
        b = io.BytesIO(); img.save(b, "PNG"); blobs.append(b.getvalue())
    with open(f"{out_dir}/app.ico", "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(sizes)))
        off = 6 + 16 * len(sizes)
        for s, b in zip(sizes, blobs):
            f.write(struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(b), off)); off += len(b)
        for b in blobs: f.write(b)

if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "src/FranCaptureStudio/Assets")
