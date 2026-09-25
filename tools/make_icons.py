"""
Generates the Mechanic AI logo and the standard WinUI 3 asset set.

Mark: a rounded navy tile with a white open-end wrench on the diagonal and a blue
diagnostic pulse (ECG-style trace) crossing it. Tiny sizes (<= 24 px) drop the pulse
so the wrench stays legible.

Usage:  python make_icons.py <output Assets directory>
Requires Pillow.
"""
import io
import math
import struct
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

NAVY_TOP = (22, 48, 79)
NAVY_BOTTOM = (15, 27, 45)
WHITE = (255, 255, 255)
BLUE = (61, 139, 253)
AMBER = (255, 176, 32)
SS = 4  # supersampling factor
BASE = 1024  # design grid


def circle_points(cx, cy, r, steps=96):
    return [(cx + r * math.cos(2 * math.pi * i / steps), cy + r * math.sin(2 * math.pi * i / steps)) for i in range(steps)]


def transform(points, angle_deg, dx, dy, scale):
    a = math.radians(angle_deg)
    ca, sa = math.cos(a), math.sin(a)
    return [((x * ca - y * sa) * scale + dx, (x * sa + y * ca) * scale + dy) for x, y in points]


def wrench_mask(size, simplified):
    """White wrench mask (L mode) on a size x size canvas (already supersampled)."""
    s = size / BASE * (1.12 if simplified else 1.0)
    mask = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(mask)
    angle = -45
    cx = cy = size / 2
    # Local geometry (wrench along +x, head to the right).
    handle_w = 70 if simplified else 60
    handle = [(-320, -handle_w), (190, -handle_w), (190, handle_w), (-320, handle_w)]
    tail = circle_points(-320, 0, handle_w)
    head = circle_points(245, 0, 165 if simplified else 158)
    jaw_w = 70 if simplified else 64
    jaw = [(245, -jaw_w), (460, -jaw_w), (460, jaw_w), (245, jaw_w)]
    jaw_round = circle_points(245, 0, jaw_w)
    hole = circle_points(-320, 0, 26)
    for shape in (handle, tail, head):
        d.polygon(transform(shape, angle, cx, cy, s), fill=255)
    for shape in (jaw, jaw_round):
        d.polygon(transform(shape, angle, cx, cy, s), fill=0)
    if not simplified:
        d.polygon(transform(hole, angle, cx, cy, s), fill=0)
    return mask


def pulse_points(size):
    s = size / BASE
    pts = [(150, 700), (360, 700), (415, 600), (480, 820), (545, 540), (600, 700), (874, 700)]
    return [(x * s, y * s) for x, y in pts]


def draw_polyline(draw, pts, width, fill):
    draw.line(pts, fill=fill, width=int(width), joint="curve")
    r = width / 2
    for x, y in (pts[0], pts[-1]):
        draw.ellipse((x - r, y - r, x + r, y + r), fill=fill)


def render_mark(px, simplified=None, plate=True):
    """Renders the logo mark at px x px (RGBA)."""
    if simplified is None:
        simplified = px <= 32
    size = px * SS
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))

    if plate:
        # Vertical gradient navy tile with rounded corners.
        grad = Image.new("RGBA", (size, size))
        gd = ImageDraw.Draw(grad)
        for y in range(size):
            t = y / max(1, size - 1)
            c = tuple(int(NAVY_TOP[i] * (1 - t) + NAVY_BOTTOM[i] * t) for i in range(3))
            gd.line([(0, y), (size, y)], fill=c + (255,))
        tile = Image.new("L", (size, size), 0)
        radius = int(size * 0.22)
        inset = 0 if px <= 32 else int(size * 0.02)
        ImageDraw.Draw(tile).rounded_rectangle((inset, inset, size - 1 - inset, size - 1 - inset), radius=radius, fill=255)
        img.paste(grad, (0, 0), tile)

    wrench_color = WHITE if plate else NAVY_BOTTOM
    wrench = wrench_mask(size, simplified)
    img.paste(Image.new("RGBA", (size, size), wrench_color + (255,)), (0, 0), wrench)

    if not simplified:
        pts = pulse_points(size)
        width = 46 * size / BASE
        # Knock-out outline so the trace reads cleanly where it crosses the wrench.
        gap = Image.new("L", (size, size), 0)
        draw_polyline(ImageDraw.Draw(gap), pts, width + 34 * size / BASE, 255)
        if plate:
            bg = Image.new("RGBA", (size, size), NAVY_BOTTOM + (255,))
            img.paste(bg, (0, 0), Image.composite(gap, Image.new("L", (size, size), 0), wrench))
        else:
            cleared = Image.new("RGBA", (size, size), (0, 0, 0, 0))
            img.paste(cleared, (0, 0), Image.composite(gap, Image.new("L", (size, size), 0), wrench))
        trace = Image.new("L", (size, size), 0)
        draw_polyline(ImageDraw.Draw(trace), pts, width, 255)
        img.paste(Image.new("RGBA", (size, size), BLUE + (255,)), (0, 0), trace)
        # Amber "reading" dot at the end of the trace.
        ex, ey = pts[-1]
        r = width * 0.95
        ImageDraw.Draw(img).ellipse((ex - r, ey - r, ex + r, ey + r), fill=AMBER + (255,))

    return img.resize((px, px), Image.LANCZOS)


