from pathlib import Path
from PIL import Image, ImageDraw


# 使用高分辨率画布绘制，再生成 Windows 常用尺寸，保证小图标边缘清晰。
ROOT = Path(__file__).resolve().parents[1]
ASSET_DIR = ROOT / "src" / "GameManagement.App" / "Assets"
SIZE = 1024
SCALE = SIZE / 512


def p(value: float) -> int:
    return round(value * SCALE)


image = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
pixels = image.load()
for y in range(SIZE):
    ratio = y / (SIZE - 1)
    color = tuple(round(a + (b - a) * ratio) for a, b in zip((23, 42, 85), (7, 19, 38)))
    for x in range(SIZE):
        pixels[x, y] = (*color, 255)

mask = Image.new("L", (SIZE, SIZE), 0)
ImageDraw.Draw(mask).rounded_rectangle((p(24), p(24), p(488), p(488)), radius=p(108), fill=255)
image.putalpha(mask)
draw = ImageDraw.Draw(image)

# 三层资料库托盘代表统一管理的游戏、版本与存档。
draw.rounded_rectangle((p(96), p(142), p(416), p(207)), radius=p(30), fill=(58, 148, 244, 180))
draw.rounded_rectangle((p(80), p(206), p(432), p(273)), radius=p(28), fill=(57, 165, 250, 220))
draw.rounded_rectangle((p(64), p(274), p(448), p(348)), radius=p(28), fill=(55, 119, 255, 255))

# 白色手柄作为最醒目的主体，控制在任务栏小尺寸下仍可识别。
gamepad = [(p(151), p(228)), (p(361), p(228)), (p(405), p(247)), (p(428), p(287)),
           (p(446), p(357)), (p(438), p(382)), (p(418), p(397)), (p(393), p(388)),
           (p(359), p(353)), (p(153), p(353)), (p(119), p(388)), (p(94), p(397)),
           (p(74), p(382)), (p(66), p(357)), (p(84), p(287)), (p(107), p(247))]
draw.polygon(gamepad, fill=(246, 250, 255, 255))
draw.line(gamepad + [gamepad[0]], fill=(185, 215, 255, 255), width=p(8), joint="curve")
draw.line((p(154), p(274), p(154), p(332)), fill=(23, 52, 99, 255), width=p(22))
draw.line((p(125), p(303), p(183), p(303)), fill=(23, 52, 99, 255), width=p(22))
draw.ellipse((p(334), p(272), p(362), p(300)), fill=(51, 119, 255, 255))
draw.ellipse((p(370), p(306), p(398), p(334)), fill=(85, 223, 255, 255))
draw.ellipse((p(261), p(311), p(283), p(333)), fill=(23, 52, 99, 190))
draw.ellipse((p(301), p(311), p(323), p(333)), fill=(23, 52, 99, 190))

png_path = ASSET_DIR / "GameManagement.png"
ico_path = ASSET_DIR / "GameManagement.ico"
image.save(png_path, optimize=True)
image.save(ico_path, format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print(png_path)
print(ico_path)
