using System.Collections.Generic;

namespace LlmCommander.Sim;

public enum UnitKind { Villager, Spearman, Archer, Cavalry, Scout }

public enum BuildingKind { TownCenter, Barracks, Storehouse }

public enum Res { Food, Wood, Gold }

public sealed record UnitStats(
    int Hp, float Speed, int Attack, float Range, float Cooldown, int Armor,
    int Food, int Wood, int Gold, float TrainTime, float Radius);

public sealed record BuildingStats(int Hp, int Size, int Wood, int Gold, float BuildTime);

/// <summary>ゲームの数値。単位は距離がタイル、時間が秒。</summary>
public static class Defs
{
    public const float Dt = 1f / 20f;
    public const int PopCap = 100;
    public const float Carry = 20f;
    public const float GatherRate = 1.0f;
    public const float Sight = 7f;

    /// <summary>ユニットの視界(タイル)。斥候だけ広い。</summary>
    public static float SightOf(UnitKind k) => k == UnitKind.Scout ? 10f : Sight;
    public const float MeleeRange = 0.7f;

    public static readonly Dictionary<UnitKind, UnitStats> Units = new()
    {
        [UnitKind.Villager] = new(25, 0.9f, 3, MeleeRange, 2.0f, 0, 50, 0, 0, 12f, 0.3f),
        [UnitKind.Spearman] = new(50, 1.0f, 4, MeleeRange, 2.0f, 1, 35, 25, 0, 15f, 0.32f),
        [UnitKind.Archer] = new(32, 0.95f, 4, 4.5f, 2.0f, 0, 0, 25, 45, 18f, 0.3f),
        [UnitKind.Cavalry] = new(100, 1.6f, 8, MeleeRange, 1.8f, 2, 60, 0, 75, 20f, 0.42f),
        // 斥候: 速くて視界が広いが、戦いには弱い
        [UnitKind.Scout] = new(45, 2.0f, 3, MeleeRange, 2.0f, 0, 80, 0, 0, 15f, 0.42f),
    };

    public static readonly Dictionary<BuildingKind, BuildingStats> Buildings = new()
    {
        [BuildingKind.TownCenter] = new(2400, 3, 275, 100, 60f),
        [BuildingKind.Barracks] = new(1200, 3, 175, 0, 30f),
        // 採集所: 村人が食・木・金を置ける小さな建物。遠い資源のそばに建てて往復を短くする
        [BuildingKind.Storehouse] = new(600, 2, 100, 0, 20f),
    };

    /// <summary>三すくみの追加ダメージ。槍→騎兵、騎兵→弓、弓→槍。</summary>
    public static int Bonus(UnitKind attacker, UnitKind target) => (attacker, target) switch
    {
        (UnitKind.Spearman, UnitKind.Cavalry) => 14,
        (UnitKind.Spearman, UnitKind.Scout) => 8,
        (UnitKind.Cavalry, UnitKind.Archer) => 6,
        (UnitKind.Archer, UnitKind.Spearman) => 3,
        _ => 0,
    };

    public static BuildingKind Producer(UnitKind k) =>
        k == UnitKind.Villager ? BuildingKind.TownCenter : BuildingKind.Barracks;

    public static string Ja(UnitKind k) => k switch
    {
        UnitKind.Villager => "村人",
        UnitKind.Spearman => "槍",
        UnitKind.Archer => "弓",
        UnitKind.Cavalry => "騎兵",
        UnitKind.Scout => "斥候",
        _ => k.ToString(),
    };

    public static string Ja(BuildingKind k) => k switch
    {
        BuildingKind.TownCenter => "村の中心",
        BuildingKind.Barracks => "兵舎",
        BuildingKind.Storehouse => "採集所",
        _ => k.ToString(),
    };

    public static string Ja(Res r) => r switch { Res.Food => "食", Res.Wood => "木", Res.Gold => "金", _ => r.ToString() };

    /// <summary>LLMとのやり取りで使う英語名。</summary>
    public static string Key(UnitKind k) => k.ToString().ToLowerInvariant();
    public static string Key(BuildingKind k) => k switch
    {
        BuildingKind.TownCenter => "town_center",
        BuildingKind.Barracks => "barracks",
        _ => "storehouse",
    };

    /// <summary>村人が資源を置ける建物か。</summary>
    public static bool IsDropoff(BuildingKind k) => k is BuildingKind.TownCenter or BuildingKind.Storehouse;
    public static string Key(Res r) => r.ToString().ToLowerInvariant();

    public static bool TryParseUnit(string? s, out UnitKind kind)
    {
        kind = default;
        switch (s?.Trim().ToLowerInvariant())
        {
            case "villager" or "村人": kind = UnitKind.Villager; return true;
            case "spearman" or "spear" or "槍" or "槍兵": kind = UnitKind.Spearman; return true;
            case "archer" or "弓" or "弓兵": kind = UnitKind.Archer; return true;
            case "cavalry" or "騎兵": kind = UnitKind.Cavalry; return true;
            case "scout" or "斥候" or "偵察兵": kind = UnitKind.Scout; return true;
        }
        return false;
    }

    public static bool TryParseBuilding(string? s, out BuildingKind kind)
    {
        kind = default;
        switch (s?.Trim().ToLowerInvariant())
        {
            case "town_center" or "towncenter" or "村の中心": kind = BuildingKind.TownCenter; return true;
            case "barracks" or "兵舎": kind = BuildingKind.Barracks; return true;
            case "storehouse" or "camp" or "mill" or "lumber_camp" or "mining_camp" or "採集所" or "倉庫" or "採掘所" or "伐採所" or "製粉所":
                kind = BuildingKind.Storehouse; return true;
        }
        return false;
    }

    public static bool TryParseRes(string? s, out Res r)
    {
        r = default;
        switch (s?.Trim().ToLowerInvariant())
        {
            case "food" or "食" or "食料": r = Res.Food; return true;
            case "wood" or "木" or "木材": r = Res.Wood; return true;
            case "gold" or "金": r = Res.Gold; return true;
        }
        return false;
    }
}
