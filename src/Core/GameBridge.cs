using System;

namespace Vendetta
{
    /// <summary>Position type for the core logic, so it doesn't depend on Unity. The Unity layer converts to and from Vector3.</summary>
    public struct Vec3
    {
        public float X, Y, Z;
        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static float Distance(Vec3 a, Vec3 b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        public override string ToString() { return "(" + X + ", " + Y + ", " + Z + ")"; }
    }

    /// <summary>Where someone is. The adapter decides how to tell these apart (trigger volumes, height, scene area, ...).</summary>
    public enum Zone { Field, Wall, Courtyard, Mine } // Courtyard = inside the walls, including the stairs

    public interface IGamePlayer
    {
        /// <summary>Stable across sessions (Steam ID ideally) so a Nemesis can find the same dwarf in a later run.</summary>
        string Id { get; }
        string Name { get; }
        bool IsDowned { get; }
        float MaxHealth { get; }
        Vec3 Position { get; }
        Zone Zone { get; }
    }

    public interface IGameMonster
    {
        /// <summary>Unique while the monster exists (Unity instance ID is fine).</summary>
        int Id { get; }
        /// <summary>The kind of monster, e.g. "GOBLIN". Saved with a Nemesis so the same kind can return.</summary>
        string TypeName { get; }
        bool IsAlive { get; }
        float MaxHealth { get; }
        Vec3 Position { get; }
        Zone Zone { get; }
    }

    /// <summary>
    /// Everything Vendetta needs to DO to the game. The Stonewards implementation
    /// (StonewardsBridge) is the only code that touches the game's classes, so when
    /// the game updates, that one file is all that needs fixing.
    /// </summary>
    public interface IGameBridge
    {
        bool IsHost { get; }
        /// <summary>Every frame on the host, before the director's own work: moves a leaping monster.</summary>
        void Tick(float dt);

        /// <summary>Make the monster chase and attack this player instead of its usual target.</summary>
        void ForceTarget(IGameMonster monster, IGamePlayer player);
        void SetAttackInterval(IGameMonster monster, float seconds);
        void Heal(IGameMonster monster, float amount);
        /// <summary>For Nemeses: bigger model, more HP.</summary>
        void Empower(IGameMonster monster, float sizeMultiplier, float healthMultiplier);

        /// <summary>True when the monster has got as close as it can but can't walk to its target. Only asked when one of them is inside the walls or on top.</summary>
        bool NeedsLeap(IGameMonster monster, IGamePlayer target);
        /// <summary>The same for a spot: it has stopped and can't walk the rest of the way.</summary>
        bool NeedsLeapTo(IGameMonster monster, Vec3 point);
        bool CanWalkTo(IGameMonster monster, Vec3 point);
        /// <summary>It can attack the player from where it is.</summary>
        bool CanHit(IGameMonster monster, IGamePlayer player);
        /// <summary>The foot of the wall on the field side, near the monster.</summary>
        bool TryFindWallPoint(IGameMonster monster, out Vec3 point);
        /// <summary>It's there and free to act, or it can't get any closer.</summary>
        bool HasReached(IGameMonster monster, Vec3 point);
        /// <summary>Crouch before the leap.</summary>
        void PlayTelegraph(IGameMonster monster, float seconds);
        void CancelTelegraph(IGameMonster monster);
        /// <summary>Starts the jump. False if it can't right now (it's being knocked back).</summary>
        bool Leap(IGameMonster monster, Vec3 destination);
        bool IsLeaping(IGameMonster monster);
        /// <summary>A walkable point near this one with some room between them. False if there isn't one.</summary>
        bool TryFindLanding(IGameMonster monster, Vec3 near, out Vec3 point);

        /// <summary>A walkable point just outside the cave entrance. False if there isn't one.</summary>
        bool TryFindMineEntrance(out Vec3 point);
        /// <summary>Make the monster go to this spot and hold it. fight: it takes on anyone who comes close.</summary>
        void Guard(IGameMonster monster, Vec3 point, bool fight);
        /// <summary>Make the monster go for the King.</summary>
        void HuntKing(IGameMonster monster);
        bool TryFindKing(out Vec3 position);
        /// <summary>Hand target choice back to the game's AI.</summary>
        void ReleaseTarget(IGameMonster monster);

        void GrantOre(IGamePlayer player, int amount);
        void LoseRun();
    }
}
