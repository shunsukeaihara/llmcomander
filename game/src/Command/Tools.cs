using System.Collections.Generic;
using System.Text.Json;

namespace LlmCommander.Command;

/// <summary>LLMに渡すツール(命令の形)とシステムプロンプト。</summary>
public static class Tools
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string Kinds = "\"spearman\", \"archer\", \"cavalry\", \"villager\"";

    private const string RuleProps = $$"""
        "stance": {"type": "string", "enum": ["hold", "advance", "retreat"],
                   "description": "hold=場所で待機し近づく敵を迎撃 / advance=場所へ進撃し道中と到着先の敵を攻撃 / retreat=敵を無視して場所へ下がる"},
        "place": {"type": "string", "description": "場所の名前。状況に出ている地名・目印・指示点、または own_base / enemy_base / rally"},
        "formation": {"type": "string", "enum": ["line", "cluster"], "description": "line=場所が線の目印のとき線に沿って横隊 / cluster=密集"},
        "kite": {"type": "boolean", "description": "引き撃ち(近接兵から距離を取りながら射撃)。弓向け"},
        "priority": {"type": "array", "items": {"type": "string", "enum": [{{Kinds}}]}, "description": "優先して狙う兵種(先頭ほど優先)。空配列で優先なし"}
        """;

    private static readonly string RoleSchema = $$"""
        {
          "type": "object",
          "properties": {
            "role": {"type": "string", "description": "役割名。基本は兵種名(槍/弓/騎兵)。同じ兵種を分けるときは「槍(前列)」のように付ける"},
            "unit_kind": {"type": "string", "enum": ["spearman", "archer", "cavalry"], "description": "この役割の兵種。新しい役割では必須"},
            "assign_count": {"type": "integer", "description": "この役割に新たに配属する数。-1 で取れるだけ全部。省略すると配属は変えない"},
            "assign_from": {"type": "string", "description": "配属元。reserve(既定)/ selected(プレイヤーが選択中のユニット)/ 作戦名 / any(他の全作戦から)"},
            {{RuleProps}},
            "trigger": {
              "type": "object",
              "description": "条件が満たされたら一度だけ then の規則に切り替える(「〜したら〜しろ」)",
              "properties": {
                "condition": {"type": "string", "enum": ["enemy_near", "role_engaged", "role_hp_below"],
                              "description": "enemy_near=敵の兵が place から radius 以内に来た / role_engaged=同じ作戦の role が交戦を始めた / role_hp_below=同じ作戦の role の生存数が当初の fraction 未満"},
                "place": {"type": "string"},
                "radius": {"type": "number"},
                "role": {"type": "string"},
                "fraction": {"type": "number"},
                "then": {"type": "object", "properties": { {{RuleProps}} } },
                "description": {"type": "string", "description": "カードに出す条件の説明。例: 槍が接触したら右から展開"}
              },
              "required": ["condition", "then", "description"]
            },
            "clear_trigger": {"type": "boolean", "description": "待機中の条件を取り消す"}
          },
          "required": ["role"]
        }
        """;

    public static readonly ToolSpec UpdateOperation = new("update_operation",
        "作戦を作る・更新する。役割ごとに配属と交戦規則を指定する。指定しなかった項目は今のまま残る(項目単位の上書き)。",
        J($$"""
        {
          "type": "object",
          "properties": {
            "operation": {"type": "string", "description": "作戦名。既存の名前なら更新、新しい名前なら作成。例: 戦線A、北の守り"},
            "roles": {"type": "array", "items": {{RoleSchema}} },
            "change_summary": {"type": "string", "description": "カードに出す変更の要約(25字以内の日本語)"}
          },
          "required": ["operation", "roles", "change_summary"]
        }
        """));

    public static readonly ToolSpec DisbandOperation = new("disband_operation",
        "作戦を解散し、所属する兵を予備に戻す。",
        J("""
        {"type": "object",
         "properties": {"operation": {"type": "string"}, "change_summary": {"type": "string"}},
         "required": ["operation"]}
        """));

    public static readonly ToolSpec SetEconomy = new("set_economy",
        "内政を設定する。指定した項目だけ変わる。村人の割り当ては資源ごとの人数(合計は村人の数以下)。どの村人を動かすかはゲームが決める。",
        J("""
        {
          "type": "object",
          "properties": {
            "gather": {"type": "object", "properties": {"food": {"type": "integer"}, "wood": {"type": "integer"}, "gold": {"type": "integer"}},
                       "description": "資源ごとの村人の人数(変更する資源だけでよい)"},
            "villagers_continuous": {"type": "boolean", "description": "村の中心で村人を作り続ける"},
            "villager_target": {"type": "integer", "description": "村人の目標人数"},
            "train": {"type": "array", "items": {"type": "object",
                      "properties": {"unit_kind": {"type": "string", "enum": ["spearman", "archer", "cavalry"]},
                                     "goal": {"type": "integer", "description": "目標の総数。-1 で人口上限まで作り続ける、0 で生産停止"}},
                      "required": ["unit_kind", "goal"]}},
            "reinforce": {"type": "object", "description": "新しく作った兵の配属先",
                          "properties": {"operation": {"type": "string"}, "role": {"type": "string"}}, "required": ["operation"]},
            "change_summary": {"type": "string"}
          },
          "required": ["change_summary"]
        }
        """));

    public static readonly ToolSpec ProposeBuild = new("propose_build",
        "建物の建設を提案する。地図に仮置きされ、プレイヤーの確認(OK)で着工する。場所は状況の「建設候補地」から site で選ぶのが基本。",
        J("""
        {
          "type": "object",
          "properties": {
            "building": {"type": "string", "enum": ["town_center", "barracks"]},
            "site": {"type": "string", "description": "状況の「建設候補地」の名前(例: 候補地2)。プレイヤーの条件に一番合うものを選ぶ"},
            "near": {"type": "string", "description": "候補地に合うものがないときだけ使う。どこの近くか: 地名・目印・指示点、own_base、資源 food / wood / gold、まだ使っていない金鉱なら new_gold"},
            "count": {"type": "integer"},
            "change_summary": {"type": "string"}
          },
          "required": ["building"]
        }
        """));

    public static readonly ToolSpec ConfirmBuild = new("confirm_build",
        "確認待ちの建設計画を確定する(プレイヤーが「OK」「建てて」など承認したとき)。plan_ids を省略すると確認待ちを全部。",
        J("""{"type": "object", "properties": {"plan_ids": {"type": "array", "items": {"type": "integer"}}}}"""));

    public static readonly ToolSpec CancelBuild = new("cancel_build",
        "建設計画を取り消す。plan_ids を省略すると確認待ちを全部。",
        J("""{"type": "object", "properties": {"plan_ids": {"type": "array", "items": {"type": "integer"}}}}"""));

    public static readonly ToolSpec Reply = new("reply",
        "プレイヤーへの短い返答。指示が曖昧で実行できないとき、質問に答えるときに使う。命令を実行できたときは不要。",
        J("""{"type": "object", "properties": {"text": {"type": "string"}}, "required": ["text"]}"""));

    public static readonly IReadOnlyList<ToolSpec> CommanderTools = new[]
    {
        UpdateOperation, DisbandOperation, SetEconomy, ProposeBuild, ConfirmBuild, CancelBuild, Reply,
    };

    // ------------------------------------------------------------ 担当LLM(作戦ごと)

    public static readonly ToolSpec Report = new("report",
        "作戦カードの状況欄を書く。毎回必ず1回呼ぶ。",
        J("""
        {"type": "object",
         "properties": {"status": {"type": "string", "description": "状況の要約。40字以内の日本語。例: 敵の槍8と北の丘で交戦、弓が優勢。騎兵は右翼で待機"}},
         "required": ["status"]}
        """));

    public static readonly ToolSpec AdjustRole = new("adjust_role",
        "担当する作戦の役割の規則を調整する。プレイヤーの指示の範囲内で、状況に合わせた細部の判断にだけ使う。",
        J($$"""
        {
          "type": "object",
          "properties": {
            "role": {"type": "string"},
            {{RuleProps}},
            "reason": {"type": "string", "description": "調整の理由(25字以内)。カードに出る"}
          },
          "required": ["role", "reason"]
        }
        """));

    public static readonly IReadOnlyList<ToolSpec> OfficerTools = new[] { Report, AdjustRole };

    // ------------------------------------------------------------ システムプロンプト

    public const string CommanderSystem = """
        あなたはリアルタイムストラテジーゲームで、プレイヤー(指揮官)の音声の指示を受けて部隊と内政を動かす副官です。
        プレイヤーの発話を解釈し、ツールを呼んでゲームに命令を出してください。返答の文章は不要で、命令はすべてツールで出します。

        # ゲームの仕組み
        - 兵種は槍・弓・騎兵の三すくみ。槍は騎兵に強い、騎兵は弓に強い、弓は槍に強い(射程で一方的に削れる)。
        - 村人は食・木・金を集める。兵舎で槍(食35 木25)、弓(木25 金45)、騎兵(食60 金75)を作る。村人は食50。
        - 建物: 村の中心(木275 金100。村人の生産と資源の受け取り)、兵舎(木175)。
        - 兵は必ずどれか一つの作戦に属する。作戦は役割(兵種ごと)を持ち、役割ごとに交戦規則(stance / place / formation / kite / priority)がある。
        - 「予備」は作戦に属していない兵の置き場。新兵は既定で予備に入る。「経済」は村人の作戦。
        - ユニットは規則に従って自律的に動き続ける。あなたは規則と割り当てを変えるだけ。
        - 霧がある。状況に出る敵は自軍から見えているものと、最後に見た位置の記録だけ。ユニットも見えている敵しか狙わない。敵の様子がわからないときは、少数の騎兵で偵察する作戦を作るとよい。

        # 指示の解釈
        - 同じ部隊・同じ戦線についての発話は、新しい作戦を作らず既存の作戦の更新にする。新しい戦線・別の目的なら新しい作戦を作る。名前は短く(例: 戦線A、北の守り、騎兵の奇襲)。
        - 「弓は引き撃ち、他は撤退」のように役割ごとに違う指示は、1回の update_operation で各役割を更新する。言及されていない項目は書かない(今の規則が残る)。
        - 「こいつら」「選択中の」は assign_from=selected。「ここ」「そこ」は発話中にクリックされた指示点(指示点1 など)。
        - 場所は状況に出ている名前だけを使う。線の目印に陣取るときは formation=line。
        - 「〜したら〜」は trigger。例:「槍が受け止めたら騎兵を右から回せ」→ 騎兵の役割に trigger(condition=role_engaged, role=槍, then={stance: advance, place: ...})。
        - 内政の人数指定は、今の割り当てから計算して gather に最終的な人数を入れる(例:「木のうち5人を残して残りは金」→ 木=5、金=今の金+移る人数)。
        - 建設は propose_build で提案するだけ(プレイヤーが確認する)。場所は「建設候補地」の特徴(資源までの距離、基地からの距離、敵の目撃)を見て、プレイヤーの条件に一番合う候補を site で選ぶ。合う候補がなければ near で地名・目印を指定する。プレイヤーが「OK」「それで」「建てて」と承認したら confirm_build。
        - 実行できない・曖昧すぎるときは reply で短く聞き返す。推測で補えるときは推測して実行する。
        """;

    public const string OfficerSystem = """
        あなたはリアルタイムストラテジーゲームで、一つの作戦を担当する部隊長です。数秒ごとに戦況を受け取ります。
        - 毎回 report で作戦カードの状況欄(40字以内)を書いてください。プレイヤーが一目で戦況を掴めるよう、交戦相手・優劣・隊列・懸念を簡潔に。
        - adjust_role は、プレイヤーの指示の範囲で細部を判断するときだけ使います(例: 隊列が崩れたので集合地点で再編、狙う兵種を戦況に合わせる)。
          プレイヤーの指示の stance や place を勝手に変えないでください。迷ったら変えずに report で懸念を伝えます。
        - 文章での返答は不要です。ツールだけを使ってください。
        """;
}
