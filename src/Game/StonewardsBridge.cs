using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace Vendetta.Game
{
    public class PlayerHandle : IGamePlayer
    {
        public readonly Component C;
        public PlayerHandle(Component c) { C = c; }

        public string Id
        {
            get
            {
                var id = Reflect.Get(C, GameNames.PlayerSteamId);
                return id != null ? id.ToString() : Name;
            }
        }
        public string Name
        {
            get
            {
                var n = Reflect.Get(C, GameNames.PlayerName);
                return n != null && n.ToString().Length > 0 ? n.ToString() : (C ? C.gameObject.name : "?");
            }
        }
        public bool IsDowned
        {
            get
            {
                if (!C) return true;
                if (Reflect.Has(C, GameNames.PlayerIsDowned)) return Reflect.GetBool(C, GameNames.PlayerIsDowned, false);
                return Reflect.GetFloat(C, GameNames.PlayerHealth, 1f) <= 0f;
            }
        }
        public float MaxHealth { get { return Reflect.GetFloat(C, GameNames.PlayerMaxHealth, 100f); } }
        public Vec3 Position { get { return C ? StonewardsBridge.ToVec(C.transform.position) : new Vec3(); } }
        public Zone Zone { get { return StonewardsBridge.ZoneOf(Position); } }
        public float Health { get { return Reflect.GetFloat(C, GameNames.PlayerHealth, 0f); } }
        public bool IsBlocking { get { return Reflect.GetBool(C, GameNames.PlayerIsBlocking, false); } }
        public bool IsLocal { get { return Reflect.GetBool(C, GameNames.PlayerIsLocal, false); } }
    }

    public class MonsterHandle : IGameMonster
    {
        public readonly Component C;
        private readonly int _id;
        public float Health { get { return Reflect.GetFloat(C, GameNames.MonsterHealth, 0f); } }
        public MonsterHandle(Component c) { C = c; _id = c.GetInstanceID(); }

        public int Id { get { return _id; } }
        public string TypeName
        {
            get
            {
                var id = C ? Reflect.Get(C, GameNames.MonsterTypeId) : null;
                return id != null ? id.ToString() : (C ? C.GetType().Name : "?");
            }
        }
        public bool IsAlive
        {
            get
            {
                if (!C) return false;
                if (Reflect.Has(C, GameNames.MonsterIsDead)) return !Reflect.GetBool(C, GameNames.MonsterIsDead, false);
                return Reflect.GetFloat(C, GameNames.MonsterHealth, 1f) > 0f;
            }
        }
        public float MaxHealth { get { return Reflect.GetFloat(C, GameNames.MonsterMaxHealth, 100f); } }
        public Vec3 Position { get { return C ? StonewardsBridge.ToVec(C.transform.position) : new Vec3(); } }
        public Zone Zone { get { return StonewardsBridge.ZoneOf(Position); } }
    }

    /// <summary>
    /// The only place Vendetta touches Stonewards. Everything is looked up by the
    /// names in GameNames, so most fixes after a game update are one string.
    /// </summary>
    public class StonewardsBridge : IGameBridge
    {
        private readonly ManualLogSource _log;
        private readonly Dictionary<int, PlayerHandle> _players = new Dictionary<int, PlayerHandle>();
        private readonly Dictionary<int, MonsterHandle> _monsters = new Dictionary<int, MonsterHandle>();
        private readonly Dictionary<int, StuckInfo> _stuck = new Dictionary<int, StuckInfo>();
        private readonly HashSet<string> _warned = new HashSet<string>();
        private readonly System.Random _rng = new System.Random();

        public Type PlayerType { get; private set; }
        public Type MonsterType { get; private set; }

        private struct StuckInfo { public Vector3 Pos; public float Since; }

        public StonewardsBridge(ManualLogSource log)
        {
            _log = log;
            _wallLog = log;
            PlayerType = Reflect.FindType(GameNames.PlayerClass);
            MonsterType = Reflect.FindType(GameNames.MonsterClass);
            if (PlayerType == null) _log.LogWarning("GameNames.PlayerClass not found: '" + GameNames.PlayerClass + "'");
            if (MonsterType == null) _log.LogWarning("GameNames.MonsterClass not found: '" + GameNames.MonsterClass + "'");
        }

        public bool IsReady { get { return PlayerType != null && MonsterType != null; } }

        // ---------------- Finding players and monsters ----------------

        public IList<IGamePlayer> GetPlayers()
        {
            var list = new List<IGamePlayer>();
            if (PlayerType == null) return list;
            foreach (var o in UnityEngine.Object.FindObjectsByType(PlayerType, FindObjectsSortMode.None))
            {
                var p = WrapPlayer(o as Component);
                if (p != null) list.Add(p);
            }
            return list;
        }

        public PlayerHandle LocalPlayer
        {
            get
            {
                foreach (PlayerHandle p in GetPlayers()) if (p.IsLocal) return p;
                return null;
            }
        }

        /// <summary>Accepts the player component, its GameObject, or a Transform/child of it.</summary>
        public PlayerHandle ResolvePlayer(object o) { return WrapPlayer(FindComponent(o, PlayerType)); }
        public MonsterHandle ResolveMonster(object o) { return WrapMonster(FindComponent(o, MonsterType)); }

        private PlayerHandle WrapPlayer(Component c)
        {
            if (!c) return null;
            PlayerHandle h;
            int id = c.GetInstanceID();
            if (!_players.TryGetValue(id, out h)) { h = new PlayerHandle(c); _players[id] = h; }
            return h;
        }

        private MonsterHandle WrapMonster(Component c)
        {
            if (!c) return null;
            MonsterHandle h;
            int id = c.GetInstanceID();
            if (!_monsters.TryGetValue(id, out h)) { h = new MonsterHandle(c); _monsters[id] = h; }
            return h;
        }

        private static Component FindComponent(object o, Type type)
        {
            if (o == null || type == null) return null;
            if (type.IsInstanceOfType(o)) return (Component)o;
            var go = o as GameObject;
            if (go) return go.GetComponentInParent(type);
            var comp = o as Component;
            if (comp) return comp.GetComponentInParent(type);
            return null;
        }

        /// <summary>Forget destroyed objects (call between runs).</summary>
        public void Prune()
        {
            var deadP = new List<int>();
            foreach (var kv in _players) if (!kv.Value.C) deadP.Add(kv.Key);
            foreach (var k in deadP) _players.Remove(k);
            var deadM = new List<int>();
            foreach (var kv in _monsters) if (!kv.Value.C) deadM.Add(kv.Key);
            foreach (var k in deadM) { _monsters.Remove(k); _stuck.Remove(k); }
        }

        // ---------------- IGameBridge ----------------

        public bool IsHost
        {
            get
            {
                if (string.IsNullOrEmpty(GameNames.HostCheck)) return true; // until found: single-player / testing
                object v = ReadStaticOrSingleton(GameNames.HostCheck);
                return v is bool ? (bool)v : true;
            }
        }

        /// <summary>0 when not in a Steam lobby.</summary>
        public ulong LobbyId
        {
            get
            {
                var lobby = ReadStaticOrSingleton(GameNames.Lobby) as UnityEngine.Object;
                object id = lobby ? Reflect.Get(lobby, GameNames.LobbyId) : null;
                return id is ulong ? (ulong)id : 0;
            }
        }

        public uint NetIdOf(IGameMonster monster)
        {
            var m = M(monster);
            object id = m != null ? Reflect.Get(m.C, GameNames.NetId) : null;
            return id is uint ? (uint)id : 0;
        }

        /// <summary>A networked object on this player's computer. Null if it isn't here (any more).</summary>
        public Component FindByNetId(uint netId)
        {
            var spawned = netId != 0 ? ReadStaticOrSingleton(GameNames.SpawnedObjects) as System.Collections.IDictionary : null;
            var found = spawned != null && spawned.Contains(netId) ? spawned[netId] as Component : null;
            return found ? found : null;
        }

        public void ForceTarget(IGameMonster monster, IGamePlayer player)
        {
            var m = M(monster); var p = P(player);
            if (m == null || p == null || !CanHunt(p)) return;
            var behaviour = Reflect.Get(m.C, GameNames.MonsterBehaviour);
            if (behaviour == null) { WarnOnce("ForceTarget", "GameNames.MonsterBehaviour"); return; }
            Reflect.Set(behaviour, GameNames.BehaviourLastAttacker, p.C);
            if (ReferenceEquals(Reflect.Get(behaviour, GameNames.BehaviourTargetPlayer), p.C)) return;
            var target = MakeTarget(behaviour, p);
            var set = Reflect.FindMethod(GameNames.BehaviourSetTarget);
            if (target == null || set == null) { WarnOnce("ForceTarget", "GameNames.BehaviourMakeTarget or BehaviourSetTarget"); return; }
            set.Invoke(behaviour, new[] { target });
        }

        /// <summary>
        /// The game's AI drops players who are dead or in the caves, and ground monsters can't path
        /// in there, so the monster goes back to its normal targets until its dwarf comes out.
        /// </summary>
        public bool CanHunt(PlayerHandle p) { return p != null && p.C && !p.IsDowned && p.Zone != Zone.Mine; }

        /// <summary>The monster a behaviour object drives.</summary>
        public MonsterHandle MonsterOf(object behaviour) { return ResolveMonster(Reflect.Get(behaviour, GameNames.BehaviourController)); }

        /// <summary>The game's own target value (an EnemyTarget struct) for this player.</summary>
        public object MakeTarget(object behaviour, PlayerHandle p) { return Reflect.Call(behaviour, GameNames.BehaviourMakeTarget, p.C); }

        /// <summary>True if this EnemyTarget is a player or companion.</summary>
        public bool IsSomeone(object enemyTarget)
        {
            var type = Reflect.Get(enemyTarget, GameNames.TargetType);
            string t = type != null ? type.ToString() : "";
            return t == "PLAYER" || t == "COMPANION";
        }

        /// <summary>A wall target moved to this point: the AI walks there, and still turns on anyone in its aggro range.</summary>
        public object MakeGuardTarget(object behaviour, Vec3 point)
        {
            var target = Reflect.Call(behaviour, GameNames.BehaviourMakeWallTarget);
            return target != null && Reflect.Set(target, GameNames.TargetPosition, ToVector(point)) ? target : null; // sets it in our boxed copy
        }

        public void Guard(IGameMonster monster, Vec3 point, bool fight)
        {
            var m = M(monster);
            var behaviour = m != null ? Reflect.Get(m.C, GameNames.MonsterBehaviour) : null;
            var set = Reflect.FindMethod(GameNames.BehaviourSetTarget);
            if (behaviour == null || set == null) return;
            var current = Reflect.Get(behaviour, GameNames.BehaviourTarget);
            if (fight && IsFightable(current)) return;
            var at = Reflect.Get(current, GameNames.TargetPosition);
            if (!IsSomeone(current) && at is Vector3 && Vec3.Distance(ToVec((Vector3)at), point) < 0.5f) return; // already guarding
            var target = MakeGuardTarget(behaviour, point);
            if (target == null) { WarnOnce("Guard", "GameNames.BehaviourMakeWallTarget or TargetPosition"); return; }
            set.Invoke(behaviour, new[] { target });
        }

        public object MakeKingTarget(object behaviour)
        {
            Vec3 unused;
            return TryFindKing(out unused) ? Reflect.Call(behaviour, GameNames.BehaviourMakeKingTarget) : null;
        }

        public bool TryFindKing(out Vec3 position)
        {
            var king = ReadStaticOrSingleton(GameNames.King) as Component;
            position = king ? ToVec(king.transform.position) : new Vec3();
            return king;
        }

        public void HuntKing(IGameMonster monster)
        {
            var m = M(monster);
            var behaviour = m != null ? Reflect.Get(m.C, GameNames.MonsterBehaviour) : null;
            var set = Reflect.FindMethod(GameNames.BehaviourSetTarget);
            var type = Reflect.Get(Reflect.Get(behaviour, GameNames.BehaviourTarget), GameNames.TargetType);
            if (set == null || type == null || type.ToString() == "KING") return;
            var target = MakeKingTarget(behaviour);
            if (target != null) set.Invoke(behaviour, new[] { target });
        }

        public void ReleaseTarget(IGameMonster monster)
        {
            var m = M(monster);
            var behaviour = m != null ? Reflect.Get(m.C, GameNames.MonsterBehaviour) : null;
            var set = Reflect.FindMethod(GameNames.BehaviourSetTarget);
            if (behaviour == null || set == null) return;
            var target = Reflect.Call(behaviour, GameNames.BehaviourPickTarget);
            if (target != null) set.Invoke(behaviour, new[] { target });
        }

        public bool TryFindMineEntrance(out Vec3 point)
        {
            point = new Vec3();
            object entrance = ReadStaticOrSingleton(GameNames.CaveEntrance);
            if (!(entrance is Vector3)) return false;
            Vector3 at = (Vector3)entrance;
            foreach (float step in new[] { 4f, 7f, 10f, 14f, 18f }) // the caves are towards -X
            foreach (float side in new[] { 0f, 4f, -4f, 8f, -8f })
            {
                Vector3 onMesh;
                if (!SampleNavMesh(at + new Vector3(step, 0f, side), 4f, out onMesh)) continue;
                if (ZoneOf(ToVec(onMesh)) == Zone.Mine || Math.Abs(onMesh.y - at.y) > 4f) continue; // not inside, not on the hill above it
                point = ToVec(onMesh);
                _log.LogInfo("Mine wait: mine entrance " + Show(ToVec(at)) + ", waiting at " + Show(point));
                return true;
            }
            _log.LogInfo("Mine wait: no walkable spot near the mine entrance " + Show(ToVec(at)) + ", so the monster stays where it is");
            return false;
        }

        private float _nextLookAround;

        /// <summary>
        /// Walks a monster to its spot and keeps it there. False if the game's AI should run instead: it's
        /// fighting someone who came close (if fight is on), or finishing an attack.
        /// </summary>
        public bool Hold(MonsterHandle m, object behaviour, Vec3 point, bool fight, Vec3? lookAt)
        {
            if (IsTrue(Reflect.Call(behaviour, GameNames.BehaviourIsBusy))) return false;
            if (fight && IsFightable(Reflect.Get(behaviour, GameNames.BehaviourTarget))) return false;

            if (fight && Time.time >= _nextLookAround) // the game's AI looks for targets once a second too
            {
                _nextLookAround = Time.time + 1f;
                var seen = Reflect.Call(behaviour, GameNames.BehaviourPickTarget);
                var set = Reflect.FindMethod(GameNames.BehaviourSetTarget);
                if (set != null && IsFightable(seen)) { set.Invoke(behaviour, new[] { seen }); return false; }
            }

            Vector3 at = m.C.transform.position, to = ToVector(point);
            if (new Vector3(to.x - at.x, 0f, to.z - at.z).magnitude < 1.5f) Reflect.Call(m.C, GameNames.MonsterStop);
            else if (IsTrue(Reflect.Call(behaviour, GameNames.BehaviourCanWalk))) Reflect.Call(m.C, GameNames.MonsterMoveTo, to);
            Face(m, lookAt);
            return true;
        }

        /// <summary>Turns it the way it's walking, or towards lookAt when it stands. The game only does this in the AI that Hold replaces.</summary>
        private static void Face(MonsterHandle m, Vec3? lookAt)
        {
            var agent = NavMeshAgentType != null ? m.C.GetComponent(NavMeshAgentType) : null;
            if (!agent) return; // wall climbers turn by themselves
            object velocity = Reflect.Get(agent, "velocity");
            Vector3 way = velocity is Vector3 ? (Vector3)velocity : new Vector3();
            way.y = 0f;
            if (way.magnitude < 0.2f)
            {
                if (!lookAt.HasValue) return;
                way = ToVector(lookAt.Value) - m.C.transform.position;
                way.y = 0f;
                if (way.magnitude < 0.1f) return;
            }
            m.C.transform.rotation = Quaternion.Slerp(m.C.transform.rotation, Quaternion.LookRotation(way), Math.Min(1f, Time.deltaTime * 10f));
        }

        /// <summary>A companion or a dwarf it can reach, so not the one hiding in the mines.</summary>
        private bool IsFightable(object enemyTarget)
        {
            if (!IsSomeone(enemyTarget)) return false;
            var player = Reflect.Get(enemyTarget, GameNames.TargetPlayer) as Component;
            return !player || CanHunt(WrapPlayer(player));
        }

        private static bool IsTrue(object o) { return o is bool && (bool)o; }

        // Attack stats this mod copied, so it only copies once. Weak, so dead monsters' copies are freed.
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> _ownStats =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();
        private static readonly MethodInfo CloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);

        public void SetAttackInterval(IGameMonster monster, float seconds)
        {
            var m = M(monster);
            if (m == null) return;
            var behaviour = Reflect.Get(m.C, GameNames.MonsterBehaviour);
            var stats = Reflect.Get(behaviour, GameNames.BehaviourAttackStats);
            if (stats == null) { WarnOnce("SetAttackInterval", "GameNames.BehaviourAttackStats"); return; }
            object unused;
            if (!_ownStats.TryGetValue(stats, out unused))
            {
                stats = CloneMethod.Invoke(stats, null);
                if (!Reflect.Set(behaviour, GameNames.BehaviourAttackStats, stats)) { WarnOnce("SetAttackInterval", "GameNames.BehaviourAttackStats"); return; }
                _ownStats.Add(stats, stats);
            }
            if (!Reflect.Set(stats, GameNames.AttackStatsInterval, seconds)) WarnOnce("SetAttackInterval", "GameNames.AttackStatsInterval");
        }

        public void Heal(IGameMonster monster, float amount)
        {
            var m = M(monster);
            if (m == null) return;
            float hp = Reflect.GetFloat(m.C, GameNames.MonsterHealth, -1f);
            if (hp < 0f) { WarnOnce("Heal", "GameNames.MonsterHealth"); return; }
            Reflect.Set(m.C, GameNames.MonsterHealth, Math.Min(m.MaxHealth, hp + amount));
        }

        public void Empower(IGameMonster monster, float sizeMultiplier, float healthMultiplier)
        {
            var m = M(monster);
            if (m == null) return;
            m.C.transform.localScale = m.C.transform.localScale * sizeMultiplier;
            float max = Reflect.GetFloat(m.C, GameNames.MonsterMaxHealth, -1f);
            float hp = Reflect.GetFloat(m.C, GameNames.MonsterHealth, -1f);
            if (max > 0f) Reflect.Set(m.C, GameNames.MonsterMaxHealth, max * healthMultiplier);
            if (hp > 0f) Reflect.Set(m.C, GameNames.MonsterHealth, hp * healthMultiplier);
        }

        private const float LeapReach = 12f;      // leaps as soon as it's this close to a dwarf it can't walk to
        private const float StuckLeapReach = 45f; // once it's at the wall or has stopped, from this far too: the courtyard is deep
        private const float NearWall = 8f;

        public bool NeedsLeap(IGameMonster monster, IGamePlayer target)
        {
            var m = M(monster); var p = P(target);
            if (m == null || p == null) return false;
            Vector3 mp = m.C.transform.position, tp = p.C.transform.position;
            bool stopped = HasStopped(m);
            float away = new Vector3(tp.x - mp.x, 0f, tp.z - mp.z).magnitude;

            string why;
            bool leap = false;
            if (!Reflect.GetBool(m.C, GameNames.MonsterCanMove, true)) why = "can't move now";
            else if (IsKnockedBack(m)) why = "being knocked back";
            else if (away < 10f && tp.y - mp.y > 2.5f && stopped && ZoneOf(ToVec(mp)) == Zone.Field) { leap = true; why = "stopped below its dwarf"; }
            else if (CanHit(m, p)) why = "can attack from where it is"; // archers keep their distance
            else if (away > StuckLeapReach) why = "too far away";
            else if (away > LeapReach && !stopped && !IsAtWall(mp)) why = "getting closer";
            else if (CanWalkTo(m, tp)) why = "can walk there";
            else if (stopped || HasAgent(m)) { leap = true; why = "can't walk there"; } // wall climbers get time to climb
            else why = "climbing";

            NoteLeap(m, why, away, tp.y - mp.y, stopped);
            return leap;
        }

        public bool CanHit(IGameMonster monster, IGamePlayer player)
        {
            var m = M(monster); var p = P(player);
            if (m == null || p == null) return false;
            var behaviour = Reflect.Get(m.C, GameNames.MonsterBehaviour);
            if (!ReferenceEquals(Reflect.Get(behaviour, GameNames.BehaviourTargetPlayer), p.C)) return false;
            object inRange = Reflect.Call(behaviour, GameNames.BehaviourInRange);
            return IsTrue(inRange);
        }

        // Finding a path is slow, so the answer is kept for half a second.
        private int _pathMonster;
        private Vector3 _pathPoint;
        private float _pathUntil;
        private bool _pathFound;

        private bool CanWalkTo(MonsterHandle m, Vector3 point)
        {
            if (_pathMonster == m.Id && Time.time < _pathUntil && (point - _pathPoint).magnitude < 3f) return _pathFound;
            object found = Reflect.Call(m.C, GameNames.MonsterFindPath, point, null);
            _pathFound = IsTrue(found);
            _pathMonster = m.Id;
            _pathPoint = point;
            _pathUntil = Time.time + 0.5f;
            return _pathFound;
        }

        public bool CanWalkTo(IGameMonster monster, Vec3 point)
        {
            var m = M(monster);
            return m != null && CanWalkTo(m, ToVector(point));
        }

        public bool TryFindWallPoint(IGameMonster monster, out Vec3 point)
        {
            point = new Vec3();
            var m = M(monster);
            var behaviour = m != null ? Reflect.Get(m.C, GameNames.MonsterBehaviour) : null;
            var at = Reflect.Get(Reflect.Call(behaviour, GameNames.BehaviourMakeWallTarget), GameNames.TargetPosition);
            Vector3 onMesh;
            if (!(at is Vector3) || !SampleNavMesh((Vector3)at, 5f, out onMesh) || ZoneOf(ToVec(onMesh)) != Zone.Field) return false;
            point = ToVec(onMesh);
            _log.LogInfo("Going to the wall at " + Show(point) + " from " + Show(m.Position));
            return true;
        }

        public bool HasReached(IGameMonster monster, Vec3 point)
        {
            var m = M(monster);
            if (m == null || !Reflect.GetBool(m.C, GameNames.MonsterCanMove, true) || IsKnockedBack(m)) return false;
            float still = StoppedFor(m);
            Vector3 at = m.C.transform.position;
            float away = new Vector3(point.X - at.x, 0f, point.Z - at.z).magnitude;
            return away < 4f || (away < 12f && still > 1.5f) || still > 6f; // a crowd at the gate can keep it back
        }

        private static bool HasAgent(MonsterHandle m) { return NavMeshAgentType != null && m.C.GetComponent(NavMeshAgentType); }

        private string _leapNote;
        private float _nextLeapNote;

        /// <summary>One log line when the reason changes, at most every 3 s.</summary>
        private void NoteLeap(MonsterHandle m, string why, float away, float above, bool stopped)
        {
            if (why == _leapNote || Time.time < _nextLeapNote) return;
            _leapNote = why;
            _nextLeapNote = Time.time + 3f;
            _log.LogInfo("Leap check (" + m.TypeName + "): " + why + ". Dwarf " + away.ToString("0.0") + " m away, " + above.ToString("0.0")
                         + " m higher" + (stopped ? ", monster standing still" : ""));
        }

        private bool HasStopped(MonsterHandle m) { return StoppedFor(m) > 1.5f; }

        private float StoppedFor(MonsterHandle m)
        {
            Vector3 mp = m.C.transform.position;
            StuckInfo info;
            if (!_stuck.TryGetValue(m.Id, out info) || (mp - info.Pos).magnitude > 0.5f)
            {
                info = new StuckInfo { Pos = mp, Since = Time.time };
                _stuck[m.Id] = info;
            }
            return Time.time - info.Since;
        }

        private static bool IsAtWall(Vector3 p)
        {
            var wall = GetWall();
            return wall != null && p.x < wall.OuterX + NearWall;
        }

        private static bool IsKnockedBack(MonsterHandle m)
        {
            var agent = NavMeshAgentType != null ? m.C.GetComponent(NavMeshAgentType) : null;
            return agent && !Reflect.GetBool(agent, "enabled", true);
        }

        public bool NeedsLeapTo(IGameMonster monster, Vec3 point)
        {
            var m = M(monster);
            if (m == null || !Reflect.GetBool(m.C, GameNames.MonsterCanMove, true) || IsKnockedBack(m)) return false;
            return HasStopped(m) && !CanWalkTo(m, ToVector(point));
        }

        private LeapMotion _leap; // there's only one vendetta monster

        public void Tick(float dt)
        {
            if (_leap == null) return;
            _leap.Tick(dt);
            if (_leap.Done) _leap = null;
        }

        private LeapMotion NewLeap(MonsterHandle m, float crouchSeconds)
        {
            if (_leap != null) _leap.Cancel();
            return new LeapMotion(m.C, crouchSeconds, () => m.IsAlive, at =>
            {
                Vec3 wanted = ToVec(at);
                Place(m, at);
                _pathUntil = 0f;
                _leapNote = null;
                _log.LogInfo("Leap: landed at " + Show(m.Position)
                             + (Vec3.Distance(m.Position, wanted) > 1.5f ? ", but should be at " + Show(wanted) : ""));
            });
        }

        public void PlayTelegraph(IGameMonster monster, float seconds)
        {
            var m = M(monster);
            if (m != null) _leap = NewLeap(m, seconds);
        }

        public void CancelTelegraph(IGameMonster monster)
        {
            var m = M(monster);
            if (m == null || _leap == null || _leap.C != m.C) return;
            _leap.Cancel();
            if (_leap.Done) _leap = null;
        }

        public bool Leap(IGameMonster monster, Vec3 destination)
        {
            var m = M(monster);
            if (m == null || !m.IsAlive) return true;
            if (_leap == null || _leap.C != m.C) _leap = NewLeap(m, 0f);
            if (!_leap.CanJump) return false;
            Reflect.Call(m.C, GameNames.MonsterStop);
            _log.LogInfo("Leap: " + Show(m.Position) + " (" + m.Zone + ") -> " + Show(destination) + " (" + ZoneOf(destination) + ")");
            _leap.Jump(ToVector(destination));
            return true;
        }

        public bool IsLeaping(IGameMonster monster)
        {
            var m = M(monster);
            return m != null && _leap != null && _leap.C == m.C && _leap.Moving;
        }

        public bool IsInAir(object controller) { return _leap != null && _leap.InAir && ReferenceEquals(_leap.C, controller); }

        /// <summary>True if this monster is crouching, in the air or landing, so the game's AI shouldn't move it.</summary>
        public bool LeapRules(object behaviour)
        {
            if (_leap == null || !_leap.C || !ReferenceEquals(Reflect.Get(_leap.C, GameNames.MonsterBehaviour), behaviour)) return false;
            if (_leap.Moving) return true;
            if (IsTrue(Reflect.Call(behaviour, GameNames.BehaviourIsBusy))) return false; // finishing an attack
            Reflect.Call(_leap.C, GameNames.MonsterStop);
            return true;
        }

        private void Place(MonsterHandle m, Vector3 at)
        {
            // Monsters on a NavMesh must be warped, or the agent snaps them back.
            var agent = NavMeshAgentType != null ? m.C.GetComponent(NavMeshAgentType) : null;
            var warp = agent ? NavMeshAgentType.GetMethod("Warp", new[] { typeof(Vector3) }) : null;
            if (warp != null) warp.Invoke(agent, new object[] { at });
            else m.C.transform.position = at;
            _stuck.Remove(m.Id);
        }

        private const float LandingRoom = 3f; // at least this far from the dwarf

        public bool TryFindLanding(IGameMonster monster, Vec3 near, out Vec3 point)
        {
            point = new Vec3();
            var m = M(monster);
            if (m == null) return false;
            Vector3 origin = ToVector(near), start = m.C.transform.position;
            bool up = IsUp(ZoneOf(ToVec(origin)));
            bool found = false;
            foreach (float ring in new[] { 4f, 5.5f, 7.5f, 11f, 16f, 2f }) // 2: only if there's nowhere with more room
            {
                bool walkable = false;
                float best = 0f;
                for (int i = 0; i < 16; i++)
                {
                    double angle = i * Math.PI / 8;
                    Vector3 onMesh;
                    if (!SampleNavMesh(origin + new Vector3((float)Math.Cos(angle) * ring, 0f, (float)Math.Sin(angle) * ring), 2.5f, out onMesh)) continue;
                    var zone = ZoneOf(ToVec(onMesh));
                    if (zone == Zone.Mine || IsUp(zone) != up) continue;
                    float room = new Vector3(onMesh.x - origin.x, 0f, onMesh.z - origin.z).magnitude;
                    if (room < (ring < LandingRoom ? 1f : LandingRoom)) continue;
                    bool canWalk = HasPath(onMesh, origin);
                    if (found && !canWalk) continue;            // an unreachable spot is only a last resort
                    float jump = (onMesh - start).magnitude;
                    if (walkable && jump >= best) continue;     // the shortest jump, so it lands on its own side
                    found = true;
                    walkable = canWalk;
                    best = jump;
                    point = ToVec(onMesh);
                }
                if (walkable) return true;
            }
            if (!found) _log.LogInfo("Leap failed: no walkable spot near " + Show(ToVec(origin)) + " (" + ZoneOf(ToVec(origin)) + ")");
            return found;
        }

        private static bool IsUp(Zone z) { return z == Zone.Wall || z == Zone.Courtyard; }

        private static string Show(Vec3 v) { return "(" + v.X.ToString("0.0") + ", " + v.Y.ToString("0.0") + ", " + v.Z.ToString("0.0") + ")"; }

        /// <summary>Adds the ore item to the player's inventory and drops whatever doesn't fit at their feet.</summary>
        public void GrantOre(IGamePlayer player, int amount)
        {
            var p = P(player);
            if (p == null || amount <= 0) return;
            try
            {
                var items = ReadStaticOrSingleton(GameNames.ItemManager);
                var item = Reflect.Call(items, GameNames.ItemById, GameNames.OreItemId);
                var inventory = Reflect.Get(p.C, GameNames.PlayerInventory);
                if (item == null || inventory == null) { WarnOnce("GrantOre", "GameNames.OreItemId or PlayerInventory"); return; }
                object left = Reflect.Call(inventory, GameNames.InventoryAdd, item, amount);
                int rest = left is int ? (int)left : 0;
                if (rest > 0)
                    Reflect.Call(items, GameNames.ItemDrop, GameNames.OreItemId, rest, p.C.transform.position + Vector3.up, true, false);
            }
            catch (Exception e) { WarnOnce("GrantOre (" + e.GetBaseException().Message + ")", "the ore item"); } // a reward must never break the kill
        }

        public void LoseRun()
        {
            var method = Reflect.FindMethod(GameNames.LoseRun);
            if (method == null) { WarnOnce("LoseRun", "GameNames.LoseRun"); return; }
            object instance = method.IsStatic ? null : FindSingleton(method.DeclaringType);
            var ps = method.GetParameters();
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
            method.Invoke(instance, args);
        }

        // ---------------- Helpers ----------------

        public static Vec3 ToVec(Vector3 v) { return new Vec3(v.x, v.y, v.z); }
        public static Vector3 ToVector(Vec3 v) { return new Vector3(v.X, v.Y, v.Z); }

        public static Zone ZoneOf(Vec3 p)
        {
            if (string.IsNullOrEmpty(GameNames.CaveEntrance))
            {
                if (p.Y < GameNames.MineBelowY) return Zone.Mine;
            }
            else
            {
                object entrance = ReadStaticOrSingleton(GameNames.CaveEntrance);
                if (entrance is Vector3 && p.X < ((Vector3)entrance).x + 1f) return Zone.Mine; // the game's own rule
            }
            var wall = GetWall();
            if (p.Y > GameNames.WallAboveY)
            {
                if (wall == null) return Zone.Wall;
                return p.X > wall.OuterX ? Zone.Field : p.X >= wall.WalkX ? Zone.Wall : Zone.Courtyard; // high ground outside is still the field
            }
            if (wall != null)
                foreach (var gate in wall.Gates) // the lower part of the stairs, behind a gate
                    if (p.X < gate.X && p.Z > gate.FromZ && p.Z < gate.ToZ) return Zone.Courtyard;
            return Zone.Field;
        }

        private class WallLayout
        {
            public Component Wall;
            public float WalkX = float.MaxValue; // the top of the wall starts here, coming from the courtyard
            public float OuterX = float.MinValue; // beyond this it's the field
            public readonly List<GateSpan> Gates = new List<GateSpan>();
        }
        private struct GateSpan { public float X, FromZ, ToZ; }

        private static WallLayout _wall;
        private static float _nextWallSearch;
        private static ManualLogSource _wallLog;

        /// <summary>Read once per level, while the wall still stands (a destroyed wall's boxes are switched off). Null if it wasn't found.</summary>
        private static WallLayout GetWall()
        {
            if (_wall != null && _wall.Wall) return _wall;
            if (Time.time < _nextWallSearch) return null;
            _nextWallSearch = Time.time + 1f;
            _wall = null;
            var c = ReadStaticOrSingleton(GameNames.Wall) as Component;
            var boxes = c ? Reflect.Get(c, GameNames.WallHitBoxes) as System.Collections.IEnumerable : null;
            if (boxes == null) return null;
            var w = new WallLayout { Wall = c };
            foreach (object box in boxes)
            {
                object b = Reflect.Get(box, "bounds");
                if (!(b is Bounds) || ((Bounds)b).size.z <= 0f) continue;
                var bounds = (Bounds)b;
                w.Gates.Add(new GateSpan { X = bounds.center.x, FromZ = bounds.min.z - 1f, ToZ = bounds.max.z + 1f });
                w.OuterX = Math.Max(w.OuterX, bounds.max.x + 4f);
                w.WalkX = Math.Min(w.WalkX, bounds.min.x - 3f);
            }
            var walk = c.transform.Find(GameNames.WallWalkObject);
            for (int i = 0; walk && i < walk.childCount; i++)
            {
                float x = walk.GetChild(i).position.x;
                w.WalkX = Math.Min(w.WalkX, x - 2.5f);
                w.OuterX = Math.Max(w.OuterX, x + 6f);
            }
            if (w.Gates.Count == 0) return null;
            _wall = w;
            if (_wallLog != null) _wallLog.LogInfo("Wall found: top from X " + w.WalkX.ToString("0.0") + " to " + w.OuterX.ToString("0.0") + ", " + w.Gates.Count + " gate(s)" + (walk ? "" : ", no walkway object"));
            return w;
        }

        private static PlayerHandle P(IGamePlayer p) { var h = p as PlayerHandle; return h != null && h.C ? h : null; }
        private static MonsterHandle M(IGameMonster m) { var h = m as MonsterHandle; return h != null && h.C ? h : null; }

        private static object FindSingleton(Type type)
        {
            var found = UnityEngine.Object.FindObjectsByType(type, FindObjectsSortMode.None);
            return found.Length > 0 ? found[0] : null;
        }

        /// <summary>"Class:Member" or "Class:Member.Path" where Member is a static field/property/method, or an instance member on the scene's only object of that class.</summary>
        private static object ReadStaticOrSingleton(string classColonMember)
        {
            int i = classColonMember.LastIndexOf(':');
            if (i <= 0) return null;
            var type = Reflect.FindType(classColonMember.Substring(0, i));
            if (type == null) return null;
            string name = classColonMember.Substring(i + 1);
            int dot = name.IndexOf('.');
            if (dot > 0)
            {
                object first = ReadStaticOrSingleton(classColonMember.Substring(0, i + 1) + name.Substring(0, dot));
                var unityObject = first as UnityEngine.Object;
                return first is UnityEngine.Object && !unityObject ? null : Reflect.Get(first, name.Substring(dot + 1)); // destroyed counts as missing
            }
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            var method = type.GetMethod(name, any, null, Type.EmptyTypes, null);
            object target = null;
            bool isStatic = method != null ? method.IsStatic
                : (type.GetField(name, any) != null && type.GetField(name, any).IsStatic)
                  || (type.GetProperty(name, any) != null && type.GetProperty(name, any).GetGetMethod(true).IsStatic);
            if (!isStatic) { target = FindSingleton(type); if (target == null) return null; }
            if (method != null) return method.Invoke(target, null);
            var field = type.GetField(name, any);
            if (field != null) return field.GetValue(target);
            var prop = type.GetProperty(name, any);
            return prop != null ? prop.GetValue(target, null) : null;
        }

        private static Type _navAgentType, _navMeshType;
        private static Type NavMeshAgentType
        {
            get { return _navAgentType ?? (_navAgentType = Reflect.FindType("UnityEngine.AI.NavMeshAgent")); }
        }

        private static bool SampleNavMesh(Vector3 source, float maxDistance, out Vector3 result)
        {
            result = source;
            if (_navMeshType == null) _navMeshType = Reflect.FindType("UnityEngine.AI.NavMesh");
            var hitType = Reflect.FindType("UnityEngine.AI.NavMeshHit");
            if (_navMeshType == null || hitType == null) return true; // no navmesh module: accept the guess
            var sample = _navMeshType.GetMethod("SamplePosition",
                new[] { typeof(Vector3), hitType.MakeByRefType(), typeof(float), typeof(int) });
            if (sample == null) return true;
            var args = new object[] { source, Activator.CreateInstance(hitType), maxDistance, -1 };
            if (!(bool)sample.Invoke(null, args)) return false;
            result = (Vector3)hitType.GetProperty("position").GetValue(args[1], null);
            return true;
        }

        /// <summary>Can something walk from here to there? True if the game's navmesh isn't available.</summary>
        private static bool HasPath(Vector3 from, Vector3 to)
        {
            var pathType = Reflect.FindType("UnityEngine.AI.NavMeshPath");
            if (_navMeshType == null || pathType == null) return true;
            var calculate = _navMeshType.GetMethod("CalculatePath", new[] { typeof(Vector3), typeof(Vector3), typeof(int), pathType });
            Vector3 end;
            if (calculate == null) return true;
            if (!SampleNavMesh(to, 3f, out end)) return false;
            object path = Activator.CreateInstance(pathType);
            if (!(bool)calculate.Invoke(null, new[] { (object)from, end, -1, path })) return false;
            var status = Reflect.Get(path, "status");
            return status != null && status.ToString() == "PathComplete";
        }

        private void WarnOnce(string key, string what)
        {
            if (_warned.Add(key)) _log.LogWarning("Vendetta: " + key + " skipped, " + what + " isn't set or wasn't found.");
        }
    }
}
