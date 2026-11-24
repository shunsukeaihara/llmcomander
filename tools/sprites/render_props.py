"""建物・木・資源の3Dモデルを、地図と同じ見下ろし角度から1方向だけ描画する。

    python render_props.py --out build/props_raw

ユニット(render_unit.py)と同じ縮尺で描く。ユニットは 48px の枠に 3.4m が入るので 1m ≈ 14.1px、
地図のタイル1枚(幅64px)は約3.2m四方にあたる。
建物は四角い敷地を菱形のタイルに合わせるため、カメラを45度回した方向から写す。

書き出すもの: <名前>.png(4倍の大きさ)と props.json(各スプライトの最終的な大きさと、地面の基準点の位置)。
"""

import argparse
import json
import math
import os
import random

import bpy

from render_unit import CLOTH, METAL, TEAM, WOOD, material, part, reset_scene

PX_PER_M = 48 / 3.4      # 最終的な 1m あたりの画素数(ユニットと同じ)
SCALE = 4                # 4倍で描いて pixelize で縮める
ELEV = math.radians(30)
AZIMUTH = math.radians(45)  # カメラはタイルの対角線の方向から見る
TILE_M = 64 / (math.sqrt(2) * PX_PER_M)  # タイル1枚の一辺(m)

STONE = (0.62, 0.58, 0.52)
PLASTER = (0.82, 0.76, 0.62)
DARK_WOOD = (0.30, 0.19, 0.10)
LEAF = [(0.09, 0.26, 0.07), (0.13, 0.33, 0.08), (0.07, 0.22, 0.08)]
PINE = [(0.05, 0.19, 0.09), (0.08, 0.25, 0.11)]
GOLD = (0.95, 0.75, 0.18)
ROCK = (0.50, 0.48, 0.45)
BERRY = (0.75, 0.10, 0.20)  # 陣営色の赤と区別できるよう、少し紫寄り


def camera(scene, w, h, zc):
    """最終的な大きさ w×h(px)。注視点の高さ zc(m)。地面の原点が画像のどこに来るかを返す。"""
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    scene.collection.objects.link(cam)
    cam.data.type = "ORTHO"
    cam.data.ortho_scale = max(w, h) / PX_PER_M
    d = 40
    cam.location = (d * math.cos(ELEV) * math.cos(AZIMUTH), d * math.cos(ELEV) * math.sin(AZIMUTH), d * math.sin(ELEV) + zc)
    cam.rotation_euler = (math.pi / 2 - ELEV, 0, AZIMUTH + math.pi / 2)
    scene.camera = cam
    scene.render.resolution_x = w * SCALE
    scene.render.resolution_y = h * SCALE

    sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
    scene.collection.objects.link(sun)
    # ユニットより光を弱める(緑や壁の色が白く飛ばないように)
    sun.data.energy = 2.2
    scene.world.node_tree.nodes["Background"].inputs[1].default_value = 0.35
    # ユニットと同じく画面の左上から光が当たるよう、カメラと同じだけ回す
    sun.rotation_euler = (math.radians(45), 0, math.radians(-30) + AZIMUTH + math.pi / 2)
    # 地面の原点は注視点より zc*cos(仰角) だけ下に写る
    return w / 2, h / 2 + zc * math.cos(ELEV) * PX_PER_M


def ico(name, radius, loc, mat, parent=None, squash=1.0, subdiv=1):
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=subdiv, radius=radius, location=loc)
    o = bpy.context.object
    o.name = name
    o.scale = (1, 1, squash)
    o.data.materials.append(mat)
    o.parent = parent
    return o


def prism(name, size, loc, mat, rot_z=0.0):
    """切妻屋根: 三角柱。size = (長さ, 幅, 高さ)。"""
    bpy.ops.mesh.primitive_cylinder_add(vertices=3, radius=1, depth=1)
    o = bpy.context.object
    o.name = name
    # 三角形の頂点を上に向け、軸を水平にする
    o.rotation_euler = (0, math.pi / 2, rot_z)
    o.scale = (size[2] / 1.5, size[1] / math.sqrt(3), size[0])
    o.location = loc
    o.data.materials.append(mat)
    return o


def pyramid(name, base, height, loc, mat):
    bpy.ops.mesh.primitive_cone_add(vertices=4, radius1=base / math.sqrt(2), depth=height, location=loc)
    o = bpy.context.object
    o.name = name
    o.rotation_euler = (0, 0, math.pi / 4)
    o.data.materials.append(mat)
    return o


# ---------------------------------------------------------------- 小物

