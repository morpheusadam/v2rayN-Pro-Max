"""
Regenerate every application and tray icon from one source logo.

Run it after replacing logo-source.png:

    python src/tools/make_icons.py                    # uses src/tools/logo-source.png
    python src/tools/make_icons.py path/to/logo.png   # or any other file

Give it a square PNG with a transparent background, ideally 1024x1024. The largest
icon frame is 256x256, so anything smaller than that has to be scaled up and the big
frames come out soft.

What it writes:
  Assets/Resources  v2rayN.ico      the application icon (window, taskbar, alt-tab)
                    NotifyIcon1-4   the tray icons, one colour per routing mode
  install dir       v2rayN.png      the plain image the cross-platform build ships

Three details here exist because of how Windows and Avalonia actually behave, and
undoing any of them brings back a bug that looked unrelated:

  * Frames below 256px are written as uncompressed BMP. Pillow writes PNG for every
    frame, and Windows renders a PNG-compressed small frame as a blank icon.
  * Frames at 32px and below use a filled disc rather than the outline artwork. A
    thin outline at 16px — the size the taskbar uses — is an unreadable smudge.
  * The tray icons keep four distinguishable colours, because that difference is how
    the tray already tells you which routing mode is active.
"""
import colorsys
import io
import os
import struct
import sys

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)

DEFAULT_SOURCE = os.path.join(HERE, "logo-source.png")
WPF_RESOURCES = os.path.join(REPO, "v2rayN", "v2rayN", "Resources")
AVALONIA_ASSETS = os.path.join(REPO, "v2rayN", "v2rayN.Desktop", "Assets")
INSTALL_DIR = r"C:\myApp\V2rayN"

ICO_SIZES = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40),
             (48, 48), (64, 64), (128, 128), (256, 256)]

# Below this, the outline artwork stops being legible and the filled mark is used.
SOLID_BELOW = 32

# The brand accent #F15A2B expressed as HSV, which is how the recolouring works.
BRAND_HUE, BRAND_SATURATION, BRAND_VALUE = 0.0396, 0.82, 0.95

# Hue per routing mode. Mode 1 is the default and carries the brand colour.
MODES = {
    "NotifyIcon1": BRAND_HUE,
    "NotifyIcon2": 0.95,   # magenta
    "NotifyIcon3": 0.78,   # purple
    "NotifyIcon4": 0.42,   # green
}

# Saturation and brightness of the supplied artwork, used to keep its own shading
# when the hue is replaced.
SOURCE_SATURATION, SOURCE_VALUE = 0.36, 0.88

# The warm accent inside the logo is left alone by every recolouring pass.
ACCENT_HUE_RANGE = (0.02, 0.13)


