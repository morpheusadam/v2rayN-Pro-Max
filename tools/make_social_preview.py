"""Build the 1280x640 social preview card.

GitHub shows this image whenever the repository link is pasted into Telegram, Twitter,
Discord or Slack — for a project whose audience lives in chat apps, it is the first thing
most people ever see of it, so it is worth generating properly rather than letting GitHub
fall back to a blank card with an avatar on it.

    python tools/make_social_preview.py

Upload the result by hand: Settings -> General -> Social preview. GitHub has no API for it.
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
IMAGES = ROOT / "docs" / "images"
OUT = IMAGES / "social-preview.png"

W, H = 1280, 640
BG = (22, 22, 26)
PANEL = (30, 30, 36)
ACCENT = (241, 90, 43)
WHITE = (245, 245, 247)
MUTED = (150, 152, 160)
GREEN = (46, 158, 107)

FONTS = Path("C:/Windows/Fonts")

# The application window inside docs/images/1.png, measured once off the capture.
WINDOW_BOX = (308, 133, 1946, 1296)


def font(name: str, size: int) -> ImageFont.FreeTypeFont:
    # Segoe UI is on every Windows install; falling back keeps the script runnable
    # elsewhere even if the result is less pretty.
    for candidate in (FONTS / name, Path("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf")):
        if candidate.exists():
            return ImageFont.truetype(str(candidate), size)
    return ImageFont.load_default(size)


def rounded(draw: ImageDraw.ImageDraw, box, radius, fill):
    draw.rounded_rectangle(box, radius=radius, fill=fill)


def main() -> None:
    card = Image.new("RGB", (W, H), BG)
    draw = ImageDraw.Draw(card)

    # A wide accent bar down the left edge rather than a full gradient: it survives the
    # heavy downscaling chat clients apply to preview thumbnails.
    draw.rectangle([0, 0, 10, H], fill=ACCENT)

    # ---- screenshot, cropped to the window and bled off the right edge ------------
    # 1.png is a full desktop capture. The wallpaper around the window is loud orange
    # and fights the accent colour, so only the application itself goes on the card.
    shot = Image.open(IMAGES / "1.png").convert("RGB").crop(WINDOW_BOX)
    shot_w = 760
    shot_h = round(shot.height * shot_w / shot.width)
    shot = shot.resize((shot_w, shot_h), Image.LANCZOS)

    panel = Image.new("RGB", (shot_w + 12, shot_h + 12), (58, 58, 66))
    panel.paste(shot, (6, 6))
    card.paste(panel, (612, 56))

    # ---- wordmark ----------------------------------------------------------------
    logo = Image.open(IMAGES / "logo.png").convert("RGBA").resize((92, 92), Image.LANCZOS)
    card.paste(logo, (72, 72), logo)

    draw.text((184, 78), "v2rayN", font=font("segoeuib.ttf", 54), fill=WHITE)
    draw.text((184, 132), "Pro Max", font=font("segoeuib.ttf", 54), fill=ACCENT)

    # ---- the pitch ---------------------------------------------------------------
    draw.text((72, 232), "Press one button.", font=font("segoeuib.ttf", 44), fill=WHITE)
    draw.text((72, 288), "Get working servers.", font=font("segoeuib.ttf", 44), fill=WHITE)

    lines = [
        "Reads your subscription links, tests what is",
        "actually alive, measures real speed, and keeps",
        "the fastest servers ready to use.",
    ]
    y = 366
    for line in lines:
        draw.text((72, y), line, font=font("segoeui.ttf", 25), fill=MUTED)
        y += 36

    # ---- pills -------------------------------------------------------------------
    small = font("segoeuib.ttf", 20)
    x = 72
    for label, colour in (("Windows + Linux", PANEL), ("Xray · sing-box · mihomo", PANEL),
                          ("open source", GREEN)):
        w = draw.textlength(label, font=small)
        rounded(draw, [x, 500, x + w + 34, 542], 21, colour)
        draw.text((x + 17, 509), label, font=small, fill=WHITE)
        x += w + 46

    draw.text((72, 574), "github.com/morpheusadam/v2rayN-Pro-Max",
              font=font("segoeui.ttf", 22), fill=ACCENT)

    OUT.parent.mkdir(parents=True, exist_ok=True)
    card.save(OUT, optimize=True)
    print(f"wrote {OUT} ({OUT.stat().st_size / 1024:.0f} KB, {W}x{H})")


if __name__ == "__main__":
    main()
