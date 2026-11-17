"""render_unit.py の連番画像をドット絵のスプライトシートに変換する。

    python pixelize.py build/spearman_raw build/spearman --scale 4 --colors 20

- 縮小(面積平均)→ 透明度の二値化 → シート全体で共通のパレットに減色 → 1px の輪郭線
- 陣営色(赤)の画素を各陣営の色に差し替えたシートも書き出す
"""

import argparse
import colorsys
import os
import re

from PIL import Image

ANIM_ORDER = ["idle", "walk", "attack"]
DIR_ORDER = ["e", "se", "s", "sw", "w", "nw", "n", "ne"]
OUTLINE = (24, 18, 14, 255)
TEAMS = {"red": None, "blue": 215, "green": 120, "yellow": 48}  # 色相(度)


def load_frames(src):
    frames = {}
    for f in os.listdir(src):
        m = re.match(r"(\w+?)_(\w+?)_(\d+)\.png$", f)
        if m:
            frames[(m[1], m[2], int(m[3]))] = Image.open(os.path.join(src, f)).convert("RGBA")
    return frames


def shrink(img, scale):
    w, h = img.size
    # 透明部分の色が混ざらないよう、乗算済みアルファで平均する
    small = img.resize((w // scale, h // scale), Image.BOX, reducing_gap=None)
    px = small.load()
    for y in range(small.height):
        for x in range(small.width):
            r, g, b, a = px[x, y]
            px[x, y] = (r, g, b, 255) if a >= 110 else (0, 0, 0, 0)
    return small


def outline(img):
    out = img.copy()
    src, dst = img.load(), out.load()
    for y in range(img.height):
        for x in range(img.width):
            if src[x, y][3]:
                continue
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                nx, ny = x + dx, y + dy
                if 0 <= nx < img.width and 0 <= ny < img.height and src[nx, ny][3]:
                    dst[x, y] = OUTLINE
                    break
    return out


def is_team(r, g, b):
    h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
    return s > 0.55 and (h < 0.04 or h > 0.96)


def recolor(img, hue):
    out = img.copy()
    px = out.load()
    for y in range(out.height):
        for x in range(out.width):
            r, g, b, a = px[x, y]
            if a and is_team(r, g, b):
                _, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
                nr, ng, nb = colorsys.hsv_to_rgb(hue / 360, s, v)
                px[x, y] = (round(nr * 255), round(ng * 255), round(nb * 255), a)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("dst")
    ap.add_argument("--scale", type=int, default=4)
    ap.add_argument("--colors", type=int, default=20)
    args = ap.parse_args()

    frames = load_frames(args.src)
    dirs = [d for d in DIR_ORDER if any(k[1] == d for k in frames)]
    counts = {a: 1 + max(k[2] for k in frames if k[0] == a) for a in ANIM_ORDER}
    cols = sum(counts.values())
    fw, fh = next(iter(frames.values())).size
    fw, fh = fw // args.scale, fh // args.scale

    # 行 = 向き、列 = 待機・歩き・攻撃のコマを横に並べる
    sheet = Image.new("RGBA", (fw * cols, fh * len(dirs)), (0, 0, 0, 0))
    for row, d in enumerate(dirs):
        col = 0
        for a in ANIM_ORDER:
            for i in range(counts[a]):
                sheet.paste(shrink(frames[(a, d, i)], args.scale), (col * fw, row * fh))
                col += 1

    # シート全体で1つのパレットに減色して、コマ間の色のぶれをなくす
    alpha = sheet.getchannel("A")
    q = sheet.convert("RGB").quantize(args.colors, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
    sheet = q.convert("RGBA")
    sheet.putalpha(alpha)
    sheet = outline(sheet)

    os.makedirs(args.dst, exist_ok=True)
    for team, hue in TEAMS.items():
        img = sheet if hue is None else recolor(sheet, hue)
        img.save(os.path.join(args.dst, f"sheet_{team}.png"))
    meta = {"frame": [fw, fh], "dirs": dirs, "anims": counts}
    with open(os.path.join(args.dst, "sheet.txt"), "w") as f:
        f.write(repr(meta) + "\n")
    print("wrote", args.dst, meta)


if __name__ == "__main__":
    main()
