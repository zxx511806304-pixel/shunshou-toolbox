"""Package the approved RGBA brand master into Windows icon sizes. Requires Pillow.

This only resizes/encodes the approved artwork; no drawing or background editing.
"""
from pathlib import Path
from PIL import Image

repo = Path(__file__).resolve().parents[1]
source = repo / 'design' / 'brand' / 'ShunshouToolbox-master.png'
root = repo / 'src' / 'Shunshou.App' / 'Assets'
root.mkdir(parents=True, exist_ok=True)
image = Image.open(source)
image.load()
if image.mode != 'RGBA' or image.width != image.height:
    raise ValueError('Brand master must be a square RGBA image.')
if image.getchannel('A').getextrema() != (0, 255):
    raise ValueError('Brand master must contain true transparency and opaque artwork.')
image.resize((256, 256), Image.Resampling.LANCZOS).save(root / 'Toolbox.png')
image.save(root / 'AppIcon.ico', format='ICO', sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
with Image.open(root / 'AppIcon.ico') as icon:
    if icon.ico.sizes() != {(size, size) for size in (16,24,32,48,64,128,256)}:
        raise ValueError('ICO is missing a required Windows icon size.')
print(root / 'AppIcon.ico')
