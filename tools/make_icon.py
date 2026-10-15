"""生成 LanTalk 程序图标（assets/LanTalk.ico）。
仅构建期使用，运行期不需要 Python。

做法：
  1. 从 assets/icon-source.png 出发，把**白底**变成透明底。
     不能简单地"白色→透明"——角色头上的白色女仆发带本身也是白的，会被一起挖掉。
     这里用**从四边往里泛洪填充**（flood fill）：只有和画面边缘连通的白才算背景，
     被描边围住的白色（发带、领结、眼睛高光）会原样保留。
  2. 边缘那圈和白色混过的浅色像素按"有多白"给一个渐变透明度，去掉白边毛刺。
  3. 补成正方形、留一点透明边距、放大到 256，再生成 16~256 各尺寸。
     小尺寸额外锐化一下，免得在任务栏里糊成一团。
"""
import os
from collections import deque

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "assets")
SRC = os.path.join(ASSETS, "icon-source.png")
ICO = os.path.join(ASSETS, "LanTalk.ico")
PNG = os.path.join(ASSETS, "LanTalk.png")

BG_THRESHOLD = 240      # 最小通道 >= 这个值才算"白"
EDGE_LO, EDGE_HI = 205, 255   # 边缘像素的"白度"映射区间
PAD_RATIO = 0.03        # 四周留出的透明边距比例
CORNER_RATIO = 0.18     # 整体圆角（源图是紧裁的，头发在四边被切齐，圆一下更像应用图标）
SIZES = [16, 24, 32, 48, 64, 128, 256]


def background_mask(rgb):
    """从四边往里泛洪填充，返回 True=背景 的布尔图。"""
    h, w, _ = rgb.shape
    near_white = rgb.min(axis=2) >= BG_THRESHOLD
    vis = np.zeros((h, w), bool)
    dq = deque()

    def push(y, x):
        if near_white[y, x] and not vis[y, x]:
            vis[y, x] = True
            dq.append((y, x))

    for x in range(w):
        push(0, x)
        push(h - 1, x)
    for y in range(h):
        push(y, 0)
        push(y, w - 1)

    while dq:
        y, x = dq.popleft()
        if y > 0:
            push(y - 1, x)
        if y < h - 1:
            push(y + 1, x)
        if x > 0:
            push(y, x - 1)
        if x < w - 1:
            push(y, x + 1)
    return vis


def dilate(mask):
    out = mask.copy()
    out[1:, :] |= mask[:-1, :]
    out[:-1, :] |= mask[1:, :]
    out[:, 1:] |= mask[:, :-1]
    out[:, :-1] |= mask[:, 1:]
    return out


def build_rgba():
    im = Image.open(SRC).convert("RGB")
    rgb = np.array(im).astype(np.int16)
    h, w, _ = rgb.shape

    bg = background_mask(rgb)
    ring = dilate(bg) & ~bg          # 紧贴背景的那一圈角色像素

    alpha = np.full((h, w), 255.0)
    alpha[bg] = 0.0
    if ring.any():
        mn = rgb.min(axis=2).astype(float)
        whiteness = np.clip((mn - EDGE_LO) / float(EDGE_HI - EDGE_LO), 0.0, 1.0)
        alpha[ring] = 255.0 * (1.0 - whiteness[ring])

    a_img = Image.fromarray(alpha.astype(np.uint8), "L").filter(
        ImageFilter.GaussianBlur(0.6))     # 让边缘过渡自然一点

    out = Image.merge("RGBA", (
        Image.fromarray(rgb[:, :, 0].astype(np.uint8), "L"),
        Image.fromarray(rgb[:, :, 1].astype(np.uint8), "L"),
        Image.fromarray(rgb[:, :, 2].astype(np.uint8), "L"),
        a_img,
    ))
    return out


def rounded_mask(size, radius_ratio, ss=4):
    """抗锯齿的圆角矩形遮罩（先在放大画布上画，再缩回来）。"""
    r = int(size * radius_ratio)
    m = Image.new("L", (size * ss, size * ss), 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, size * ss - 1, size * ss - 1], radius=r * ss, fill=255)
    return m.resize((size, size), Image.LANCZOS)


def apply_corner(img, ratio):
    a = np.array(img)
    m = np.array(rounded_mask(img.size[0], ratio)).astype(int)
    a[:, :, 3] = (a[:, :, 3].astype(int) * m // 255).astype(np.uint8)
    return Image.fromarray(a, "RGBA")


def make_square(img, pad_ratio):
    """补成正方形并留出透明边距。"""
    w, h = img.size
    side = int(max(w, h) * (1.0 + 2 * pad_ratio))
    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.paste(img, ((side - w) // 2, (side - h) // 2), img)
    return canvas


def main():
    base = build_rgba()
    print("去白底后：不透明像素 %.1f%%" % (
        100.0 * (np.array(base.getchannel("A")) > 0).sum() / (base.size[0] * base.size[1])))

    sq = make_square(base, PAD_RATIO)
    big = sq.resize((256, 256), Image.LANCZOS)
    big = big.filter(ImageFilter.UnsharpMask(radius=1.2, percent=55, threshold=3))
    big = apply_corner(big, CORNER_RATIO)

    frames = []
    for s in SIZES:
        f = big.resize((s, s), Image.LANCZOS) if s != 256 else big.copy()
        f = apply_corner(f, CORNER_RATIO)
        if s <= 32:
            f = f.filter(ImageFilter.UnsharpMask(radius=0.6, percent=90, threshold=2))
        frames.append(f)

    frames[-1].save(ICO, format="ICO", sizes=[(s, s) for s in SIZES], append_images=frames[:-1])
    big.save(PNG, format="PNG")
    print("icon ->", ICO, os.path.getsize(ICO), "bytes")
    print("png  ->", PNG, os.path.getsize(PNG), "bytes")

    # 预览：浅底 / 深底 / 各尺寸实际大小
    def sheet(bg):
        pad = 12
        row = Image.new("RGBA", (sum(s + pad for s in SIZES) + pad, 256 + pad * 2), bg)
        x = pad
        for f in frames:
            row.paste(f, (x, pad + (256 - f.size[1]) // 2), f)
            x += f.size[0] + pad
        return row

    light = sheet((245, 245, 245, 255))
    dark = sheet((40, 44, 52, 255))
    out = Image.new("RGBA", (light.size[0], light.size[1] * 2), (0, 0, 0, 0))
    out.paste(light, (0, 0))
    out.paste(dark, (0, light.size[1]))
    prev = os.path.join(ROOT, "dist", "_icon_preview.png")
    os.makedirs(os.path.dirname(prev), exist_ok=True)
    out.convert("RGB").save(prev)
    print("预览 ->", prev)


if __name__ == "__main__":
    main()
