using System.Collections.Generic;
using System.Numerics;

namespace LlmCommander.Sim;

public abstract class Entity
{
    public int Id;
    public int Team;
    public Vector2 Pos;
    public float Hp;
    public int MaxHp;
    public bool Alive => Hp > 0;
    public abstract float Radius { get; }
    public float LastHitAt = -999;
    public int LastAttacker;
}

public enum Anim { Idle, Walk, Attack }

public enum JobKind { None, Gather, Build }

public sealed class Unit : Entity
{
    public UnitKind Kind;
    public override float Radius => Defs.Units[Kind].Radius;
    public UnitStats Stats => Defs.Units[Kind];

    public float Cooldown;
    public List<Vector2>? Path;
    public int PathIndex;
    public Vector2 PathGoal = new(float.NaN, float.NaN);
    public float RepathAt;

    /// <summary>所属している作戦と役割。</summary>
    public int OpId;
    public string Role = "";

    // 村人の仕事
    public JobKind Job;
    public Res JobRes;
    public float Carrying;
    public Res CarryingRes;
    public int JobTarget;          // 資源ノード・建物の Id
    public (int X, int Y) JobTile; // 木のタイル
    public bool Returning;

    // 表示用
    public Vector2 Facing = new(1, 0);

    /// <summary>直前のステップでの位置。描画でステップの間を補間するために使う。</summary>
    public Vector2 PrevPos;

    /// <summary>描画用の位置。alpha は直前のステップから次のステップまでの経過割合(0..1)。</summary>
    public Vector2 RenderPos(float alpha) => Vector2.Lerp(PrevPos, Pos, alpha);

    public Anim Anim;
    public float AnimTime;
    public float LastFoughtAt = -999;
    /// <summary>逃げている間の期限と、逃げている相手(離れきるまで逃げ続ける)。</summary>
    public float FleeUntil = -999;
    public int FleeFrom;
    /// <summary>巡回の向き(1=右回り、-1=左回り)。敵がいる側を避けて向きを変える。</summary>
    public int PatrolDir = 1;
    /// <summary>追いかけている相手と、追い始めた時刻・距離。追いつけない相手は諦める。</summary>
    public int ChaseId;
    public float ChaseSince;
    public float ChaseStartDist;
    /// <summary>諦めた相手。期限まで狙わない。</summary>
    public int IgnoreId;
    public float IgnoreUntil = -999;

    /// <summary>探索中の次の行き先と、それを決めた時刻。</summary>
    public Vector2? ExploreGoal;
    public float ExploreAt;
    /// <summary>こもっている建物の Id(0 なら外にいる)。</summary>
    public int Inside;
    /// <summary>こもりに向かっている建物の Id。</summary>
    public int GarrisonTarget;
    /// <summary>直接指示で移動して待つ場所(村人用)。</summary>
    public Vector2? ManualTarget;
    public bool IsInside => Inside != 0;
}

public sealed class Building : Entity
{
    public BuildingKind Kind;
    public int TileX, TileY;
    public int Size => Defs.Buildings[Kind].Size;
    public override float Radius => Size * 0.5f;
    public float Progress = 1f;
    public bool Done => Progress >= 1f;
    public readonly Queue<UnitKind> Queue = new();
    public float QueueTimer;
    public int PlanId;
    public float ArrowCooldown;
}

public sealed class ResourceNode
{
    public int Id;
    public Res Kind;
    public Vector2 Pos;
    public float Amount;
    public float Radius => Kind == Res.Gold ? 0.8f : 0.5f;
}
