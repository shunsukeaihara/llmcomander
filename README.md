# LLM Commander

声で指揮するRTSのPoC。発話をLLMが解釈して「作戦」として登録し、作戦ごとの担当LLMが並列で戦況を見ます。ユニットはLLMを待たず、設定された交戦規則で動き続けます。

- コンセプト: [docs/concept.md](docs/concept.md)
- 画面(デモ場面): [docs/screenshot-poc.png](docs/screenshot-poc.png)

## 動かし方

必要なもの: [Godot 4.7(.NET版)](https://godotengine.org/download)、.NET SDK 8 以上、マイク(音声を使う場合)

1. `game/.env.example` を `game/.env` にコピーして、`ANTHROPIC_API_KEY`(必須)と `OPENAI_API_KEY`(音声認識用)を書く。
2. Godot で `game/project.godot` を開き、ビルドして実行する(F5)。

キーがなくてもゲーム自体は動きます(指示は受け付けません)。

### 操作

| 操作 | 内容 |
|---|---|
| Space 長押し | 話す。離すと送信。話しながらクリックした地点は「指示点1, 2…」としてLLMに渡る |
| Enter | 文字で指示する(音声の代わり) |
| 左クリック / ドラッグ | ユニットを選択(「こいつら」「選択中の」で参照できる) |
| 1 → クリック | 地点の目印を置く(地点A, B…) |
| 2 → ドラッグ | 線の目印を引く(線A, B…)。「槍は線Aで受け止めて」のように使う |
| 右クリック | 目印を消す |
| WASD / 矢印 / 中ドラッグ / ホイール | スクロールとズーム |
| Tab | 地図上の意図表示の切り替え |
| C | 右の作戦パネルを折りたたむ・開く |

建設の提案は作戦カードの「OK」ボタンか、「OK」と言うと確定します。

### 指示の例

- 「木に8人、金に3人。村人は作り続けて」
- 「兵舎を基地の近くに」→「OK」
- 「槍と弓を北の丘に。弓は後ろから軍事ユニット優先」
- 「槍は線Aで受け止めて、騎兵は右で待機、槍が接触したら回り込め」
- 「弓は引き撃ち、他は撤退」

## LLMの応答時間を測る

ゲーム画面なしで、交戦中の場面に用意した発話を流し、司令LLMと担当LLMの応答時間と出力を表示します。

```sh
cd tools/bench
dotnet run                        # 用意した発話を順に流す
dotnet run -- "騎兵で敵の弓を狙え"   # 任意の発話
```

`game/.env` のキーとモデル設定を使います。`COMMANDER_MODEL=claude-sonnet-5-5` などに変えて比べられます。

## 構成

| 場所 | 内容 |
|---|---|
| `game/src/Sim` | シミュレーション(マップ、経路探索、経済、戦闘、作戦と交戦規則、敵AI)。Godot に依存しない |
| `game/src/Command` | LLM に渡す状況の要約、ツール定義、ツール呼び出しの適用、非同期の実行管理 |
| `game/src/Llm` | Claude と OpenAI 音声認識の呼び出し |
| `game/src/View` | 描画、UI、入力、マイク録音 |
| `tests/` | シミュレーションと指揮層のテスト(`dotnet test tests/LlmCommander.Tests`) |
| `tools/sprites` | Blender でユニットのドット絵を作る工程 |
| `tools/bench` | LLM の応答時間の計測 |

画面確認用に、`LLMC_DEMO=1` で交戦中の場面から始まり、`LLMC_SCREENSHOT=out.png` で画面を保存して終了します。
