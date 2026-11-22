using System;
using System.Numerics;

namespace LlmCommander.Sim;

/// <summary>
/// 固定マップ「丘陵の谷」。80×80タイル、プレイヤーは画面の左(西)、敵は右(東)。
/// (x, y) と (y, x) を入れ替えると画面が左右反転するので、その対称性で公平にしている。
/// </summary>
public static class MapLayout
{
    public const int Size = 80;

    public static World CreateValley()
    {
        var map = new GameMap(Size, Size);
        var world = new World(map);

        void Region(string key, string name, float x, float y, float r) =>
            map.Regions.Add(new Region(key, name, new Vector2(x, y), r));

        // 対になる地名を両側に置く
        // 本拠地の場所が地名からわからないよう、どちらも「平地」と呼ぶ(自軍の基地は own_base / 自軍基地 で指す)
        Region("west_base", "西の平地", 16.5f, 64.5f, 9);
        Region("east_base", "東の平地", 64.5f, 16.5f, 9);
        Region("center", "中央", 40, 40, 7);
        Region("north_hill", "北の丘", 22, 22, 6);
        Region("south_hill", "南の丘", 58, 58, 6);
        Region("north_woods", "北の林", 12, 12, 7);
        Region("south_woods", "南の林", 68, 68, 7);
        Region("west_woods", "西の林", 5, 72, 7);
        Region("east_woods", "東の林", 72, 5, 7);
        Region("northwest_woods", "北西の林", 24, 42, 4);
        Region("northeast_woods", "北東の林", 42, 24, 4);
        Region("southwest_woods", "南西の林", 40, 58, 4);
        Region("southeast_woods", "南東の林", 58, 40, 4);
        Region("north_gold", "北の金鉱", 27, 27, 4);
        Region("south_gold", "南の金鉱", 53, 53, 4);
        Region("west_gold", "西の金鉱", 22, 71, 4);
        Region("east_gold", "東の金鉱", 71, 22, 4);

        void Sym(Action<int, int> f, int x, int y)
        {
            f(x, y);
            f(y, x);
        }

        var rng = new Random(7);
        void Blob(float cx, float cy, float rx, float ry, float density = 0.92f)
        {
            for (int y = (int)(cy - ry); y <= (int)(cy + ry); y++)
            for (int x = (int)(cx - rx); x <= (int)(cx + rx); x++)
            {
                float dx = (x + 0.5f - cx) / rx, dy = (y + 0.5f - cy) / ry;
                if (dx * dx + dy * dy > 1) continue;
                if (rng.NextDouble() > density) continue;
                Sym((a, b) => map.SetForest(a, b), x, y);
            }
        }

        // 外周の森(背後の林)
        for (int i = 0; i < Size; i++)
        for (int d = 0; d < 3; d++)
            Sym((a, b) => map.SetForest(a, b), i, d); // 上側の縁(と左側の縁)
        Blob(5, 72, 5, 8);
        Blob(10, 77, 9, 3);
        Blob(12, 12, 6, 6, 0.85f);
        Blob(68, 68, 6, 6, 0.85f);
        Blob(24, 42, 3, 3, 0.8f);
        Blob(40, 58, 3, 3, 0.8f);
        // 右下の縁も森にする
        for (int i = 0; i < Size; i++)
        for (int d = Size - 2; d < Size; d++)
            Sym((a, b) => map.SetForest(a, b), i, d);

        // 中央の池
        for (int y = 36; y <= 44; y++)
        for (int x = 36; x <= 44; x++)
        {
            float dx = x + 0.5f - 40, dy = y + 0.5f - 40;
            if (dx * dx + dy * dy < 7.5f) map.Tiles[y * map.W + x] = Tile.Water;
        }

        // 基地のまわりは空けておく
        void Clear(float cx, float cy, float r)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                if (dx * dx + dy * dy < r * r && map.At(x, y) == Tile.Forest) map.Tiles[y * map.W + x] = Tile.Grass;
            }
        }
        Clear(16.5f, 64.5f, 8);
        Clear(64.5f, 16.5f, 8);

        // 資源
        void Nodes(Res r, float x, float y, int n, float amount)
        {
            for (int i = 0; i < n; i++)
            {
                float a = i * 2.1f;
                float rr = i == 0 ? 0 : 1.1f;
                world.AddNode(r, new Vector2(x + MathF.Cos(a) * rr, y + MathF.Sin(a) * rr), amount);
                world.AddNode(r, new Vector2(y + MathF.Sin(a) * rr, x + MathF.Cos(a) * rr), amount);
            }
        }
        Nodes(Res.Gold, 22, 71, 4, 800);   // 西の金鉱 / 東の金鉱
        Nodes(Res.Food, 10, 59, 5, 300);   // 自陣の木の実
        void Cluster(Res r, float x, float y, int n, float amount)
        {
            for (int i = 0; i < n; i++)
            {
                float a = i * 2.1f;
                float rr = i == 0 ? 0 : 1.1f;
                world.AddNode(r, new Vector2(x + MathF.Cos(a) * rr, y + MathF.Sin(a) * rr), amount);
            }
        }
        Cluster(Res.Gold, 27, 27, 5, 800); // 北の金鉱(対角線上なので1か所)
        Cluster(Res.Gold, 53, 53, 5, 800); // 南の金鉱
        Nodes(Res.Food, 30, 50, 4, 300);   // 中間の木の実

        // 初期配置
        for (int team = 0; team < 2; team++)
        {
            var c = team == 0 ? new Vector2(16.5f, 64.5f) : new Vector2(64.5f, 16.5f);
            world.SpawnBuilding(team, BuildingKind.TownCenter, (int)c.X - 1, (int)c.Y - 1);
            var ts = world.Teams[team];
            var toCenter = Vector2.Normalize(new Vector2(40, 40) - c);
            ts.Rally = map.NearestPassable(c + toCenter * 6);
            for (int i = 0; i < 6; i++)
                world.SpawnUnit(team, UnitKind.Villager, c + new Vector2(-2.5f + i % 3, 2.5f + i / 3));
            // 斥候を1騎ずつ持って始まる
            world.SpawnUnit(team, UnitKind.Scout, c + toCenter * 3);
        }
        world.UpdateVision();
        return world;
    }
}