def fit_on_canvas(w, h, mark_px, background=None, text=None):
    canvas = Image.new("RGBA", (w, h), background + (255,) if background else (0, 0, 0, 0))
    mark = render_mark(mark_px)
    if text:
        draw = ImageDraw.Draw(canvas)
        gap = mark_px * 0.18
        font_px = int(mark_px * 0.42)
        while True:
            font = load_font(font_px)
            tw = draw.textlength(text, font=font)
            total = mark_px + gap + tw
            if total <= w * 0.86 or font_px <= 8:
                break
            font_px -= 2
        x0 = int((w - total) / 2)
        canvas.alpha_composite(mark, (x0, (h - mark_px) // 2))
        bbox = draw.textbbox((0, 0), text, font=font)
        th = bbox[3] - bbox[1]
        draw.text((x0 + mark_px + gap, (h - th) / 2 - bbox[1]), text, font=font, fill=WHITE + (255,))
    else:
        canvas.alpha_composite(mark, ((w - mark_px) // 2, (h - mark_px) // 2))
    return canvas


def load_font(px):
    for candidate in (
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
        "C:/Windows/Fonts/segoeuib.ttf",
    ):
        if Path(candidate).exists():
            return ImageFont.truetype(candidate, px)
    return ImageFont.load_default()


def write_ico(path, sizes):
    """ICO with PNG-compressed frames (supported since Windows Vista)."""
    frames = []
    for s in sizes:
        buf = io.BytesIO()
        render_mark(s).save(buf, format="PNG", optimize=True)
        frames.append((s, buf.getvalue()))
    header = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    directory = b""
    data = b""
    for s, png in frames:
        dim = 0 if s >= 256 else s
        directory += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(png), offset)
        offset += len(png)
        data += png
    Path(path).write_bytes(header + directory + data)


def main():
    out = Path(sys.argv[1] if len(sys.argv) > 1 else "Assets")
    out.mkdir(parents=True, exist_ok=True)
    navy = NAVY_BOTTOM

    def save(img, name):
        img.save(out / name, format="PNG", optimize=True)
        print(f"{name}: {img.size[0]}x{img.size[1]}")

    save(render_mark(48), "LockScreenLogo.scale-200.png")
    save(fit_on_canvas(1240, 600, 300, text="Mechanic AI"), "SplashScreen.scale-200.png")
    save(fit_on_canvas(300, 300, 216), "Square150x150Logo.scale-200.png")
    save(render_mark(88), "Square44x44Logo.scale-200.png")
    save(render_mark(24), "Square44x44Logo.targetsize-24_altform-unplated.png")
    save(render_mark(48), "Square44x44Logo.targetsize-48_altform-lightunplated.png")
    save(render_mark(50), "StoreLogo.png")
    save(fit_on_canvas(620, 300, 180, background=navy, text="Mechanic AI"), "Wide310x150Logo.scale-200.png")
    write_ico(out / "AppIcon.ico", [16, 24, 32, 48, 64, 256])
    print("AppIcon.ico: 16, 24, 32, 48, 64, 256")

    # Preview sheet for review (not an app asset).
    preview = Image.new("RGBA", (16 + 24 + 32 + 48 + 64 + 256 + 70, 270), (240, 240, 240, 255))
    x = 10
    for s in (16, 24, 32, 48, 64, 256):
        preview.alpha_composite(render_mark(s), (x, 10))
        x += s + 10
    preview.save(Path(__file__).with_name("preview.png"))


if __name__ == "__main__":
    main()
