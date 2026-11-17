"""3Dモデルをアイソメトリックの固定角度から描画し、スプライト用の連番画像を書き出す。

使い方(Blender の Python モジュール bpy が必要):
    python render_unit.py --unit spearman --out build/spearman_raw [--dirs 8] [--size 192]

ユニットはプリミティブを組み合わせたローポリのモデル(--unit で兵種を選ぶ)。
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


def mats():
    return {
        "team": material("team", TEAM), "skin": material("skin", SKIN), "cloth": material("cloth", CLOTH),
        "metal": material("metal", METAL), "wood": material("wood", WOOD),
        "straw": material("straw", (0.78, 0.66, 0.38)), "horse": material("horse", (0.45, 0.30, 0.18)),
        "mane": material("mane", (0.18, 0.12, 0.08)),
    }


def build_humanoid(m, unit, hip_z=0.85, parent=None):
    """正面が +X 向きの人型。部位を動かすための関節を dict で返す。"""
    root = joint("root", (0, 0, 0), parent)
    hips = joint("hips", (0, 0, hip_z), root)
    j = {"root": root, "hips": hips, "unit": unit}
    for side, y in (("l", 0.13), ("r", -0.13)):
        leg = joint(f"leg_{side}", (0, y, 0), hips)
        part("box", f"thigh_{side}", (0.16, 0.15, 0.82), (0, 0, -0.41), m["cloth"], leg)
        part("box", f"boot_{side}", (0.26, 0.16, 0.12), (0.04, 0, -0.80), m["wood"], leg)
        j[f"leg_{side}"] = leg
    torso = joint("torso", (0, 0, 0), hips)
    j["torso"] = torso
    body = m["cloth"] if unit == "villager" else m["team"]
    part("box", "body", (0.30, 0.46, 0.62), (0, 0, 0.30), body, torso)
    part("box", "belt", (0.32, 0.48, 0.08), (0, 0, 0.02), m["wood"], torso)
    part("ball", "head", (0.28, 0.28, 0.30), (0, 0, 0.78), m["skin"], torso)
    if unit == "spearman":
        part("ball", "helmet", (0.32, 0.32, 0.22), (0, 0, 0.87), m["metal"], torso)
        part("cone", "crest", (0.07, 0.07, 0.16), (0, 0, 1.02), m["team"], torso)
    elif unit == "archer":
        part("cone", "hood", (0.36, 0.36, 0.34), (0, 0, 0.92), m["team"], torso)
        part("cyl", "quiver", (0.14, 0.14, 0.55), (-0.2, 0.1, 0.35), m["wood"], torso, rot=(0.3, 0, 0))
    elif unit == "villager":
        part("box", "apron", (0.32, 0.47, 0.26), (0.01, 0, 0.45), m["team"], torso)  # 陣営色はエプロンに
        part("cyl", "brim", (0.56, 0.56, 0.04), (0, 0, 0.9), m["straw"], torso)
        part("cone", "hat", (0.32, 0.32, 0.18), (0, 0, 0.99), m["straw"], torso)
    else:  # cavalry の乗り手
        part("ball", "helmet", (0.32, 0.32, 0.22), (0, 0, 0.87), m["metal"], torso)
        part("box", "plume", (0.2, 0.06, 0.12), (-0.08, 0, 1.02), m["team"], torso)
    for side, y in (("l", 0.30), ("r", -0.30)):
        arm = joint(f"arm_{side}", (0, y, 0.55), torso)
        part("box", f"sleeve_{side}", (0.14, 0.14, 0.55), (0, 0, -0.26), m["skin"], arm)
        j[f"arm_{side}"] = arm
    # 右手の武器(手の位置を中心に回転する)
    w = joint("weapon", (0, 0, -0.5), j["arm_r"])
    j["weapon"] = w
    if unit == "spearman":
        part("cyl", "shaft", (0.07, 0.07, 2.3), (0, 0, 0.35), m["wood"], w)
        part("cone", "tip", (0.12, 0.12, 0.3), (0, 0, 1.62), m["metal"], w)
        part("cyl", "shield", (0.64, 0.64, 0.06), (0.16, 0.08, -0.35), m["team"], j["arm_l"], rot=(math.pi / 2, 0, 0))
        part("cyl", "boss", (0.14, 0.14, 0.08), (0.16, 0.13, -0.35), m["metal"], j["arm_l"], rot=(math.pi / 2, 0, 0))
    elif unit == "villager":
        part("cyl", "handle", (0.06, 0.06, 0.75), (0, 0, 0.3), m["wood"], w)
        part("box", "axe", (0.26, 0.06, 0.16), (0.1, 0, 0.62), m["metal"], w)
    elif unit == "cavalry":
        part("box", "blade", (0.05, 0.1, 0.9), (0, 0, 0.5), m["metal"], w)
        part("box", "guard", (0.06, 0.26, 0.05), (0, 0, 0.06), m["wood"], w)
        part("cyl", "shield", (0.5, 0.5, 0.06), (0.1, 0.08, -0.3), m["team"], j["arm_l"], rot=(math.pi / 2, 0, 0))
    else:  # archer: 左手に弓(弧を短い棒の連なりで作る)
        bow = joint("bow", (0.05, 0.05, -0.5), j["arm_l"])
        j["bow"] = bow
        for i in range(5):
            a = (i - 2) * 0.32
            part("box", f"bow{i}", (0.05, 0.05, 0.26), (0.1 * math.cos(a) - 0.1, 0, 0.5 * math.sin(a)), m["wood"], bow,
                 rot=(0, a, 0))
    return j


def build_horse(m):
    """正面が +X 向きの馬。乗り手の腰の位置に joint を返す。"""
    root = joint("root", (0, 0, 0), None)
    body = joint("horse_body", (0, 0, 1.0), root)
    part("box", "barrel", (1.3, 0.48, 0.5), (0, 0, 0), m["horse"], body)
    part("box", "blanket", (0.6, 0.52, 0.3), (-0.05, 0, 0.14), m["team"], body)
    neck = joint("neck", (0.55, 0, 0.15), body)
    part("box", "neckbox", (0.28, 0.26, 0.62), (0.08, 0, 0.25), m["horse"], neck, rot=(0, 0.6, 0))
    part("box", "headbox", (0.46, 0.22, 0.22), (0.36, 0, 0.5), m["horse"], neck, rot=(0, 0.35, 0))
    part("box", "mane", (0.1, 0.1, 0.5), (-0.04, 0, 0.32), m["mane"], neck, rot=(0, 0.6, 0))
    part("box", "tail", (0.1, 0.1, 0.5), (-0.72, 0, -0.1), m["mane"], body, rot=(0, -0.5, 0))
    j = {"horse_root": root, "horse_body": body, "neck": neck}
    for name, x, y in (("fl", 0.48, 0.16), ("fr", 0.48, -0.16), ("bl", -0.48, 0.16), ("br", -0.48, -0.16)):
        leg = joint(f"hleg_{name}", (x, y, -0.2), body)
        part("box", f"hlegbox_{name}", (0.14, 0.13, 0.82), (0, 0, -0.4), m["horse"], leg)
        part("box", f"hoof_{name}", (0.16, 0.15, 0.08), (0, 0, -0.8), m["mane"], leg)
        j[f"hleg_{name}"] = leg
    return j


def build_unit(unit):
    m = mats()
    if unit != "cavalry":
        return build_humanoid(m, unit)
    h = build_horse(m)
    rider = build_humanoid(m, unit, hip_z=0.35, parent=h["horse_body"])
    h.update(rider)
    h["root"] = h["horse_root"]
    # 乗り手の脚は馬の胴を挟むように開く
    h["leg_l"].rotation_euler = (-0.5, 0, 0)
    h["leg_r"].rotation_euler = (0.5, 0, 0)
    return h


# ---------------------------------------------------------------- 動き

def pose(j, anim, t):
    """anim のフレーム t(0..1)の姿勢をとる。"""
    unit = j["unit"]
    ry = lambda o, a: setattr(o, "rotation_euler", (o.rotation_euler[0], a, 0))  # +X 正面での前後の振り
    for k in ("leg_l", "leg_r", "arm_l", "arm_r", "torso"):
        ry(j[k], 0)
    mounted = unit == "cavalry"
    base_z = 0.35 if mounted else 0.85
    j["hips"].location.z = base_z
    s = math.sin(t * 2 * math.pi)
    c = math.cos(t * 2 * math.pi)

    # 武器の構え
    if unit == "spearman":
        j["weapon"].rotation_euler = (0, math.radians(110), 0)  # 腕の角度と合わせて水平より少し上
        hold = math.radians(-35)
    elif unit == "cavalry":
        j["weapon"].rotation_euler = (0, math.radians(60), 0)
        hold = math.radians(-30)
    elif unit == "villager":
        j["weapon"].rotation_euler = (0, math.radians(80), 0)
        hold = math.radians(-20)
    else:
        hold = math.radians(-10)
        ry(j["arm_l"], math.radians(-20))

    if mounted:
        for name in ("fl", "fr", "bl", "br"):
            ry(j[f"hleg_{name}"], 0)
        j["horse_body"].location.z = 1.0
        ry(j["neck"], 0)

    if anim == "idle":
        j["hips"].location.z = base_z - 0.015 * (1 - c)
        ry(j["arm_r"], hold)
        if mounted:
            ry(j["neck"], 0.08 * s)
    elif anim == "walk":
        ry(j["arm_r"], hold + 0.15 * s)
        if mounted:
            # 駆け足: 前脚と後ろ脚をずらして振る
            for name, ph in (("fl", 0), ("fr", 0.25), ("bl", 0.5), ("br", 0.75)):
                ry(j[f"hleg_{name}"], 0.55 * math.sin((t + ph) * 2 * math.pi))
            j["horse_body"].location.z = 1.0 + 0.06 * abs(c)
            ry(j["neck"], 0.12 * s)
        else:
            ry(j["leg_l"], 0.5 * s)
            ry(j["leg_r"], -0.5 * s)
            if unit != "archer":
                ry(j["arm_l"], -0.25 * s)
            j["hips"].location.z = base_z - 0.04 * abs(c)
    elif anim == "attack":
        k = (-0.6, 0.2, 1.0, 0.3)[min(3, int(t * 4))]
        if unit == "archer":
            # 弓を前に構えて引き、放つ
            ry(j["arm_l"], math.radians(-80))
            ry(j["arm_r"], math.radians(-80) + 0.5 * max(0, -k + 0.3))
        elif unit in ("villager", "cavalry"):
            # 振りかぶって振り下ろす
            ry(j["arm_r"], hold - 1.3 * (1 - k) + 0.6)
            ry(j["torso"], -0.1 * k)
        else:
            ry(j["arm_r"], hold - 0.35 * k)
            ry(j["torso"], -0.12 * k)
        if not mounted and unit != "villager":
            ry(j["leg_l"], -0.35)
            ry(j["leg_r"], 0.25)
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
    ap.add_argument("--unit", choices=("spearman", "archer", "villager", "cavalry"), default="spearman")
    ap.add_argument("--dirs", type=int, choices=(4, 8), default=8)
    ap.add_argument("--size", type=int, default=192)
    args = ap.parse_args()

    scene = reset_scene()
    j = build_unit(args.unit)
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
