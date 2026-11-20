# スプライト生成ツール

3Dモデルをアイソメトリックの固定角度(仰角30度の正投影)から描画し、ドット絵のスプライトシートに変換する。
3Dモデルを経由するので、向きやコマが変わってもユニットの見た目が揃う。

## 必要なもの

```sh
python3 -m venv .venv && .venv/bin/pip install bpy pillow
```

GPUは不要。CPUだけで、8方向×10コマの80枚を数秒で描画できる。

## 使い方

```sh
.venv/bin/python render_unit.py --out build/spearman_raw --dirs 8   # 192px の連番画像を書き出す
.venv/bin/python pixelize.py build/spearman_raw build/spearman       # 48px のシートに変換する
```

- 出力は `sheet_<陣営>.png`。行が向き(e, se, s, sw, w, nw, n, ne)、列が待機2・歩き4・攻撃4コマ。
- `pixelize.py` は次の順に処理する。
  1. 縮小する。
  2. シート全体で共通のパレットに減色する。
  3. 1pxの輪郭線を付ける。
  4. 陣営色(赤)を差し替えて、陣営ごとのシートを作る。

## 建物・木・資源

```sh
.venv/bin/python render_props.py --out build/props_raw                          # 1方向だけ、4倍で描く
.venv/bin/python pixelize_props.py build/props_raw ../../game/assets/props      # ドット絵にしてゲームの素材へ
```

- ユニットと同じ縮尺(1m ≈ 14.1px、タイル1枚 ≈ 3.2m 四方)で、カメラを45度回して菱形のタイルに合わせる。
- 木6種(広葉樹・針葉樹)、金鉱、木の実の茂み、村の中心、兵舎。建物は陣営色を差し替えた `_blue` / `_red` を作る。
- `props.json` に各画像の大きさと「地面の基準点」(地図上の位置に合わせる点)を書き出す。

## 現状

- モデルは仮のもので、プリミティブを組み合わせた槍兵。
- 今後、生成した3Dモデル(glb/fbx、リグとアニメーション付き)を読み込めるように差し替える。