def tree_broad(seed):
    rng = random.Random(seed)
    trunk = material("trunk", DARK_WOOD)
    leaves = [material(f"leaf{i}", c) for i, c in enumerate(LEAF)]
    h = 1.5 + rng.random() * 0.6
    part("cyl", "trunk", (0.55, 0.55, h + 1), (0, 0, (h + 1) / 2), trunk, None)
    for i in range(5):
        a = rng.random() * math.tau
        r = 0.9 * rng.random()
        ico(f"crown{i}", 1.6 + rng.random() * 0.6, (math.cos(a) * r, math.sin(a) * r, h + 1.2 + i * 0.45),
            leaves[rng.randrange(len(leaves))], squash=0.8)


def tree_pine(seed):
    rng = random.Random(seed)
    trunk = material("trunk", DARK_WOOD)
    greens = [material(f"pine{i}", c) for i, c in enumerate(PINE)]
    h = 1.2 + rng.random() * 0.5
    part("cyl", "trunk", (0.45, 0.45, h + 1), (0, 0, (h + 1) / 2), trunk, None)
    scale = 0.9 + rng.random() * 0.3
    for i, (r, z) in enumerate([(2.3, 0), (1.8, 1.4), (1.2, 2.6)]):
        part("cone", f"tier{i}", (r * 2 * scale, r * 2 * scale, 2.2 * scale), (0, 0, h + 1 + z * scale), greens[i % 2], None)


def gold_mine():
    rock, gold = material("rock", ROCK), material("gold", GOLD)
    rng = random.Random(3)
    for i in range(6):
        a = i / 6 * math.tau + rng.random() * 0.5
        r = 0.9 + rng.random() * 0.5
        ico(f"rock{i}", 0.7 + rng.random() * 0.4, (math.cos(a) * r, math.sin(a) * r, 0.3), rock, squash=0.7)
    for i in range(5):
        a = rng.random() * math.tau
        r = rng.random() * 0.9
        ico(f"nugget{i}", 0.45 + rng.random() * 0.25, (math.cos(a) * r, math.sin(a) * r, 0.5 + rng.random() * 0.3), gold, squash=0.8)


def berry_bush():
    leaf, berry = material("bush", LEAF[0]), material("berry", BERRY)
    ico("bush", 0.9, (0, 0, 0.7), leaf, squash=0.8)
    ico("bush2", 0.7, (0.5, 0.3, 0.6), leaf, squash=0.8)
    rng = random.Random(5)
    for i in range(10):
        a = rng.random() * math.tau
        z = 0.4 + rng.random() * 0.8
        ico(f"b{i}", 0.13, (math.cos(a) * 0.95, math.sin(a) * 0.95, z), berry, subdiv=1)


# ---------------------------------------------------------------- 建物(敷地は3×3タイル)

def town_center():
    stone, plaster, wood, team = material("stone", STONE), material("plaster", PLASTER), material("wood", WOOD), material("team", TEAM)
    dark = material("dark", DARK_WOOD)
    s = TILE_M * 3 * 0.86  # 敷地より少し小さく
    part("box", "base", (s, s, 1.2), (0, 0, 0.6), stone, None)
    part("box", "hall", (s * 0.72, s * 0.72, 2.6), (0, 0, 2.5), plaster, None)
    for dx in (-1, 1):
        for dy in (-1, 1):  # 柱
            part("box", f"post{dx}{dy}", (0.35, 0.35, 2.6), (dx * s * 0.36, dy * s * 0.36, 2.5), wood, None)
    pyramid("roof", s * 0.95, 3.0, (0, 0, 5.3), team)
    part("box", "door", (0.1, 1.2, 1.6), (s * 0.365, 0, 2.0), dark, None)
    part("box", "door2", (1.2, 0.1, 1.6), (0, s * 0.365, 2.0), dark, None)
    part("cyl", "pole", (0.12, 0.12, 3.0), (0, 0, 7.4), wood, None)
    part("box", "flag", (0.05, 1.1, 0.7), (0, 0.55, 8.4), team, None)


