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

## 現状

- モデルは仮のもので、プリミティブを組み合わせた槍兵。
- 今後、生成した3Dモデル(glb/fbx、リグとアニメーション付き)を読み込めるように差し替える。
