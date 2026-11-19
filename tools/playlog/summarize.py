#!/usr/bin/env python3
"""プレイログ(JSONL)の要約。

    python summarize.py ログ.jsonl              # 応答時間の統計、発話と結果の一覧、出来事、戦況の推移
    python summarize.py ログ.jsonl --cmd 3      # 3番目の発話について、LLMに渡した状況と返ってきた命令を全部表示
    python summarize.py ログ.jsonl --officer    # 担当LLMの報告と調整の一覧も表示

標準ライブラリだけで動く。
"""

import argparse
import json
import statistics
import sys


def clock(t):
    t = int(t or 0)
    return f"{t // 60}:{t % 60:02d}"


def stats(values):
    values = [v for v in values if v is not None]
    if not values:
        return "-"
    values = sorted(values)
    p90 = values[min(len(values) - 1, int(round(0.9 * (len(values) - 1))))]
    return (f"平均 {statistics.mean(values):.2f}秒 / 中央値 {statistics.median(values):.2f}秒 / "
            f"90% {p90:.2f}秒 / 最大 {values[-1]:.2f}秒 ({len(values)}件)")


def counts(d):
    return " ".join(f"{k}{v}" for k, v in (d or {}).items()) or "なし"


def load(path):
    rows = []
    with open(path, encoding="utf-8") as f:
        for i, line in enumerate(f, 1):
            line = line.strip()
            if not line:
                continue
            try:
                rows.append(json.loads(line))
            except json.JSONDecodeError:
                print(f"(壊れた行を飛ばしました: {i}行目)", file=sys.stderr)
    return rows


def show_command(c, n):
    print(f"\n===== 発話 {n}: 「{c.get('utterance')}」 ({clock(c.get('submitted_t'))}) =====")
    print(f"モデル: {c.get('model')} (effort {c.get('effort')})  "
          f"認識 {c.get('stt_seconds', 0):.2f}秒 / 解釈 {c.get('llm_seconds', 0):.2f}秒 / 反映まで {c.get('applied_seconds', 0):.2f}秒")
    if c.get("error"):
        print("エラー:", c["error"])
    print("\n--- LLMに渡した状況 ---")
    print(c.get("prompt", ""))
    print("--- 返ってきた命令 ---")
    for call in c.get("calls", []):
        print(f"{call['name']} {json.dumps(call['input'], ensure_ascii=False, indent=2)}")
        print(f"  → {call.get('result')}")
    if c.get("text"):
        print("文章の返答:", c["text"])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("log")
    ap.add_argument("--cmd", type=int, help="この番号の発話の詳細を表示する(1始まり)")
    ap.add_argument("--officer", action="store_true", help="担当LLMの報告と調整も一覧にする")
    args = ap.parse_args()

    rows = load(args.log)
    by = lambda t: [r for r in rows if r.get("type") == t]
    commands = by("command")

    if args.cmd:
        if not 1 <= args.cmd <= len(commands):
            sys.exit(f"発話は {len(commands)} 件です")
        show_command(commands[args.cmd - 1], args.cmd)
        return

    start = next(iter(by("start")), {})
    end = next(iter(by("end")), None)
    s = start.get("settings", {})
    last_t = rows[-1].get("t", 0) if rows else 0
    result = {"player": "勝利", "enemy": "敗北"}.get((end or {}).get("winner"), "決着なし")
    print(f"# {args.log}")
    print(f"司令: {s.get('commander_model')} (effort {s.get('commander_effort')})  担当: {s.get('officer_model')}"
          f"  試合時間: {clock(last_t)}  結果: {result}{'' if end else '(途中で終了)'}")

    print("\n## 応答時間")
    voice = [c for c in commands if c.get("source") == "voice"]
    print(f"発話 {len(commands)}件(音声 {len(voice)}件、文字 {len(commands) - len(voice)}件)")
    print("  音声認識  :", stats([c.get("stt_seconds") for c in voice]))
    print("  司令LLM   :", stats([c.get("llm_seconds") for c in commands if not c.get("error")]))
    print("  反映まで  :", stats([c.get("applied_seconds") for c in commands if not c.get("error")]))
    print("  話し終わり→反映:", stats([(c.get("stt_seconds") or 0) + (c.get("applied_seconds") or 0) for c in voice]))
    officers = by("officer")
    if officers:
        errs = sum(1 for o in officers if o.get("error"))
        print(f"  担当LLM   : {stats([o.get('seconds') for o in officers if not o.get('error')])}  エラー {errs}件")
    stt_errors = [r for r in by("stt") if r.get("error")]
    if stt_errors:
        print(f"  音声認識の失敗: {len(stt_errors)}件")

    print("\n## 発話と結果")
    for i, c in enumerate(commands, 1):
        mark = "✗" if c.get("error") else ("?" if any(x["name"] == "reply" for x in c.get("calls", [])) else " ")
        lat = f"{c.get('stt_seconds', 0):.1f}+{c.get('llm_seconds', 0):.1f}秒"
        print(f"{mark}{i:>3}. {clock(c.get('submitted_t'))} [{c.get('source')}] 「{c.get('utterance')}」 ({lat})")
        print(f"       → {c.get('error') or c.get('result')}")
    print("  (✗=失敗、?=LLMが聞き返した。詳細は --cmd 番号)")

    events = by("event") + by("ui")
    if events:
        print("\n## 出来事")
        for e in sorted(events, key=lambda r: r.get("t", 0)):
            if e["type"] == "event":
                print(f"  {clock(e['t'])} {e.get('op')}: {e.get('text')}")
            else:
                print(f"  {clock(e['t'])} 操作: {e.get('action')} {e.get('name') or e.get('building') or ''}")
    alerts = by("alert")
    if alerts:
        print(f"\n## 警報 {len(alerts)}件")
        for a in alerts[:10]:
            print(f"  {clock(a['t'])} {a.get('text')}")

    if args.officer and officers:
        print("\n## 担当LLM")
        for o in officers:
            for call in o.get("calls", []):
                inp = call["input"]
                text = inp.get("status") if call["name"] == "report" else json.dumps(inp, ensure_ascii=False)
                print(f"  {clock(o['t'])} {o.get('op')} {call['name']}: {text}")

    snaps = by("snapshot")
    if snaps:
        print("\n## 戦況の推移")
        for sn in snaps:
            st = sn.get("stock", {})
            ops = ", ".join(f"{o['name']}({o['tag']})" for o in sn.get("ops", []) if o["name"] != "予備" or any(r["count"] for r in o["roles"]))
            print(f"  {clock(sn['t'])} 食{st.get('food')} 木{st.get('wood')} 金{st.get('gold')} 村人{sn.get('villagers')}"
                  f"  自軍[{counts(sn.get('army'))}] 敵[{counts(sn.get('enemy_army'))}]"
                  f"  損害 自{sn.get('losses', {}).get('own')}/敵{sn.get('losses', {}).get('enemy')}  {ops}")


if __name__ == "__main__":
    main()