def load_trimmed(path):
    """Trim the transparent margin so the mark fills the icon rather than floating."""
    im = Image.open(path).convert("RGBA")
    bbox = im.getchannel("A").getbbox()
    if bbox:
        im = im.crop(bbox)

    side = max(im.size)
    pad = int(side * 0.04)
    canvas = Image.new("RGBA", (side + pad * 2, side + pad * 2), (0, 0, 0, 0))
    canvas.paste(im, ((canvas.width - im.width) // 2, (canvas.height - im.height) // 2))
    return canvas


def recolor(im, hue, saturation=None, value=None):
    """Rotate the artwork onto `hue`, leaving the warm accent ellipse untouched."""
    out = im.copy()
    px = out.load()
    for y in range(out.height):
        for x in range(out.width):
            r, g, b, a = px[x, y]
            if a == 0:
                continue
            h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            if s <= 0.15 or ACCENT_HUE_RANGE[0] < h < ACCENT_HUE_RANGE[1]:
                continue
            # Scaled rather than replaced, so highlights and shadows survive.
            ns = s if saturation is None else min(1.0, saturation * (s / SOURCE_SATURATION))
            nv = v if value is None else min(1.0, value * (v / SOURCE_VALUE))
            nr, ng, nb = colorsys.hsv_to_rgb(hue, ns, nv)
            px[x, y] = (int(nr * 255), int(ng * 255), int(nb * 255), a)
    return out


def on_disc(im, hue):
    """Put the mark in white on a filled disc, so it survives 16px on any taskbar."""
    r, g, b = colorsys.hsv_to_rgb(hue, BRAND_SATURATION, BRAND_VALUE)
    canvas = Image.new("RGBA", im.size, (0, 0, 0, 0))
    inset = int(im.width * 0.045)
    ImageDraw.Draw(canvas).ellipse(
        [inset, inset, im.width - inset, im.height - inset],
        fill=(int(r * 255), int(g * 255), int(b * 255), 255))

    mark = im.copy()
    px = mark.load()
    for y in range(mark.height):
        for x in range(mark.width):
            pr, pg, pb, pa = px[x, y]
            if pa == 0:
                continue
            h, s, v = colorsys.rgb_to_hsv(pr / 255, pg / 255, pb / 255)
            if s > 0.15 and not (ACCENT_HUE_RANGE[0] < h < ACCENT_HUE_RANGE[1]):
                nr, ng, nb = colorsys.hsv_to_rgb(h, 0.05, 1.0)
                px[x, y] = (int(nr * 255), int(ng * 255), int(nb * 255), pa)

    canvas.alpha_composite(mark)
    return canvas


def bmp_frame(im):
    """One icon frame as an uncompressed DIB, which is what Windows wants below 256px."""
    w, h = im.size
    px = im.load()

    # BITMAPINFOHEADER. Height is doubled because the AND mask counts as image rows.
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)

    rows = []
    for y in range(h - 1, -1, -1):          # bottom-up
        row = bytearray()
        for x in range(w):
            r, g, b, a = px[x, y]
            row += bytes((b, g, r, a))      # BGRA
        rows.append(bytes(row))

    # Unused for 32-bit frames, but the format still requires it, padded per row.
    mask_stride = ((w + 31) // 32) * 4
    return header + b"".join(rows) + b"\x00" * (mask_stride * h)


def save_ico(path, outline, solid):
    """Write the multi-size ICO, picking artwork and encoding per frame size."""
    outline = outline.resize((256, 256), Image.LANCZOS)
    solid = solid.resize((256, 256), Image.LANCZOS)

    payloads = []
    for size in ICO_SIZES:
        frame = (solid if size[0] <= SOLID_BELOW else outline).resize(size, Image.LANCZOS)
        if size[0] >= 256:
            buf = io.BytesIO()
            frame.save(buf, format="PNG")
            payloads.append(buf.getvalue())
        else:
            payloads.append(bmp_frame(frame))

    offset = 6 + 16 * len(ICO_SIZES)
    out = bytearray(struct.pack("<HHH", 0, 1, len(ICO_SIZES)))
    for (w, h), payload in zip(ICO_SIZES, payloads):
        out += struct.pack("<BBBBHHII",
                           0 if w >= 256 else w,
                           0 if h >= 256 else h,
                           0, 0, 1, 32, len(payload), offset)
        offset += len(payload)
    for payload in payloads:
        out += payload

    with open(path, "wb") as f:
        f.write(bytes(out))
    print(f"  {os.path.relpath(path, REPO)}")


def main():
    source_path = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_SOURCE
    supplied = load_trimmed(source_path)
    print(f"source: {source_path}  ({supplied.width}x{supplied.height} after trim)")
    if supplied.width < 256:
        print("  note: smaller than 256px, so the large frames will be upscaled and soft")

    brand = recolor(supplied, BRAND_HUE, BRAND_SATURATION, BRAND_VALUE)
    brand_solid = on_disc(supplied, BRAND_HUE)

    targets = [d for d in (WPF_RESOURCES, AVALONIA_ASSETS) if os.path.isdir(d)]

    print("app icon:")
    for directory in targets:
        save_ico(os.path.join(directory, "v2rayN.ico"), brand, brand_solid)

    print("tray icons:")
    for name, hue in MODES.items():
        solid = on_disc(supplied, hue)
        tinted = recolor(supplied, hue, BRAND_SATURATION, BRAND_VALUE)
        for directory in targets:
            save_ico(os.path.join(directory, f"{name}.ico"), tinted, solid)

    if os.path.isdir(INSTALL_DIR):
        brand.resize((256, 256), Image.LANCZOS).save(os.path.join(INSTALL_DIR, "v2rayN.png"))
        print(f"install image:\n  {os.path.join(INSTALL_DIR, 'v2rayN.png')}")

    print("\nWindows caches icons per file path. If the taskbar still shows the old one:")
    print("  Stop-Process -Name explorer -Force")
    print("  Remove-Item \"$env:LOCALAPPDATA\\Microsoft\\Windows\\Explorer\\iconcache*\" -Force")
    print("  Start-Process explorer.exe")


if __name__ == "__main__":
    main()
