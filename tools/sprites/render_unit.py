"""3Dモデルをアイソメトリックの固定角度から描画し、スプライト用の連番画像を書き出す。

使い方(Blender の Python モジュール bpy が必要):
    python render_unit.py --out build/spearman_raw [--dirs 8] [--size 192]

今は仮のモデルとして、槍兵をプリミティブで組み立てている。
生成した3Dモデル(glb など)に差し替える場合は build_spearman() を読み込み処理に置き換え、
pose() でボーンや部位を動かす。
"""

import argparse
import math
import os

import bpy

# ---------------------------------------------------------------- シーン

TEAM = (0.85, 0.08, 0.08)  # 陣営色。後工程でこの赤を各陣営の色に差し替える
SKIN = (0.80, 0.58, 0.42)
CLOTH = (0.42, 0.32, 0.20)
METAL = (0.62, 0.64, 0.68)
WOOD = (0.40, 0.25, 0.12)


def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 16
    scene.cycles.use_denoising = False
    scene.render.film_transparent = True
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.view_settings.view_transform = "Standard"
    world = bpy.data.worlds.new("World")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs[0].default_value = (0.35, 0.35, 0.4, 1)
    world.node_tree.nodes["Background"].inputs[1].default_value = 0.6
    scene.world = world
    return scene


def material(name, rgb):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (*rgb, 1)
    bsdf.inputs["Roughness"].default_value = 0.9
    return m


def part(kind, name, size, loc, mat, parent, rot=(0, 0, 0)):
    """部位を1つ作る。loc は親(関節)からの相対位置。"""
    if kind == "box":
        bpy.ops.mesh.primitive_cube_add(size=1)
    elif kind == "cyl":
        bpy.ops.mesh.primitive_cylinder_add(vertices=10, radius=0.5, depth=1)
    elif kind == "cone":
        bpy.ops.mesh.primitive_cone_add(vertices=10, radius1=0.5, depth=1)
    elif kind == "ball":
        bpy.ops.mesh.primitive_uv_sphere_add(segments=12, ring_count=8, radius=0.5)
    o = bpy.context.object
    o.name = name
    o.scale = size
    o.location = loc
    o.rotation_euler = rot
    o.data.materials.append(mat)
    o.parent = parent
    return o


def joint(name, loc, parent):
    """回転の中心になる空オブジェクト。"""
    e = bpy.data.objects.new(name, None)
    bpy.context.collection.objects.link(e)
    e.location = loc
    e.parent = parent
    return e


def build_spearman():
    """正面が +X 向きの槍兵。部位を動かすための関節を dict で返す。"""
    team, skin, cloth = material("team", TEAM), material("skin", SKIN), material("cloth", CLOTH)
    metal, wood = material("metal", METAL), material("wood", WOOD)

    root = joint("root", (0, 0, 0), None)
    hips = joint("hips", (0, 0, 0.85), root)
    j = {"root": root, "hips": hips}
    for side, y in (("l", 0.13), ("r", -0.13)):
        leg = joint(f"leg_{side}", (0, y, 0), hips)
        part("box", f"thigh_{side}", (0.16, 0.15, 0.82), (0, 0, -0.41), cloth, leg)
        part("box", f"boot_{side}", (0.26, 0.16, 0.12), (0.04, 0, -0.80), wood, leg)
        j[f"leg_{side}"] = leg
    torso = joint("torso", (0, 0, 0), hips)
    part("box", "body", (0.30, 0.46, 0.62), (0, 0, 0.30), team, torso)
    part("box", "belt", (0.32, 0.48, 0.08), (0, 0, 0.02), wood, torso)
    part("ball", "head", (0.26, 0.26, 0.28), (0, 0, 0.78), skin, torso)
    part("ball", "helmet", (0.30, 0.30, 0.20), (0, 0, 0.86), metal, torso)
    part("cone", "crest", (0.06, 0.06, 0.14), (0, 0, 1.0), team, torso)
    for side, y in (("l", 0.30), ("r", -0.30)):
        arm = joint(f"arm_{side}", (0, y, 0.55), torso)
        part("box", f"sleeve_{side}", (0.13, 0.13, 0.55), (0, 0, -0.26), skin, arm)
        j[f"arm_{side}"] = arm
    # 左腕に盾
    part("cyl", "shield", (0.62, 0.62, 0.06), (0.16, 0.08, -0.35), team, j["arm_l"], rot=(math.pi / 2, 0, 0))
    part("cyl", "boss", (0.14, 0.14, 0.08), (0.16, 0.13, -0.35), metal, j["arm_l"], rot=(math.pi / 2, 0, 0))
    # 右手に槍(手の位置を中心に回転する)
    spear = joint("spear", (0, 0, -0.5), j["arm_r"])
    part("cyl", "shaft", (0.05, 0.05, 2.3), (0, 0, 0.35), wood, spear)
    part("cone", "tip", (0.09, 0.09, 0.26), (0, 0, 1.6), metal, spear)
    j["spear"] = spear
    j["torso"] = torso
    return j


