using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LlmCommander.Sim;

namespace LlmCommander.Command;

/// <summary>LLMのツール呼び出しをゲームに適用する。不正な入力は例外にせず、結果の文に理由を書く。</summary>
public sealed class Applier
{
    private readonly World _w;
    private readonly int _team;

    public Applier(World w, int team)
    {
        _w = w;
        _team = team;
    }

    private TeamState T => _w.Teams[_team];

    // ------------------------------------------------------------ JSON の読み取り

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return (int)Math.Round(d);
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var i)) return i;
        return null;
    }

    private static float? Num(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : null;

    private static bool? Bool(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    private static JsonElement? Obj(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static IEnumerable<JsonElement> Arr(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private Place? PlaceOf(string? name) => name == null ? null : _w.ResolvePlace(_team, name);

    /// <summary>規則の項目を読む。場所が解決できなければ problems に書く。</summary>
    private RulePatch ReadRule(JsonElement e, List<string> problems)
    {
        var p = new RulePatch();
        switch (Str(e, "stance"))
        {
            case "hold": p.Stance = Stance.Hold; break;
            case "advance": p.Stance = Stance.Advance; break;
            case "retreat": p.Stance = Stance.Retreat; break;
            case "explore": p.Stance = Stance.Explore; break;
            case "patrol": p.Stance = Stance.Patrol; break;
        }
        if (Str(e, "place") is { } pn)
        {
            p.Anchor = PlaceOf(pn);
            if (p.Anchor == null) problems.Add($"場所「{pn}」が見つかりません");
        }
        switch (Str(e, "formation"))
        {
            case "line": p.Formation = Formation.Line; break;
            case "cluster": p.Formation = Formation.Cluster; break;
        }
        p.Kite = Bool(e, "kite");
        p.Evade = Bool(e, "evade");
        if (Num(e, "radius") is { } rad) p.Radius = Math.Clamp(rad, 3, 60);
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("priority", out var pr) && pr.ValueKind == JsonValueKind.Array)
            p.Priority = pr.EnumerateArray()
                .Select(x => Defs.TryParseUnit(x.GetString(), out var k) ? (UnitKind?)k : null)
                .Where(k => k != null).Select(k => k!.Value).Distinct().ToList();
        return p;
    }

    // ------------------------------------------------------------ 適用

    /// <param name="selected">発話時に選択されていたユニット。</param>
    /// <param name="sites">発話の時点で LLM に見せた建設候補地(名前 → 候補)。</param>
    public string Apply(ToolCall call, string utterance, IReadOnlyList<int> selected,
                        IReadOnlyDictionary<string, SiteCandidate>? sites = null)
    {
        try
        {
            return call.Name switch
            {
                "update_operation" => UpdateOperation(call.Input, utterance, selected),
                "disband_operation" => Disband(call.Input),
                "set_economy" => SetEconomy(call.Input),
                "propose_build" => ProposeBuild(call.Input, sites),
                "confirm_build" => Confirm(call.Input, true),
                "cancel_build" => Confirm(call.Input, false),
                "garrison" => Garrison(call.Input),
                // 中身のない返答は無視する(命令に添えて空の reply が付いてくることがある)
                "reply" => string.IsNullOrWhiteSpace(Str(call.Input, "text")) ? "" : "返答: " + Str(call.Input, "text"),
                _ => $"不明な命令 {call.Name}",
            };
        }
        catch (Exception ex)
        {
            return $"{call.Name} の適用に失敗: {ex.Message}";
        }
    }

    private string UpdateOperation(JsonElement input, string utterance, IReadOnlyList<int> selected)
    {
        string name = (Str(input, "operation") ?? "").Trim();
        if (name == "" || name is "経済" or "economy") return "作戦名がありません";
        var op = T.FindOp(name);
        bool created = false;
        if (op == null)
        {
            op = new Operation { Id = _w.NextId(), Team = _team, Name = name, CreatedAt = _w.Time };
            T.Ops.Add(op);
            created = true;
        }
        var problems = new List<string>();
        var done = new List<string>();

        foreach (var re in Arr(input, "roles"))
        {
            string roleName = (Str(re, "role") ?? "").Trim();
            Defs.TryParseUnit(Str(re, "unit_kind"), out var kind);
            bool hasKind = Str(re, "unit_kind") != null && Defs.TryParseUnit(Str(re, "unit_kind"), out _);
            if (roleName == "" && hasKind) roleName = Defs.Ja(kind);
            if (roleName == "") { problems.Add("役割名がありません"); continue; }

            var patch = ReadRule(re, problems);
            var role = op.FindRole(roleName);
            bool newRole = role == null;
            if (role == null)
            {
                if (!hasKind && !Defs.TryParseUnit(roleName, out kind)) { problems.Add($"役割「{roleName}」の兵種が不明です"); continue; }
                var anchor = patch.Anchor ?? op.Roles.FirstOrDefault()?.Anchor ?? new Place("集合地点", new[] { T.Rally });
                role = new Role(roleName, anchor) { Kind = kind };
                op.Roles.Add(role);
            }

            // 配属
            int? count = Int(re, "assign_count");
            if (newRole && count == null) count = -1; // 新しい役割は、指定がなければ回せるだけ配属する
            string from = Str(re, "assign_from") ?? (selected.Count > 0 && count == null && created ? "selected" : "reserve");
            if (count != null || from == "selected")
            {
                int moved = Assign(op, role, count ?? -1, from, selected);
                role.InitialCount = _w.Members(op, role).Count();
                if (moved > 0) done.Add($"{role.Name}に{moved}体を配属");
                else if (count != 0) problems.Add($"{role.Name}に回せる兵がいません");
            }

            if (Bool(re, "clear_trigger") == true && role.Trigger != null)
            {
                role.Trigger = null;
                done.Add($"{role.Name}の条件を取り消し");
            }
            if (Obj(re, "trigger") is { } te)
            {
                var tr = ReadTrigger(te, problems);
                if (tr != null)
                {
                    role.Trigger = tr;
                    done.Add($"{role.Name}: 条件「{tr.Text}」");
                }
            }
            // 撤退先が書かれていなければ集合地点へ下がる(今の持ち場のままだと下がらないため)
            if (patch.Stance == Stance.Retreat && patch.Anchor == null)
                patch.Anchor = _w.ResolvePlace(_team, "rally");
            if (!patch.IsEmpty) role.Apply(patch, _w.Time);
        }

        // 誰もいない役割は消す(予備の兵種別の役割は残す)
        op.Roles.RemoveAll(r => !_w.Members(op, r).Any() && r.Trigger == null && !op.IsReserve && op.Roles.Count > 1);

        string summary = Str(input, "change_summary") ?? "更新";
        op.Intents.Add(utterance);
        if (op.Intents.Count > 8) op.Intents.RemoveAt(0);
        op.Log(_w.Time, $"「{utterance}」→ {summary}");
        op.Eventful = true;
        T.Version++;
        var result = $"{(created ? "作成" : "更新")}: {op.Name}({summary})";
        if (problems.Count > 0) result += " ※" + string.Join("、", problems.Distinct());
        return result;
    }

    private Trigger? ReadTrigger(JsonElement te, List<string> problems)
    {
        var tr = new Trigger { Text = Str(te, "description") ?? "条件" };
        switch (Str(te, "condition"))
        {
            case "enemy_near":
                tr.Kind = ConditionKind.EnemyNear;
                tr.Place = PlaceOf(Str(te, "place"));
                tr.Radius = Num(te, "radius") ?? 5;
                if (tr.Place == null) { problems.Add($"条件の場所「{Str(te, "place")}」が見つかりません"); return null; }
                break;
            case "role_engaged":
                tr.Kind = ConditionKind.RoleEngaged;
                tr.Role = Str(te, "role") ?? "";
                break;
            case "role_hp_below":
                tr.Kind = ConditionKind.RoleHpBelow;
                tr.Role = Str(te, "role") ?? "";
                tr.Fraction = Num(te, "fraction") ?? 0.5f;
                break;
            default:
                problems.Add("条件の種類が不明です");
                return null;
        }
        tr.Then = Obj(te, "then") is { } then ? ReadRule(then, problems) : new RulePatch();
        return tr;
    }

    /// <summary>兵を役割に移す。from: reserve / selected / 作戦名 / any。</summary>
    private int Assign(Operation op, Role role, int count, string from, IReadOnlyList<int> selected)
    {
        IEnumerable<Unit> pool = from switch
        {
            "selected" => selected.Select(id => _w.Units.GetValueOrDefault(id)).Where(u => u is { Alive: true } && u.Team == _team).Cast<Unit>(),
            "reserve" => _w.Members(T.Reserve),
            "any" => _w.TeamUnits(_team).Where(u => u.OpId != op.Id && u.Kind != UnitKind.Villager),
            _ => T.FindOp(from) is { } src ? _w.Members(src) : _w.Members(T.Reserve),
        };
        var list = pool.Where(u => u.Kind != UnitKind.Villager && (role.Kind == null || u.Kind == role.Kind) &&
                                   !(u.OpId == op.Id && u.Role == role.Name))
            .OrderBy(u => role.Anchor.Distance(u.Pos)).ToList();
        if (count >= 0) list = list.Take(count).ToList();
        foreach (var u in list)
        {
            u.OpId = op.Id;
            u.Role = role.Name;
        }
        return list.Count;
    }

    private string Disband(JsonElement input)
    {
        string name = Str(input, "operation") ?? "";
        var op = T.FindOp(name);
        if (op == null || op.IsEconomy || op.IsReserve) return $"作戦「{name}」は解散できません";
        foreach (var u in _w.Members(op).ToList())
        {
            u.OpId = T.Reserve.Id;
            u.Role = _w.EnsureRole(T.Reserve, u.Kind).Name;
        }
        op.Disbanded = true;
        if (T.ReinforceOp == op.Id) T.ReinforceOp = T.Reserve.Id;
        T.Version++;
        return $"解散: {op.Name}";
    }

    private string SetEconomy(JsonElement input)
    {
        var parts = new List<string>();
        if (Obj(input, "gather") is { } g)
        {
            // 割り当てを決め直すので、直接指示で待機させていた村人も仕事に戻す
            foreach (var v in _w.TeamUnits(_team).Where(v => v.Kind == UnitKind.Villager)) v.ManualTarget = null;
            int villagers = _w.TeamUnits(_team).Count(u => u.Kind == UnitKind.Villager);
            foreach (Res r in Enum.GetValues<Res>())
                if (Int(g, Defs.Key(r)) is { } n)
                    T.Gather[r] = Math.Max(0, n);
            int total = T.Gather.Values.Sum();
            parts.Add("割り当て " + string.Join(" ", Enum.GetValues<Res>().Select(r => $"{Defs.Ja(r)}{T.Gather[r]}")) +
                      (total > villagers ? $"(村人{villagers}人に対して{total}人分)" : ""));
        }
        if (Bool(input, "villagers_continuous") is { } vc)
        {
            T.VillagersContinuous = vc;
            parts.Add(vc ? "村人を継続生産" : "村人の生産停止");
        }
        if (Int(input, "villager_target") is { } vt)
        {
            T.VillagerTarget = Math.Clamp(vt, 0, Defs.PopCap);
            parts.Add($"村人の目標{T.VillagerTarget}人");
        }
        foreach (var t in Arr(input, "train"))
            if (Defs.TryParseUnit(Str(t, "unit_kind"), out var k) && k != UnitKind.Villager && Int(t, "goal") is { } goal)
            {
                T.TrainGoal[k] = Math.Max(-1, goal);
                parts.Add($"{Defs.Ja(k)}の生産 {(goal < 0 ? "継続" : goal == 0 ? "停止" : goal + "体")}");
            }
        if (Str(input, "rally") is { } rallyName)
        {
            var place = PlaceOf(rallyName);
            if (place == null) parts.Add($"集合場所「{rallyName}」が見つかりません");
            else
            {
                _w.SetRally(_team, place);
                parts.Add($"新兵の集合場所を{place.Name}に");
            }
        }
        if (Obj(input, "reinforce") is { } rf)
        {
            var op = T.FindOp(Str(rf, "operation") ?? "");
            if (op == null || op.IsEconomy) parts.Add($"配属先「{Str(rf, "operation")}」が見つかりません");
            else
            {
                T.ReinforceOp = op.Id;
                T.ReinforceRole = Str(rf, "role") ?? "";
                parts.Add($"新兵は{op.Name}{(T.ReinforceRole != "" ? "/" + T.ReinforceRole : "")}へ");
            }
        }
        string summary = Str(input, "change_summary") ?? string.Join("、", parts);
        T.EconomyNote = summary;
        T.EconomyNoteAt = _w.Time;
        T.Economy.Log(_w.Time, summary);
        T.Version++;
        return "内政: " + string.Join("、", parts);
    }

    private string ProposeBuild(JsonElement input, IReadOnlyDictionary<string, SiteCandidate>? sites)
    {
        if (!Defs.TryParseBuilding(Str(input, "building"), out var kind)) return "建物の種類が不明です";
        string near = Str(input, "near") ?? "own_base";
        int count = Math.Clamp(Int(input, "count") ?? 1, 1, 3);
        System.Numerics.Vector2? pos = null;
        // 候補地が選ばれていればそこに建てる(候補の計算後に塞がっていれば、その近くで探し直す)
        if (Str(input, "site") is { } siteName)
        {
            if (sites != null && sites.TryGetValue(siteName.Trim(), out var cand))
            {
                pos = cand.Center;
                near = $"{siteName}({cand.Region})";
            }
            else if (Str(input, "near") == null) return $"建設候補地「{siteName}」が見つかりません";
        }
        if (pos == null)
        {
            if (near == "new_gold") pos = _w.NearResource(_team, Res.Gold, _w.BasePos(_team), unclaimed: true);
            else if (Defs.TryParseRes(near, out var res)) pos = _w.NearResource(_team, res, _w.BasePos(_team), unclaimed: false);
            else pos = PlaceOf(near)?.Center;
        }
        if (pos == null) return $"建設場所「{near}」が見つかりません";

        var ids = new List<int>();
        for (int i = 0; i < count; i++)
        {
            var site = _w.FindBuildSite(kind, pos.Value);
            if (site == null) break;
            var plan = new BuildPlan
            {
                Id = _w.NextId(), Kind = kind, TileX = site.Value.X, TileY = site.Value.Y,
                Status = PlanStatus.Proposed, Near = near,
            };
            T.Plans.Add(plan);
            ids.Add(plan.Id);
        }
        if (ids.Count == 0) return $"{near}の近くに{Defs.Ja(kind)}を建てる場所がありません";
        T.Version++;
        var where = _w.Map.RegionName(T.Plans.First(p => p.Id == ids[0]).Center);
        return $"建設を提案: {Defs.Ja(kind)}×{ids.Count}({where}付近、#{string.Join(",#", ids)})。確認待ち";
    }

    private string Confirm(JsonElement input, bool ok)
    {
        var ids = Arr(input, "plan_ids").Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToHashSet();
        var plans = T.Plans.Where(p => p.Status == PlanStatus.Proposed && (ids.Count == 0 || ids.Contains(p.Id))).ToList();
        if (!ok)
            plans = T.Plans.Where(p => p.Status is PlanStatus.Proposed or PlanStatus.Confirmed && (ids.Count == 0 || ids.Contains(p.Id))).ToList();
        if (plans.Count == 0) return ok ? "確認待ちの建設はありません" : "取り消せる建設はありません";
        foreach (var p in plans) p.Status = ok ? PlanStatus.Confirmed : PlanStatus.Cancelled;
        T.Version++;
        return (ok ? "建設を確定: " : "建設を取り消し: ") + string.Join("、", plans.Select(p => $"{Defs.Ja(p.Kind)}#{p.Id}"));
    }

    private string Garrison(JsonElement input)
    {
        if (Str(input, "action") == "leave")
        {
            int n = _w.Ungarrison(_team);
            return n > 0 ? $"村人{n}人を村の中心から出して仕事に戻す" : "村の中心にこもっている村人はいません";
        }
        int sent = _w.Garrison(_team, Int(input, "count") ?? -1);
        return sent > 0 ? $"村人{sent}人を村の中心にこもらせる" : "こもれる村の中心がありません(満員か未完成)";
    }

    // ------------------------------------------------------------ 直接指示(右クリック)

    /// <summary>
    /// 選択したユニットへの直接指示。クリックした場所に応じて、移動・攻撃・採集・建設・こもりを決める。
    /// 兵は作戦「直接指示」の役割にまとめ、村人は経済の割り当てもそれに合わせて変える。
    /// </summary>
    public string ManualOrder(IReadOnlyList<int> selected, System.Numerics.Vector2 tile)
    {
        var units = selected.Select(id => _w.Units.GetValueOrDefault(id)).Where(u => u is { Alive: true } && u.Team == _team).Cast<Unit>().ToList();
        if (units.Count == 0) return "";
        var vis = _w.Vision[_team];
        int tx = (int)tile.X, ty = (int)tile.Y;
        var enemyUnit = _w.Units.Values
            .Where(o => o.Team != _team && o.Alive && !o.IsInside && vis.IsVisible(o.Pos) && System.Numerics.Vector2.Distance(o.Pos, tile) < 1.2f)
            .OrderBy(o => System.Numerics.Vector2.Distance(o.Pos, tile)).FirstOrDefault();
        Building? building = _w.Map.In(tx, ty) && _w.Map.Occupant[ty * _w.Map.W + tx] is int bid and > 0 &&
                             _w.Buildings.TryGetValue(bid, out var bb) ? bb : null;
        bool enemyBuilding = building != null && building.Team != _team && (vis.Buildings.ContainsKey(building.Id) || !vis.Enabled);
        var region = _w.Map.RegionName(tile);
        var parts = new List<string>();

        var villagers = units.Where(u => u.Kind == UnitKind.Villager).ToList();
        if (villagers.Count > 0) parts.Add(VillagerOrder(villagers, tile, building, region));

        var army = units.Where(u => u.Kind != UnitKind.Villager).ToList();
        if (army.Count > 0)
        {
            bool attack = enemyUnit != null || enemyBuilding;
            var target = enemyUnit?.Pos ?? (enemyBuilding ? building!.Pos : tile);
            var op = T.ActiveOps.FirstOrDefault(o => o.IsManual);
            if (op == null)
            {
                op = new Operation { Id = _w.NextId(), Team = _team, Name = "直接指示", IsManual = true, CreatedAt = _w.Time };
                T.Ops.Add(op);
            }
            int n = 1;
            while (op.Roles.Any(r => r.Name == $"指示{n}")) n++;
            var place = new Place(attack ? $"{region}の攻撃目標" : $"{region}の指定地点", new[] { target });
            var role = new Role($"指示{n}", place) { Stance = attack ? Stance.Advance : Stance.Hold, FocusTarget = enemyUnit?.Id ?? 0 };
            op.Roles.Add(role);
            foreach (var u in army)
            {
                u.OpId = op.Id;
                u.Role = role.Name;
                u.ExploreGoal = null;
            }
            role.InitialCount = army.Count;
            string who = string.Join(" ", army.GroupBy(u => u.Kind).Select(g => $"{Defs.Ja(g.Key)}{g.Count()}"));
            string what = attack ? $"{region}の{(enemyUnit != null ? "敵兵" : "敵の" + Defs.Ja(building!.Kind))}を攻撃" : $"{region}へ移動して待機";
            op.Log(_w.Time, $"右クリック: {who} → {what}");
            parts.Add($"{who}: {what}");
        }
        T.Version++;
        return string.Join(" / ", parts.Where(p => p != ""));
    }

    private string VillagerOrder(List<Unit> villagers, System.Numerics.Vector2 tile, Building? building, string region)
    {
        // 今の仕事から外す(割り当ての目標もそのぶん減らす)
        void Release(Unit v)
        {
            if (v.Job == JobKind.Gather) T.Gather[v.JobRes] = Math.Max(0, T.Gather[v.JobRes] - 1);
            v.Job = JobKind.None;
            v.ManualTarget = null;
            v.GarrisonTarget = 0;
        }

        if (building != null && building.Team == _team && building.Kind == BuildingKind.TownCenter && building.Done)
        {
            foreach (var v in villagers) Release(v);
            int sent = _w.Garrison(_team, -1, villagers.Select(v => v.Id).ToHashSet());
            return $"村人{sent}人: 村の中心にこもる";
        }
        if (building != null && building.Team == _team && !building.Done)
        {
            foreach (var v in villagers)
            {
                Release(v);
                v.Job = JobKind.Build;
                v.JobTarget = building.Id;
            }
            return $"村人{villagers.Count}人: {Defs.Ja(building.Kind)}の建設を手伝う";
        }
        var node = _w.Nodes.Where(n => n.Amount > 0 && System.Numerics.Vector2.Distance(n.Pos, tile) < 1.2f)
            .OrderBy(n => System.Numerics.Vector2.Distance(n.Pos, tile)).FirstOrDefault();
        bool tree = _w.Map.In((int)tile.X, (int)tile.Y) && _w.Map.At((int)tile.X, (int)tile.Y) == Tile.Forest;
        if (node != null || tree)
        {
            var res = node?.Kind ?? Res.Wood;
            foreach (var v in villagers)
            {
                Release(v);
                v.Job = JobKind.Gather;
                v.JobRes = res;
                v.JobTarget = node?.Id ?? 0;
                v.JobTile = tree ? ((int)tile.X, (int)tile.Y) : (-1, -1);
                T.Gather[res]++;
            }
            return $"村人{villagers.Count}人: {region}で{Defs.Ja(res)}を集める(割り当て {Defs.Ja(res)}{T.Gather[res]})";
        }
        foreach (var v in villagers)
        {
            Release(v);
            v.ManualTarget = _w.Map.NearestPassable(tile);
        }
        return $"村人{villagers.Count}人: {region}へ移動して待機";
    }

    // ------------------------------------------------------------ 担当LLM

    public string ApplyOfficer(Operation op, ToolCall call)
    {
        switch (call.Name)
        {
            case "report":
                op.Status = Str(call.Input, "status") ?? op.Status;
                op.StatusAt = _w.Time;
                T.Version++;
                return "";
            case "adjust_role":
            {
                var role = op.FindRole(Str(call.Input, "role") ?? "");
                if (role == null) return "";
                var problems = new List<string>();
                var patch = ReadRule(call.Input, problems);
                if (problems.Count > 0 || patch.IsEmpty) return "";
                if (role.Apply(patch, _w.Time))
                {
                    op.Log(_w.Time, $"部隊長の判断: {Str(call.Input, "reason") ?? role.Name + "を調整"}");
                    T.Version++;
                }
                return "";
            }
        }
        return "";
    }
}
