using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace Vendetta.Game
{
    /// <summary>
    /// Harmony patches that feed game events into the director. Each hook attaches
    /// by its GameNames entry. A missing name or method only switches that hook off,
    /// and every hook catches its own errors, so Vendetta can't crash the game.
    /// </summary>
    public static class GameHooks
    {
        public class HookStatus { public string Name; public string Target; public bool Attached; }

        private static StonewardsBridge _bridge;
        private static VendettaDirector _director;
        private static ManualLogSource _log;
        private static readonly HashSet<string> _errored = new HashSet<string>();

        public static int WaveNumber { get; private set; }

        public static List<HookStatus> Apply(Harmony harmony, ManualLogSource log, StonewardsBridge bridge, VendettaDirector director)
        {
            _bridge = bridge; _director = director; _log = log;
            var status = new List<HookStatus>();
            Hook(harmony, status, "Run started", GameNames.RunStarted, null, "RunStarted_Post");
            Hook(harmony, status, "Run ended", GameNames.RunEnded, null, "RunEnded_Post");
            Hook(harmony, status, "Run left", GameNames.RunLeft, "RunLeft_Pre", null);
            Hook(harmony, status, "Wave started", GameNames.WaveStarted, null, "WaveStarted_Post");
            Hook(harmony, status, "Wave ended", GameNames.WaveEnded, null, "WaveEnded_Post");
            Hook(harmony, status, "Monster spawned", GameNames.MonsterSpawned, null, "MonsterSpawned_Post");
            Hook(harmony, status, "Player takes damage", GameNames.PlayerTakeDamage, "PlayerTakeDamage_Pre", "PlayerTakeDamage_Post");
            Hook(harmony, status, "Monster takes damage", GameNames.MonsterTakeDamage, "MonsterTakeDamage_Pre", "MonsterTakeDamage_Post");
            Hook(harmony, status, "Monster picks target", GameNames.BehaviourSetTarget, "SetTarget_Pre", null);
            Hook(harmony, status, "Monster thinks", GameNames.BehaviourExecute, "Execute_Pre", null);
            Hook(harmony, status, "Exploder thinks", GameNames.BehaviourExecuteExploder, "Execute_Pre", null);
            Hook(harmony, status, "Monster knocked back", GameNames.MonsterKnockback, "Knockback_Pre", null);
            Hook(harmony, status, "Monster died", GameNames.MonsterDied, null, "MonsterDied_Post");
            Hook(harmony, status, "Player downed", GameNames.PlayerDowned, null, "PlayerDowned_Post");
            Hook(harmony, status, "Body on revive spot", GameNames.RevivePlaced, null, "RevivePlaced_Post");
            Hook(harmony, status, "Player revived", GameNames.PlayerRevived, null, "PlayerRevived_Post");
            Hook(harmony, status, "Ore mined", GameNames.OreMined, null, "OreMined_Post");
            return status;
        }

        private static void Hook(Harmony harmony, List<HookStatus> status, string label, string target, string prefix, string postfix)
        {
            var s = new HookStatus { Name = label, Target = string.IsNullOrEmpty(target) ? "(not set)" : target };
            status.Add(s);
            if (string.IsNullOrEmpty(target)) return;
            var original = Reflect.FindMethod(target);
            if (original == null) { _log.LogWarning("Vendetta hook '" + label + "': method not found: " + target); return; }
            try
            {
                harmony.Patch(original,
                    prefix: prefix != null ? new HarmonyMethod(typeof(GameHooks).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)) : null,
                    postfix: postfix != null ? new HarmonyMethod(typeof(GameHooks).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic)) : null);
                s.Attached = true;
                _log.LogInfo("Vendetta hook '" + label + "' attached to " + target);
            }
            catch (Exception e) { _log.LogError("Vendetta hook '" + label + "' failed to attach: " + e.Message); }
        }

        // ---------------- Patch bodies ----------------

        private static void RunStarted_Post()
        {
            Safe("RunStarted", () => { WaveNumber = 0; _bridge.Prune(); _director.OnRunStarted(); });
        }

        private static void RunEnded_Post()
        {
            Safe("RunEnded", () => _director.OnRunEnded());
        }

        /// <summary>Prefix, so the monsters and players still exist. After RunEnded this does nothing.</summary>
        private static void RunLeft_Pre()
        {
            Safe("RunLeft", () => _director.OnRunEnded());
        }

        private static void WaveStarted_Post(object[] __args)
        {
            Safe("WaveStarted", () =>
            {
                var types = new List<string>();
                var entries = Arg(__args, GameNames.WaveStarted_MonstersArg) as System.Collections.IEnumerable;
                if (entries != null)
                    foreach (var entry in entries)
                    {
                        var id = Reflect.Get(entry, GameNames.WaveEntryTypeId) as string;
                        if (!string.IsNullOrEmpty(id) && !types.Contains(id)) types.Add(id);
                    }
                WaveNumber++;
                _director.OnWaveStarted(WaveNumber, _bridge.GetPlayers(), types);
            });
        }

        private static void WaveEnded_Post()
        {
            Safe("WaveEnded", () => _director.OnWaveEnded());
        }

        private static void MonsterSpawned_Post(object __instance)
        {
            Safe("MonsterSpawned", () => _director.OnMonsterSpawned(_bridge.ResolveMonster(__instance)));
        }

        // The game decides dodge and block inside its damage method and runs death inside it too. So the
        // prefix notes who is attacking whom, the death hooks read that, and the postfix works out from
        // the health change whether the hit really landed.

        private class Hit
        {
            public PlayerHandle Player;
            public MonsterHandle Monster;
            public string AttackerName = "nobody (no attacker given)"; // for the log
            public float HealthBefore;
            public bool Blocked;
            public bool WasAlive;
            public Hit Outer; // a hit can cause another (damage reflect), so these nest
        }
        private static Hit _playerHit;  // a hit on a player that's being applied right now
        private static Hit _monsterHit; // same, on a monster

        // "ref object __0" (not __args): this Harmony only writes __args back for ref parameters.
        private static void PlayerTakeDamage_Pre(object __instance, ref object __0, object[] __args, out object __state)
        {
            var hit = new Hit { Outer = _playerHit };
            __state = hit;
            _playerHit = hit;
            object damageArg = __0;
            Safe("PlayerTakeDamage", () =>
            {
                hit.Player = _bridge.ResolvePlayer(__instance);
                hit.Monster = _bridge.ResolveMonster(Arg(__args, GameNames.PlayerTakeDamage_SourceArg));
                if (hit.Player == null || hit.Monster == null) return;
                hit.HealthBefore = hit.Player.Health;
                hit.Blocked = hit.Player.IsBlocking;

                float baseDamage;
                if (!ReadDamage(damageArg, out baseDamage)) return;
                float damage = _director.ModifyHitDamage(hit.Monster, hit.Player, hit.Blocked, baseDamage);
                if (Math.Abs(damage - baseDamage) > 0.001f) damageArg = WithDamage(damageArg, damage);
            });
            __0 = damageArg;
        }

        private static void PlayerTakeDamage_Post(object __state)
        {
            var hit = __state as Hit;
            if (hit == null) return;
            _playerHit = hit.Outer;
            Safe("PlayerTakeDamage", () =>
            {
                if (hit.Player == null || hit.Monster == null || hit.HealthBefore <= 0f) return; // not a monster's hit, or already down
                bool damaged = hit.Player.Health < hit.HealthBefore - 0.001f;
                if (hit.Blocked || damaged) _director.OnHitLanded(hit.Monster, hit.Player, hit.Blocked);
                var down = _director.DownedTarget; // in case the downed hook was inlined away (see MonsterTakeDamage_Post)
                if (hit.Player.IsDowned && (down == null || down.Id != hit.Player.Id)) _director.OnPlayerDowned(hit.Player, hit.Monster);
            });
        }

        private static void MonsterTakeDamage_Pre(object __instance, object[] __args, out object __state)
        {
            var hit = new Hit { Outer = _monsterHit };
            __state = hit;
            _monsterHit = hit;
            Safe("MonsterTakeDamage", () =>
            {
                hit.Monster = _bridge.ResolveMonster(__instance);
                var source = Arg(__args, GameNames.MonsterTakeDamage_SourceArg) as UnityEngine.Component;
                hit.Player = _bridge.ResolvePlayer(source);
                if (hit.Player != null) hit.AttackerName = hit.Player.Name;
                else if (source) hit.AttackerName = source.gameObject.name;
                if (hit.Monster == null) return;
                hit.HealthBefore = hit.Monster.Health;
                hit.WasAlive = hit.Monster.IsAlive;
            });
        }

        private static void MonsterTakeDamage_Post(object __state)
        {
            var hit = __state as Hit;
            if (hit == null) return;
            _monsterHit = hit.Outer;
            Safe("MonsterTakeDamage", () =>
            {
                if (hit.Monster == null) return;
                float dealt = hit.HealthBefore - hit.Monster.Health; // what it really lost: bonuses included, overkill not
                if (dealt > 0f) _director.OnMonsterDamaged(hit.Monster, hit.Player, dealt);
                // Mono inlines the game's tiny death method (DeathCo), which skips a hook on it. So look for the death here too.
                if (hit.WasAlive && !hit.Monster.IsAlive) MonsterKilled(hit.Monster, hit);
            });
        }

        /// <summary>Whenever the vendetta monster picks a target, it picks its dwarf (while they can be hunted).</summary>
        private static void SetTarget_Pre(object __instance, ref object __0)
        {
            object target = __0;
            Safe("SetTarget", () =>
            {
                var c = _director.Current;
                if (c == null) return;
                var monster = _bridge.MonsterOf(__instance);
                var dwarf = c.Target as PlayerHandle;
                if (monster == null || monster.Id != c.Monster.Id) return;
                if (c.Target.IsDowned) target = _bridge.MakeKingTarget(__instance) ?? target;
                else if (_bridge.CanHunt(dwarf)) target = _bridge.MakeTarget(__instance, dwarf) ?? target;
                // Its dwarf is in the mines: it goes straight to the entrance. There it fights whoever comes close, but doesn't wander off to the wall or the King.
                else if (c.WaitingAtMine && c.HasWaitPoint && !(c.ArrivedAtMine && _bridge.IsSomeone(target))) target = _bridge.MakeGuardTarget(__instance, c.WaitPoint) ?? target;
            });
            __0 = target;
        }

        private static bool Knockback_Pre(object __instance) { return !_bridge.IsInAir(__instance); }

        /// <summary>Moves a leaping monster, and one walking to the wall or waiting at the mine entrance. False skips the game's AI for this frame.</summary>
        private static bool Execute_Pre(object __instance)
        {
            if (_bridge.LeapRules(__instance)) return false;
            var c = _director.Current;
            if (c == null || !(c.AtWallOnly || (c.WaitingAtMine && c.HasWaitPoint))) return true; // almost always
            bool held = false;
            Safe("Hold", () =>
            {
                var m = c.Monster as MonsterHandle;
                if (m == null || !m.C || !ReferenceEquals(Reflect.Get(m.C, GameNames.MonsterBehaviour), __instance)) return;
                held = c.AtWallOnly ? _bridge.Hold(m, __instance, c.WallPoint, false, c.WallGoal) : _bridge.Hold(m, __instance, c.WaitPoint, c.ArrivedAtMine, null);
            });
            return !held;
        }

        private static void MonsterDied_Post(object __instance)
        {
            Safe("MonsterDied", () =>
            {
                var monster = _bridge.ResolveMonster(__instance);
                bool known = _monsterHit != null && monster != null && _monsterHit.Monster != null && _monsterHit.Monster.Id == monster.Id;
                MonsterKilled(monster, known ? _monsterHit : null);
            });
        }

        private static void MonsterKilled(MonsterHandle monster, Hit hit)
        {
            var c = _director.Current;
            if (c != null && monster != null && monster.Id == c.Monster.Id)
                _log.LogInfo("Vendetta monster (" + monster.TypeName + ") killed by: " + (hit != null ? hit.AttackerName : "unknown, outside the damage hook"));
            _director.OnMonsterKilled(monster, hit != null ? hit.Player : null); // does nothing the second time
        }

        private static void PlayerDowned_Post(object __instance)
        {
            Safe("PlayerDowned", () =>
            {
                var player = _bridge.ResolvePlayer(__instance);
                bool known = _playerHit != null && player != null && _playerHit.Player != null && _playerHit.Player.Id == player.Id;
                _director.OnPlayerDowned(player, known ? _playerHit.Monster : null);
            });
        }

        private static void RevivePlaced_Post(object __instance)
        {
            Safe("RevivePlaced", () => _director.OnRevivePlaced(_bridge.ResolvePlayer(__instance)));
        }

        private static void PlayerRevived_Post(object __instance)
        {
            Safe("PlayerRevived", () => _director.OnPlayerRevived(_bridge.ResolvePlayer(__instance)));
        }

        private static void OreMined_Post(object __instance, object[] __args)
        {
            Safe("OreMined", () =>
            {
                var who = GameNames.OreMined_PlayerArg < 0 ? __instance : Arg(__args, GameNames.OreMined_PlayerArg);
                var amount = Arg(__args, GameNames.OreMined_AmountArg);
                _director.RecordOreMined(_bridge.ResolvePlayer(who), IsNumber(amount) ? Convert.ToSingle(amount) : 1f);
            });
        }

        // ---------------- Helpers ----------------

        private static object Arg(object[] args, int index)
        {
            return args != null && index >= 0 && index < args.Length ? args[index] : null;
        }

        private static bool ReadDamage(object arg, out float damage)
        {
            object n = string.IsNullOrEmpty(GameNames.DamageAmount) ? arg : Reflect.Get(arg, GameNames.DamageAmount);
            damage = IsNumber(n) ? Convert.ToSingle(n) : 0f;
            return IsNumber(n);
        }

        private static object WithDamage(object arg, float damage)
        {
            if (string.IsNullOrEmpty(GameNames.DamageAmount)) return Convert.ChangeType(damage, arg.GetType());
            Reflect.Set(arg, GameNames.DamageAmount, damage); // changes our boxed copy; the hook then hands it back to the game
            return arg;
        }

        private static bool IsNumber(object o)
        {
            return o is float || o is int || o is double || o is long || o is short || o is uint;
        }

        private static void Safe(string hook, Action action)
        {
            try { action(); }
            catch (Exception e)
            {
                if (_errored.Add(hook)) _log.LogError("Vendetta hook " + hook + " error (shown once): " + e);
            }
        }
    }
}
