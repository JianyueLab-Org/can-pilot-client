"""Create the UI bitmap from the same CAN ICO used by the executable."""
from pathlib import Path
from PIL import Image

root = Path(__file__).resolve().parents[1]
with Image.open(root / "src/CanClient/app.ico") as image:
    image.convert("RGBA").save(root / "src/CanClient/app-logo.png")
