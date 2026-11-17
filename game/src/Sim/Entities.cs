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
    public Anim Anim;
    public float AnimTime;
    public float LastFoughtAt = -999;
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
}

public sealed class ResourceNode
{
    public int Id;
    public Res Kind;
    public Vector2 Pos;
    public float Amount;
    public float Radius => Kind == Res.Gold ? 0.8f : 0.5f;
}