# ---------------------------------------------------------------- 動き

def pose(j, anim, t):
    """anim のフレーム t(0..1)の姿勢をとる。"""
    rx = lambda o, a: setattr(o, "rotation_euler", (0, a, 0))  # +X 正面での前後の振り
    for k in ("leg_l", "leg_r", "arm_l", "arm_r", "torso"):
        rx(j[k], 0)
    j["hips"].location.z = 0.85
    j["spear"].rotation_euler = (0, math.radians(110), 0)  # 槍を前に倒して構える(腕の角度と合わせて水平より少し上)
    j["spear"].location = (0, 0, -0.5)
    s = math.sin(t * 2 * math.pi)
    if anim == "idle":
        j["hips"].location.z = 0.85 - 0.015 * (1 - math.cos(t * 2 * math.pi))
        rx(j["arm_r"], math.radians(-35))
    elif anim == "walk":
        rx(j["leg_l"], 0.5 * s)
        rx(j["leg_r"], -0.5 * s)
        rx(j["arm_r"], math.radians(-35) + 0.15 * s)
        rx(j["arm_l"], -0.25 * s)
        j["hips"].location.z = 0.85 - 0.04 * abs(math.cos(t * 2 * math.pi))
    elif anim == "attack":
        # 引いて溜め → 突く → 戻す
        k = (-0.6, 0.2, 1.0, 0.3)[min(3, int(t * 4))]
        rx(j["arm_r"], math.radians(-35) - 0.35 * k)
        j["spear"].location = (0, 0, -0.5)
        rx(j["torso"], -0.12 * k)
        rx(j["leg_l"], -0.35)
        rx(j["leg_r"], 0.25)
    bpy.context.view_layer.update()


ANIMS = {"idle": 2, "walk": 4, "attack": 4}

# ---------------------------------------------------------------- カメラと出力


def setup_camera(scene, size, ortho_scale):
    """仰角30度の正投影。床のタイルが 2:1 の菱形に見える。"""
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    scene.collection.objects.link(cam)
    cam.data.type = "ORTHO"
    cam.data.ortho_scale = ortho_scale
    elev = math.radians(30)
    d = 20
    cam.location = (0, -d * math.cos(elev), d * math.sin(elev) + 0.9)
    cam.rotation_euler = (math.pi / 2 - elev, 0, 0)
    scene.camera = cam
    scene.render.resolution_x = scene.render.resolution_y = size

    sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
    scene.collection.objects.link(sun)
    sun.data.energy = 3.5
    sun.rotation_euler = (math.radians(45), 0, math.radians(-30))  # 左上から光


# 画面上の向き名 → ワールドでの向き(度)。カメラは -Y 側から見ている。
DIR_NAMES = ["e", "se", "s", "sw", "w", "nw", "n", "ne"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--dirs", type=int, choices=(4, 8), default=8)
    ap.add_argument("--size", type=int, default=192)
    args = ap.parse_args()

    scene = reset_scene()
    j = build_spearman()
    setup_camera(scene, args.size, ortho_scale=3.4)
    os.makedirs(args.out, exist_ok=True)

    dirs = DIR_NAMES if args.dirs == 8 else DIR_NAMES[::2]
    for name in dirs:
        angle = -45 * DIR_NAMES.index(name)
        j["root"].rotation_euler = (0, 0, math.radians(angle))
        for anim, n in ANIMS.items():
            for f in range(n):
                pose(j, anim, f / n)
                scene.render.filepath = os.path.join(args.out, f"{anim}_{name}_{f}.png")
                bpy.ops.render.render(write_still=True)
    print("done", args.out)


if __name__ == "__main__":
    main()
