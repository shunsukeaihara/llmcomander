"""render_props.py の画像をドット絵にして、ゲームの素材フォルダに書き出す。

    python pixelize_props.py build/props_raw ../../game/assets/props

- 縮小 → 透明度の二値化 → 減色 → 1px の輪郭線(ユニットと同じ処理)
- 建物は陣営色(赤)を差し替えて、陣営ごとの画像も作る(<名前>_blue.png / _red.png)
- props.json(大きさと、地面の基準点)もそのまま写す
"""

import argparse
import json
import os

from PIL import Image

from pixelize import outline, recolor, shrink

TEAM_FILES = {"red": None, "blue": 215}
BUILDINGS = {"town_center", "barracks"}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("dst")
    ap.add_argument("--scale", type=int, default=4)
    ap.add_argument("--colors", type=int, default=24)
    args = ap.parse_args()
    os.makedirs(args.dst, exist_ok=True)
    with open(os.path.join(args.src, "props.json")) as f:
        meta = json.load(f)

    for name in meta:
        img = shrink(Image.open(os.path.join(args.src, f"{name}.png")).convert("RGBA"), args.scale)
        alpha = img.getchannel("A")
        q = img.convert("RGB").quantize(args.colors, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE).convert("RGBA")
        q.putalpha(alpha)
        q = outline(q)
        if name in BUILDINGS:
            for team, hue in TEAM_FILES.items():
                (q if hue is None else recolor(q, hue)).save(os.path.join(args.dst, f"{name}_{team}.png"))
        else:
            q.save(os.path.join(args.dst, f"{name}.png"))
    with open(os.path.join(args.dst, "props.json"), "w") as f:
        json.dump(meta, f, indent=1)
    print("wrote", args.dst, list(meta))


if __name__ == "__main__":
    main()
