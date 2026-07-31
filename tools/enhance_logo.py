"""
Rebuild the source logo at high resolution.

The supplied artwork is 250x250 and the largest icon frame is 256x256, so the big
frames had to be upscaled and came out soft. This is not a photo, though — it is flat
vector-style art: two solid colours and an antialiased edge between them and
transparency. That structure can be reconstructed rather than merely interpolated.

The steps, and why each one is there:

  1. Upscale 4x with Lanczos. On its own this gives a blurred edge, because
     interpolation spreads the original edge pixels over four times the distance.
  2. Re-sharpen the alpha with a smoothstep curve. Flat art has a hard boundary that
     was only ever softened by antialiasing, so pushing partial alpha back towards 0
     or 1 restores a crisp edge while keeping a single pixel of antialiasing.
  3. Snap the colours. Interpolation invents in-between hues along every edge; each
     opaque pixel is pulled back to whichever of the artwork's two real colours it is
     closest to, which removes the muddy fringe.

The result is a genuine 1000x1000 master, not an upscale that merely looks bigger.
"""
import os
import sys

from PIL import Image, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_SOURCE = os.path.join(HERE, "logo-source.png")

SCALE = 4
# Alpha below this is pushed to transparent, above it to opaque; the band between is
# what stays as antialiasing.
EDGE_LOW, EDGE_HIGH = 0.35, 0.72


def dominant_colours(im, limit=2):
    """The artwork's real colours, ignoring the antialiased edge pixels."""
    counts = {}
    px = im.load()
    for y in range(im.height):
        for x in range(im.width):
            r, g, b, a = px[x, y]
            if a < 250:
                continue
            counts[(r, g, b)] = counts.get((r, g, b), 0) + 1
    return [c for c, _ in sorted(counts.items(), key=lambda kv: -kv[1])[:limit]]


def smoothstep(t, low, high):
    if t <= low:
        return 0.0
    if t >= high:
        return 1.0
    x = (t - low) / (high - low)
    return x * x * (3 - 2 * x)


def enhance(src_path, out_path, scale=SCALE):
    src = Image.open(src_path).convert("RGBA")
    palette = dominant_colours(src)
    print(f"source {src.width}x{src.height}, base colours: "
          + ", ".join("#%02X%02X%02X" % c for c in palette))

    size = (src.width * scale, src.height * scale)
    big = src.resize(size, Image.LANCZOS)

    # A slight blur before re-thresholding keeps the restored edge smooth rather than
    # jagged; the threshold below is what makes it crisp again.
    alpha = big.getchannel("A").filter(ImageFilter.GaussianBlur(scale * 0.25))
    rgb = big.convert("RGB").filter(ImageFilter.GaussianBlur(scale * 0.15))

    out = Image.new("RGBA", size)
    a_px, c_px, o_px = alpha.load(), rgb.load(), out.load()

    for y in range(size[1]):
        for x in range(size[0]):
            a = smoothstep(a_px[x, y] / 255.0, EDGE_LOW, EDGE_HIGH)
            if a <= 0:
                o_px[x, y] = (0, 0, 0, 0)
                continue

            r, g, b = c_px[x, y]
            if palette:
                # Nearest real colour, so edges stop carrying invented in-between hues.
                r, g, b = min(palette, key=lambda c: (c[0] - r) ** 2 + (c[1] - g) ** 2 + (c[2] - b) ** 2)
            o_px[x, y] = (r, g, b, int(round(a * 255)))

    out.save(out_path)
    print(f"wrote {out_path}  ({out.width}x{out.height})")
    return out


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_SOURCE
    out = sys.argv[2] if len(sys.argv) > 2 else DEFAULT_SOURCE
    enhance(src, out)


if __name__ == "__main__":
    main()
