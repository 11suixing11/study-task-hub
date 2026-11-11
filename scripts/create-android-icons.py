"""Reuse the established product artwork for Android launcher resources."""
from pathlib import Path
from PIL import Image

root = Path(__file__).resolve().parents[1]
resources = root / "src/AccessProbe.Android/Resources"
source = Image.open(root / "design/icons/app-1024.png").convert("RGBA")
for density, pixels in {"mdpi": 48, "hdpi": 72, "xhdpi": 96, "xxhdpi": 144, "xxxhdpi": 192}.items():
    target = resources / f"mipmap-{density}" / "app_launcher.png"
    target.parent.mkdir(parents=True, exist_ok=True)
    source.resize((pixels, pixels), Image.Resampling.LANCZOS).save(target)

# The adaptive icon safe area is 66 of the 108 dp layer. Keep the complete
# task-card artwork within that area so circle and squircle masks retain it.
foreground = Image.new("RGBA", (432, 432))
artwork = source.resize((264, 264), Image.Resampling.LANCZOS)
foreground.alpha_composite(artwork, (84, 84))
target = resources / "drawable-xxxhdpi/app_icon_foreground.png"
target.parent.mkdir(parents=True, exist_ok=True)
foreground.save(target)
print("Generated five launcher densities and one adaptive foreground.")
