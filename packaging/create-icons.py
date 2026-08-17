"""Create the small native icons used by the portable streamer."""

import argparse
from pathlib import Path

from PIL import Image, ImageDraw


SIZES = [16, 24, 32, 48, 64, 128, 256]


def make_icon(accent):
    canvas_size = 1024
    image = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    draw.rounded_rectangle(
        (38, 38, 986, 986),
        radius=220,
        fill="#121827",
        outline="#3a4865",
        width=28,
    )
    draw.rounded_rectangle(
        (188, 170, 836, 650),
        radius=64,
        outline="#edf3ff",
        width=42,
    )
    draw.line((420, 738, 604, 738), fill="#edf3ff", width=42)
    draw.line((512, 650, 512, 738), fill="#edf3ff", width=42)
    draw.arc((282, 306, 742, 766), 215, 325, fill=accent, width=32)
    draw.ellipse((622, 594, 808, 780), fill=accent, outline="#ffffff", width=18)
    draw.ellipse((680, 652, 750, 722), fill="#ffffff")

    return image


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)

    for name, color in {
        "streamer.ico": "#7185ff",
        "streamer-red.ico": "#e34b60",
        "streamer-green.ico": "#36c98f",
    }.items():
        make_icon(color).save(
            args.output / name,
            format="ICO",
            sizes=[(size, size) for size in SIZES],
        )


if __name__ == "__main__":
    main()
