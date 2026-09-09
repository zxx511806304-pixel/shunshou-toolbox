"""Render the original geometric toolbox app mark (no external artwork). Requires Pillow."""
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1] / 'src' / 'Shunshou.App' / 'Assets'
root.mkdir(parents=True, exist_ok=True)
image = Image.new('RGBA', (1024, 1024))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((32, 32, 992, 992), radius=225, fill='#087E81')
draw.rounded_rectangle((367, 234, 657, 414), radius=53, outline='#FFFFFF', width=46)
draw.rounded_rectangle((205, 360, 819, 759), radius=61, fill='#FFFFFF')
draw.rectangle((205, 503, 819, 531), fill='#087E81')
draw.rounded_rectangle((451, 475, 573, 571), radius=24, fill='#E5B97A')
draw.rounded_rectangle((294, 646, 730, 672), radius=13, fill='#CFE5E3')
image.resize((256, 256), Image.Resampling.LANCZOS).save(root / 'Toolbox.png')
image.save(root / 'AppIcon.ico', format='ICO', sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
print(root / 'AppIcon.ico')