def barracks():
    stone, plaster, wood, team = material("stone", STONE), material("plaster", PLASTER), material("wood", WOOD), material("team", TEAM)
    metal = material("metal", METAL)
    s = TILE_M * 3 * 0.86
    part("box", "base", (s, s * 0.62, 0.6), (0, -s * 0.12, 0.3), stone, None)
    part("box", "hall", (s * 0.9, s * 0.5, 2.4), (0, -s * 0.12, 1.8), plaster, None)
    prism("roof", (s * 0.98, s * 0.6, 1.8), (0, -s * 0.12, 3.6), team)
    part("box", "beam", (s * 0.92, 0.25, 0.25), (0, -s * 0.12 + s * 0.25, 2.9), wood, None)
    # 前庭: 柵と武器立て
    for i in range(7):
        x = -s * 0.45 + i * s * 0.15
        part("box", f"fence{i}", (0.15, 0.15, 1.0), (x, s * 0.42, 0.5), wood, None)
    part("box", "rail", (s * 0.92, 0.1, 0.12), (0, s * 0.42, 0.8), wood, None)
    part("box", "rack", (1.6, 0.3, 0.15), (s * 0.15, s * 0.25, 1.2), wood, None)
    for i in range(4):
        part("cyl", f"spear{i}", (0.06, 0.06, 2.2), (s * 0.15 - 0.6 + i * 0.4, s * 0.25, 1.1), wood, None)
        part("cone", f"tip{i}", (0.12, 0.12, 0.3), (s * 0.15 - 0.6 + i * 0.4, s * 0.25, 2.3), metal, None)
    part("cyl", "pole", (0.12, 0.12, 3.5), (-s * 0.35, s * 0.3, 1.75), wood, None)
    part("box", "banner", (0.05, 0.8, 1.2), (-s * 0.35, s * 0.3 + 0.42, 2.8), team, None)


def storehouse():
    """採集所(敷地は2×2タイル): 柱と片流れの屋根だけの小屋に、木箱・樽・丸太・袋・金の山。"""
    stone, wood, team = material("stone", STONE), material("wood", WOOD), material("team", TEAM)
    dark, gold, cloth = material("dark", DARK_WOOD), material("gold", GOLD), material("cloth", CLOTH)
    s = TILE_M * 2 * 0.86
    part("box", "floor", (s, s, 0.3), (0, 0, 0.15), stone, None)
    h = 2.4
    for dx in (-1, 1):
        for dy in (-1, 1):
            part("box", f"post{dx}{dy}", (0.25, 0.25, h), (dx * s * 0.42, dy * s * 0.42, 0.3 + h / 2), wood, None)
    prism("roof", (s * 0.98, s * 0.98, 0.9), (0, 0, 0.3 + h + 0.45), team)
    # 奥の壁(板張り)
    part("box", "wall", (0.15, s * 0.84, h * 0.8), (-s * 0.42, 0, 0.3 + h * 0.4), dark, None)
    # 木箱と樽
    for i, (x, y) in enumerate([(-0.25, -0.3), (-0.25, 0.05), (-0.05, -0.3)]):
        part("box", f"crate{i}", (0.9, 0.9, 0.9), (x * s, y * s, 0.75), wood, None)
    part("box", "crate_top", (0.8, 0.8, 0.8), (-0.25 * s, -0.3 * s, 1.6), wood, None)
    for i, (x, y) in enumerate([(0.2, -0.32), (0.32, -0.12)]):
        part("cyl", f"barrel{i}", (0.4, 0.4, 0.9), (x * s, y * s, 0.75), dark, None)
    # 丸太の山(手前右)
    for i, (dy, z) in enumerate([(0.0, 0.41), (0.44, 0.41), (0.22, 0.8)]):
        part("cyl", f"log{i}", (0.42, 0.42, 1.8), (s * 0.22, s * 0.14 + dy, z), wood, None, rot=(0, math.pi / 2, 0))
    # 袋と金の小山(手前左)
    for i in range(3):
        ico(f"sack{i}", 0.35, (-s * 0.05 + i * 0.45, s * 0.3, 0.55), cloth, squash=0.75)
    ico("goldpile", 0.45, (s * 0.32, -s * 0.38 + 0.9, 0.45), gold, squash=0.6)


# 名前: (作る関数, 最終の幅, 高さ, 注視点の高さ m)
PROPS = {
    **{f"tree_broad_{i}": (lambda i=i: tree_broad(i), 96, 128, 3.3) for i in range(3)},
    **{f"tree_pine_{i}": (lambda i=i: tree_pine(i + 10), 96, 128, 3.3) for i in range(3)},
    "gold": (gold_mine, 80, 64, 1.0),
    "berry": (berry_bush, 48, 48, 0.9),
    "town_center": (town_center, 224, 224, 4.6),
    "barracks": (barracks, 224, 224, 4.6),
    "storehouse": (storehouse, 160, 144, 2.0),
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--only", nargs="*")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)
    meta = {}
    for name, (build, w, h, zc) in PROPS.items():
        if args.only and name not in args.only:
            continue
        scene = reset_scene()
        build()
        ax, ay = camera(scene, w, h, zc)
        scene.render.filepath = os.path.join(args.out, f"{name}.png")
        bpy.ops.render.render(write_still=True)
        meta[name] = {"w": w, "h": h, "ax": round(ax, 1), "ay": round(ay, 1)}
    with open(os.path.join(args.out, "props.json"), "w") as f:
        json.dump(meta, f, indent=1)
    print("done", args.out, len(meta))


if __name__ == "__main__":
    main()
