using System.Collections.Generic;
using System.Text.Json;

namespace LlmCommander.Command;

/// <summary>LLMに渡すツール(命令の形)とシステムプロンプト。</summary>
public static class Tools
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string Kinds = "\"spearman\", \"archer\", \"cavalry\", \"scout\", \"villager\"";

    private const string RuleProps = $$"""
        "stance": {"type": "string", "enum": ["hold", "advance", "retreat", "patrol"],
                   "description": "hold=場所で待機し近づく敵を迎撃 / advance=場所へ進撃し道中と到着先の敵を攻撃 / retreat=敵を無視して場所へ下がる"},
        "place": {"type": "string", "description": "場所。書き方は「場所の書き方」を参照(名前、「指示点1から中央へ3マス」「槍の位置」「AとBの間」「A〜B」など)"},
        "route": {"type": "array", "items": {"type": "string"}, "description": "place に着いたあと、順に向かう場所(場所の書き方で)。着くたびにゲームが次へ進める。敵の位置は着いた時点で決まる。例: place=北の丘, route=[敵の位置の敵側4マス]。空配列で取り消し"},
        "formation": {"type": "string", "enum": ["line", "cluster"], "description": "line=場所が線の目印のとき線に沿って横隊 / cluster=密集"},
        "kite": {"type": "boolean", "description": "引き撃ち(近接兵から距離を取りながら射撃)。弓向け"},
        "radius": {"type": "number", "description": "patrol で回る半径(マス)。遠巻きなら 12〜16、すぐ近くなら 6〜8"},
        "evade": {"type": "boolean", "description": "戦わない。敵兵が近づいたり攻撃されたりしたら逃げ、離れたら元の行動に戻る(見張りや偵察向け。patrol では常に有効)"},
        "priority": {"type": "array", "items": {"type": "string", "enum": [{{Kinds}}]}, "description": "優先して狙う兵種(先頭ほど優先)。空配列で優先なし"}
        """;

    private static readonly string RoleSchema = $$"""
        {
          "type": "object",
          "properties": {
            "role": {"type": "string", "description": "役割名。基本は兵種名(槍/弓/騎兵)。同じ兵種を分けるときは「槍(前列)」のように付ける"},
            "unit_kind": {"type": "string", "enum": ["spearman", "archer", "cavalry", "scout"], "description": "この役割の兵種。新しい役割では必須"},
            "assign_count": {"type": "integer", "description": "この役割に新たに配属する数。-1 で取れるだけ全部。省略すると配属は変えない"},
            "assign_from": {"type": "string", "description": "配属元。reserve(既定)/ selected(プレイヤーが選択中のユニット)/ 作戦名 / any(他の全作戦から)"},
            {{RuleProps}},
            "trigger": {
              "type": "object",
              "description": "条件が満たされたら一度だけ then の規則に切り替える(「〜したら〜しろ」)",
              "properties": {
                "condition": {"type": "string", "enum": ["enemy_near", "role_engaged", "role_hp_below"],
                              "description": "enemy_near=敵の兵が place から radius 以内に来た / role_engaged=同じ作戦の role が交戦を始めた / role_hp_below=同じ作戦の role の生存数が当初の fraction 未満(「押されたら」「劣勢なら」は 0.6 前後、「半分やられたら」は 0.5。1 にしない)"},
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
            "goal": {"type": "string", "description": "作戦の目的。プレイヤーの意図を、規則に収まらない部分も含めて言葉で書く(例: 槍で敵の弓に圧をかけ続け、劣勢なら指示点1まで下がる)。作戦の担当がこれを見て細部を決め続ける。意図が変わったときだけ書く"},
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
                      "properties": {"unit_kind": {"type": "string", "enum": ["spearman", "archer", "cavalry", "scout"]},
                                     "goal": {"type": "integer", "description": "目標の総数。-1 で人口上限まで作り続ける、0 で生産停止"}},
                      "required": ["unit_kind", "goal"]}},
            "rally": {"type": "string", "description": "新兵の集合場所(予備の兵が待つ場所)。地名・目印・指示点、own_base など"},
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
            "building": {"type": "string", "enum": ["town_center", "barracks", "storehouse"]},
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

    public static readonly ToolSpec Garrison = new("garrison",
        "村人を村の中心にこもらせる / 外に出す。中の村人は攻撃されず、村の中心は中の人数と同じ本数の矢を撃つ(最大15人)。",
        J("""
        {"type": "object",
         "properties": {"action": {"type": "string", "enum": ["enter", "leave"], "description": "enter=こもる / leave=全員外に出て仕事に戻る"},
                        "count": {"type": "integer", "description": "enter のときの人数。-1 または省略で全員"}},
         "required": ["action"]}
        """));

    public static readonly ToolSpec Scout = new("scout",
        "偵察任務を作る。斥候(または指定した兵)を任務に移し、行き先は任務の担当が目的に沿って順に決める。斥候は戦わず、敵が近づくと逃げる。",
        J("""
        {"type": "object",
         "properties": {
           "name": {"type": "string", "description": "任務の名前(短く。例: 偵察、金鉱探し、敵基地の監視)"},
           "objective": {"type": "string", "description": "目的。プレイヤーの言葉をほぼそのまま(例: 村の中心を中心に同心円状に少しずつ探索、敵の金鉱を探す、敵の拠点を遠巻きに回って様子を見る)"},
           "center": {"type": "string", "description": "探索の中心。地名・目印・own_base など(既定 own_base)。敵の本拠地を見つけていれば enemy_base も使える"},
           "unit_kind": {"type": "string", "enum": ["scout", "cavalry", "spearman", "archer"], "description": "使う兵種(既定 scout)"},
           "count": {"type": "integer", "description": "使う数(既定 1)"},
           "assign_from": {"type": "string", "description": "reserve(既定)/ selected / any / 作戦名"}
         },
         "required": ["objective"]}
        """));

    public static readonly ToolSpec SetMarker = new("set_marker",
        "地図の目印を作る・動かす(同じ名前があれば動かす)。その目印を持ち場にしている部隊も一緒に動く。「指示点を少し中央寄りに」「槍がいる場所を集合地点Cに」など。",
        J("""
        {"type": "object",
         "properties": {"name": {"type": "string", "description": "目印の名前(既存の指示点・目印、または新しい名前)"},
                        "place": {"type": "string", "description": "置く場所(場所の書き方で。例: 指示点1から中央へ3マス、予備/槍の位置)"},
                        "line_to": {"type": "string", "description": "線の目印にするときの、もう一方の端"}},
         "required": ["name", "place"]}
        """));

    public static readonly ToolSpec DeleteMarker = new("delete_marker",
        "地図の目印を消す。",
        J("""{"type": "object", "properties": {"name": {"type": "string"}}, "required": ["name"]}"""));

    public static readonly ToolSpec Reply = new("reply",
        "プレイヤーへの短い返答。質問に答えるとき、推測して実行した内容を一言伝えるとき、どうしても実行できないときに使う。",
        J("""{"type": "object", "properties": {"text": {"type": "string"}}, "required": ["text"]}"""));

    public static readonly IReadOnlyList<ToolSpec> CommanderTools = new[]
    {
        UpdateOperation, DisbandOperation, SetEconomy, ProposeBuild, ConfirmBuild, CancelBuild, Garrison, Scout, SetMarker, DeleteMarker, Reply,
    };

    // ------------------------------------------------------------ 担当LLM(作戦ごと)

    public static readonly ToolSpec Report = new("report",
        "作戦カードの状況欄を書く。毎回必ず1回、ほかのツールより先に呼ぶ。",
        J("""
        {"type": "object",
         "properties": {"status": {"type": "string", "description": "状況の要約。40字以内の日本語。例: 敵の槍8と北の丘で交戦、弓が優勢。騎兵は右翼で待機"}},
         "required": ["status"]}
        """));

    public static readonly ToolSpec OfficerReport = new("report",
        "目的の点検と、作戦カードの状況欄。毎回必ず1回、ほかのツールより先に呼ぶ。",
        J("""
        {"type": "object",
         "properties": {
           "steps": {"type": "string", "description": "目的を手順と条件に分け、それぞれ 済/今/未/発動 を付ける。例: 騎兵 北の丘へ=済、騎兵 背後を突く=今(規則を変える)、槍 押されたら指示点1へ=発動(当初6→2体)"},
           "status": {"type": "string", "description": "状況の要約。40字以内の日本語。数の優劣は数字で判断する"}},
         "required": ["steps", "status"]}
        """));

    public static readonly ToolSpec AdjustRole = new("adjust_role",
        "担当する作戦の役割の規則を変える。目的を実現するために、行き先・構え・隊列・狙い・引き撃ちなどを状況に合わせて決める。",
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

    public static readonly ToolSpec SplitRole = new("split_role",
        "役割の兵の一部を新しい役割に分けて、別の動きをさせる(例: 槍のうち3体を右から回り込ませる)。",
        J($$"""
        {
          "type": "object",
          "properties": {
            "from_role": {"type": "string"},
            "count": {"type": "integer", "description": "分ける数"},
            "new_role": {"type": "string", "description": "新しい役割名(例: 槍(右翼))。既存の役割名なら、そこへ移す"},
            {{RuleProps}},
            "reason": {"type": "string", "description": "理由(25字以内)。カードに出る"}
          },
          "required": ["from_role", "count", "new_role", "reason"]
        }
        """));

    // ------------------------------------------------------------ 偵察の担当LLM

    public static readonly ToolSpec SetWaypoint = new("set_waypoint",
        "斥候の次の行き先を1つ決める。",
        J("""
        {"type": "object",
         "properties": {"place": {"type": "string", "description": "未探索点の名前(例: 未探索点3)、地名、目印、own_base(見つけていれば enemy_base)"},
                        "reason": {"type": "string", "description": "選んだ理由(20字以内)"}},
         "required": ["place"]}
        """));

    public static readonly IReadOnlyList<ToolSpec> ScoutTools = new[] { SetWaypoint, Report };

    public const string ScoutSystem = """
        あなたはリアルタイムストラテジーゲームで、斥候を誘導する偵察の担当です。斥候が行き先に着くたび、または数秒ごとに状況を受け取ります。
        - プレイヤーの目的に沿って、次の行き先を set_waypoint で1つ選んでください。今の行き先がまだ目的に合っていて移動中なら、変えなくてかまいません。
          - 「同心円状に少しずつ」なら、探索の中心からの距離が近い未探索点から順に。遠くへ飛ばない。
          - 敵の本拠地の位置は、敵の村の中心を見つけるまでわからない。知っているふりをしないこと。
          - 「敵を探して」「敵の金鉱を探して」で本拠地が未発見なら、自軍基地から離れた未探索点を広く順に回り、敵の建物や兵を見つけたら報告する。本拠地を見つけたら、その周りの未探索点を優先する。
          - 「遠巻きに様子を見る」なら、見つけている敵の本拠地から10〜15マスほど離れた場所を順に回る。近づきすぎない。
        - 斥候は戦えません。最後に見た敵の近くは避けてください(逃げる動きはゲームがする)。
        - 毎回 report で、見つけたもの・探索の進み具合を40字以内で書いてください。文章での返答は不要です。
        """;

    public static readonly IReadOnlyList<ToolSpec> OfficerTools = new[] { OfficerReport, AdjustRole, SplitRole };

    // ------------------------------------------------------------ システムプロンプト

    /// <summary>場所の書き方(司令役と担当の両方に渡す)。</summary>
    public const string PlaceSyntax = """
        # 場所の書き方(place / center / near などに書く)
        - 名前: 状況に出ている地名・目印・指示点、own_base(自軍基地)、rally(新兵の集合地点)、enemy_base(敵の本拠地を見つけたときだけ)
        - 部隊の今の位置: 「槍の位置」「戦線A/弓の位置」「予備/槍の位置」「現在地」(担当する作戦の今の位置)
        - 見えている敵の位置: 「敵の位置」「敵の弓の位置」「敵の槍の位置」(一番近い敵の集まり)。背後は「敵の位置の敵側5マス」
        - 相対: 「指示点1から中央へ3マス」「自軍基地から北の丘へ少し」「指示点1から敵側へ5マス」「戦線A/槍の位置の北4マス」
          (方角は 北・南・東・西・北東… と、敵側(前)・自陣側(後ろ)。少し=3マス。1マスは兵1体ぶん)
        - 中間: 「指示点1と北の丘の間」
        - 線(横隊を敷くとき): 「指示点1〜指示点2」「北の丘から中央までの線」
        - 生の座標は書かない。名前は状況に出ているとおりに書き、「付近」やかっこ書きは付けない。
        """;

    public const string CommanderSystem = """
        あなたはリアルタイムストラテジーゲームで、プレイヤー(指揮官)の音声の指示を受けて部隊と内政を動かす副官です。
        プレイヤーの発話を解釈し、ツールを呼んでゲームに命令を出してください。返答の文章は不要で、命令はすべてツールで出します。

        # ゲームの仕組み
        - 兵種は槍・弓・騎兵の三すくみ。槍は騎兵に強い、騎兵は弓に強い、弓は槍に強い(射程で一方的に削れる)。
        - 村人は食・木・金を集める。兵舎で槍(食35 木25)、弓(木25 金45)、騎兵(食60 金75)を作る。村人は食50。
        - 建物: 村の中心(木275 金100。村人の生産と資源の受け取り)、兵舎(木175)、採集所(storehouse。木100。村人が食・木・金を置ける)。
        - 村人は資源を置き場(村の中心・採集所)まで運ぶ。置き場までの距離が長いと採集が遅いので、遠い資源を採るときは採集所をそばに建てる。
          「金鉱の近くに採集所」→ propose_build(building=storehouse, site=金の資源のそばの候補地)。候補地がなければ near=gold / wood / food。
        - 兵は必ずどれか一つの作戦に属する。作戦は役割(兵種ごと)を持ち、役割ごとに交戦規則(stance / place / formation / kite / priority)がある。
        - 作戦にはそれぞれ担当(部隊長のLLM)が付き、作戦の目的(goal)を見て、数秒ごとに行き先・構え・部隊の分け方を決め続ける。
          あなたは規則をざっくり置いて、規則の部品に収まらない意図(順番・条件・程度・様子を見て判断することなど)は goal に言葉で書いて任せる。
          例:「槍で敵の弓をつつきつつ、押されたら下がって」→ goal にそのまま書き、規則は stance=advance, place=敵の弓の位置 だけ置く。
          手順のある指示(「北の丘を回ってから背後を突け」)は、place に最初の行き先、route に続きの行き先を書く(place=北の丘, route=[敵の位置の敵側4マス])。ゲームが着くたびに次へ進める。
          place には「場所の書き方」で読める場所だけを書く。文章を書かない。
        - 「予備」は作戦に属していない兵の置き場。新兵は既定で予備に入る。「経済」は村人の作戦。
        - ユニットは規則に従って自律的に動き続ける。あなたは規則と割り当てを変えるだけ。
        - 斥候(scout)は速く視界が広いが弱い。探索・偵察・資源探し・遠巻きの監視は scout ツールで偵察任務にする(目的にプレイヤーの言葉を書く)。行き先は任務の担当が決めるので、あなたは場所を細かく指定しない。任務をやめるときは disband_operation。
        - 見張り(その場で待って、攻撃されたら逃げる)は update_operation の stance=hold, evade=true。決まった場所を回るなら stance=patrol, radius。
        - 「村人を村の中心に避難」「こもって守れ」は garrison(enter)、「外に出て仕事に戻れ」は garrison(leave)。
        - 「新兵は〜に集めて」「待機位置/集合場所を〜に」は set_economy の rally(作戦の持ち場ではない)。
        - 目印(指示点・地点・線)の移動や作成は set_marker。向きに注意する:
          - 「指示点を少し真ん中側に」→ set_marker(name=指示点1, place=指示点1から中央へ3マス)
          - 「今槍がいる場所を指示点1にして」→ set_marker(name=指示点1, place=予備/槍の位置)(目印を部隊の所へ動かす。部隊は動かさない)
          - 「指示点1を待機位置にして」「新兵はここに」→ set_economy(rally=指示点1)(集合地点を目印の所へ。目印は動かさない)
        - 霧がある。状況に出る敵は自軍から見えているものと、最後に見た位置の記録だけ。ユニットも見えている敵しか狙わない。敵の様子がわからないときは、少数の騎兵で偵察する作戦を作るとよい。

        # 指示の解釈
        - 同じ部隊・同じ戦線についての発話は、新しい作戦を作らず既存の作戦の更新にする。新しい戦線・別の目的なら新しい作戦を作る。名前は短く(例: 戦線A、北の守り、騎兵の奇襲)。
        - 「弓は引き撃ち、他は撤退」のように役割ごとに違う指示は、1回の update_operation で各役割を更新する。言及されていない項目は書かない(今の規則が残る)。
        - 「こいつら」「選択中の」は assign_from=selected。「ここ」「そこ」は発話中にクリックされた指示点(指示点1 など)。
          クリックされた指示点はプレイヤーが指した場所そのものなので、set_marker で動かさない(「槍の待機位置はここ」→ 槍の place=指示点1)。
        - 場所は「場所の書き方」に従う。名前の付いていない場所も、名前の付いた場所や部隊の位置からの相対で書ける。線の目印に陣取るときは formation=line。
        - stance を retreat にするときは、撤退先の place を必ず書く(言われていなければ rally か own_base)。
        - 「〜したら〜」は trigger。例:「槍が受け止めたら騎兵を右から回せ」→ 騎兵の役割に trigger(condition=role_engaged, role=槍, then={stance: advance, place: ...})。
        - 内政の人数指定は、今の割り当てから計算して gather に最終的な人数を入れる(例:「木のうち5人を残して残りは金」→ 木=5、金=今の金+移る人数)。gather の合計は村人の数を超えないようにする。言われた数の合計が超えるときは、言われていない資源を減らす。
        - 建設は propose_build で提案するだけ(プレイヤーが確認する)。場所は「建設候補地」の特徴(資源までの距離、基地からの距離、敵の目撃)を見て、プレイヤーの条件に一番合う候補を site で選ぶ。合う候補がなければ near で地名・目印を指定する。プレイヤーが「OK」「それで」「建てて」と承認したら confirm_build。どれを承認したか言われていなければ、「直前の提案」の印がある計画だけを確定する。
        - 聞き返さない。曖昧な指示も、状況から一番ありそうな解釈で実行し、推測した点は change_summary に書く(例: 「指示点1を3マス中央寄りへ(距離は推測)」)。
          聞き間違いらしい発話も、状況に合う意味に読み替えて、ツールで実行する。どう読んでも実行できないときだけ reply で短く伝える。
          reply に、ツールで実行していないことを「した」と書かない。
        - 質問(「待機位置は?」など)には reply で答える。
        """ + PlaceSyntax;

    public const string OfficerSystem = """
        あなたはリアルタイムストラテジーゲームで、一つの作戦を任された部隊長です。プレイヤーの意図(作戦の目的と指示)を実現する責任者です。
        作戦が作られた・変わった直後と、その後は数秒ごと(何か起きたらすぐ)に状況を受け取ります。ユニットは今の規則どおりに動き続けるので、規則を変えない限り何も変わりません。

        毎回の手順:
        1. report の steps で、目的を手順と条件に分けて、それぞれ 済/今/未/発動 を判定する。
           - 手順(「Aへ行ってからBへ」「回り込んでから突く」): 規則の「その後」に続きが入っていれば、ゲームが着くたびに進めるので任せてよい。
             入っていなければ、役割が「到着済み」になったときに adjust_role で次の場所にする(route を使ってもよい)。
           - 条件(「押されたら」「劣勢なら」「敵が来たら」): 数字で判定する。交戦中で、生存数が当初の6割以下、または平均体力が5割以下なら「押されている」。
        2. 「今」になった手順と「発動」した条件は、すぐ adjust_role / split_role で規則に入れる。報告だけで終わらせない。
           - 例: 騎兵が北の丘に到着済み、次は背後を突く → adjust_role(騎兵, stance=advance, place=敵の位置の敵側4マス)
           - 例: 槍が当初6→2体で交戦中、押されたら指示点1へ → adjust_role(槍, stance=retreat, place=指示点1)
           - 部隊を分けて別の動きをさせるときは split_role。
        3. 規則がすでに目的どおりなら、変えない(同じ内容を何度も送らない)。

        - 一番新しいプレイヤーの指示が最優先。指示にはっきり書かれた構えや場所は、目的のために必要なときだけ変える。
        - 「敵の弓の位置」のような敵の位置を持ち場にすると、ゲームが敵の動きに合わせて追い続ける。
        - status は40字以内。交戦相手・優劣(数で)・次にやることを簡潔に。
        - 文章での返答は不要です。ツールだけを使ってください。
        """ + PlaceSyntax;

}
