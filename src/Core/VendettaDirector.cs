using System;
using System.Collections.Generic;
using System.Linq;

namespace Vendetta
{
    /// <summary>What's happening this wave. The Unity layer reads this to draw the marker.</summary>
    public enum LeapGoal { Dwarf, Mine, King }

    public class VendettaState
    {
        public IGameMonster Monster;
        public IGamePlayer Target;
        public string MonsterName;
        public bool IsGreedWave;
        public bool IsNemesis;
        public NemesisPerk Perk;
        public VendettaStacks Stacks;
        public VendettaRegen Regen;

        public float TelegraphRemaining;  // > 0 while winding up a wall leap
        public float LeapCooldown;
        public float MineChaseTimer = -1f; // >= 0 while it's on its way to the mine entrance
        public LeapGoal LeapGoal;          // where the leap being wound up goes
        public bool HuntingKing;           // its dwarf is dead, so it goes for the King
        public bool AtWallOnly;            // it's walking to the foot of the wall, or waiting there
        public bool HasWallPoint;
        public Vec3 WallPoint;
        public Vec3 WallGoal;              // what it wants on the other side
        public float ShelterRemaining = -1f; // how much longer its dwarf is safe inside the walls. -1 = not set yet
        public bool Sheltered;             // that clock is running now
        public bool ShelterAnnounced, ShelterWarned;
        public bool WaitingAtMine;         // its dwarf is underground: it's on its way to the entrance, or there
        public bool ArrivedAtMine;
        public float MineTravelTime;
        public bool HasWaitPoint;          // false if the game has no walkable spot there: it still grows, but stays where it is
        public Vec3 WaitPoint;
        public float MineWaitTimer;        // time since it last grew from waiting
        public int LastAppliedStep = -1;
        public float RetargetTimer;

        public bool DownedItsTarget;      // earns Nemesis status if it survives
        public Zone KillZone;
        public NemesisRecord SourceRecord; // set when this is a returning Nemesis
    }

    /// <summary>
    /// Runs the vendetta from wave to wave. It doesn't use Unity or the game's
    /// classes directly: the game's hooks call the On... methods, the Unity layer
    /// calls Tick every frame, and everything the director does to the game goes
    /// through IGameBridge. Runs on the host only.
    /// </summary>
    public class VendettaDirector
    {
        private const float LeapCooldownSeconds = 6f;
        private const float RetargetInterval = 0.5f;

        private readonly IGameBridge _bridge;
        private readonly Func<VendettaSettings> _settings;
        private readonly Random _rng;
        private readonly VendettaTargeting _targeting;

        public NemesisRoster Roster { get; private set; }
        public VendettaState Current { get; private set; }

        public IGamePlayer DownedTarget { get; private set; }
        public float ReviveRemaining { get; private set; }
        /// <summary>The body is on a revive spot, so the countdown is stopped.</summary>
        public bool ReviveSecured { get; private set; }

        /// <summary>(message, onlyForPlayerId). onlyForPlayerId == null means everyone.</summary>
        public event Action<string, string> Announce;
        /// <summary>Raised when the roster changes and should be saved.</summary>
        public event Action RosterChanged;
        /// <summary>(monsterName, targetName, killedByTarget). Hook trophies here.</summary>
        public event Action<string, string, bool> VendettaSlain;

        private readonly Dictionary<string, PlayerWaveStats> _accumulating = new Dictionary<string, PlayerWaveStats>();
        private IList<IGamePlayer> _players = new List<IGamePlayer>();
        private bool _waveActive;
        private int _wave;
        private bool _runLost;
        private bool _nemesisReturnedThisRun;
        private IList<string> _waveTypes = new List<string>(); // monster types in the current or last wave
        private VendettaState _slainGrudge;                    // downed its target this wave, then was killed

        private IGamePlayer _pendingTarget;
        private bool _pendingGreed;
        private NemesisRecord _pendingNemesis;

        public VendettaDirector(IGameBridge bridge, Func<VendettaSettings> settings, NemesisRoster roster, Random rng = null)
        {
            _bridge = bridge;
            _settings = settings;
            _rng = rng ?? new Random();
            _targeting = new VendettaTargeting(settings, _rng);
            Roster = roster ?? new NemesisRoster();
        }

        // ======================= Run and wave lifecycle =======================

        public void OnRunStarted()
        {
            _targeting.ResetForNewRun();
            _accumulating.Clear();
            _nemesisReturnedThisRun = false;
            _runLost = false;
            _waveActive = false;
            Current = null;
            _waveTypes = new List<string>();
            _slainGrudge = null;
            ClearRevive();
        }

        /// <param name="waveNumber">1-based.</param>
        /// <param name="players">All players in the run.</param>
        /// <param name="monsterTypes">The monster types in this wave.</param>
        public void OnWaveStarted(int waveNumber, IList<IGamePlayer> players, IList<string> monsterTypes = null)
        {
            if (!_bridge.IsHost) return;
            var s = _settings();
            _players = players ?? new List<IGamePlayer>();

            // Reinforcements: the game starts the next wave while monsters from the last one
            // are alive. A vendetta that's still running carries on instead of being dropped.
            bool carriesOn = _waveActive && Current != null && Current.Monster.IsAlive;
            FlushSlainGrudge();
            _waveActive = true;
            _wave = waveNumber;
            _waveTypes = monsterTypes ?? new List<string>();
            if (carriesOn) return;
            Current = null;

            // Last wave's damage and the dig phase's ore become the stats this wave's pick uses.
            var alive = _players.Where(p => !p.IsDowned).ToList();
            var stats = alive.Select(p =>
            {
                PlayerWaveStats acc;
                _accumulating.TryGetValue(p.Id, out acc);
                return new PlayerWaveStats
                {
                    PlayerId = p.Id,
                    DisplayName = p.Name,
                    DamageDealt = acc != null ? acc.DamageDealt : 0f,
                    OreMined = acc != null ? acc.OreMined : 0f
                };
            }).ToList();
            _accumulating.Clear();

            _pendingTarget = null;
            _pendingNemesis = null;
            _pendingGreed = false;
            if (alive.Count == 0) return;

            // A returning Nemesis takes priority over the normal pick, once per run.
            if (s.NemesisEnabled && !_nemesisReturnedThisRun && waveNumber >= s.NemesisEarliestWave)
            {
                var rec = Roster.FindForPlayers(alive.Select(p => p.Id), null);
                if (rec != null)
                {
                    _pendingNemesis = rec;
                    _pendingTarget = alive.First(p => p.Id == rec.TargetPlayerId);
                    return;
                }
            }

            var pick = _targeting.Pick(waveNumber, stats);
            _pendingTarget = alive.First(p => p.Id == pick.Target.PlayerId);
            _pendingGreed = pick.IsGreedWave;
        }

        /// <summary>Call for each monster as it spawns. The first one after a wave starts becomes the vendetta monster.</summary>
        public void OnMonsterSpawned(IGameMonster monster)
        {
            if (!_bridge.IsHost || !_waveActive || Current != null || _pendingTarget == null || monster == null) return;
            if (monster.Zone == Zone.Mine) return; // a cave dweller the players dug up, not part of the wave
            var s = _settings();
            var target = _pendingTarget;
            var rec = _pendingNemesis;

            // A Nemesis returns as its own kind when this wave has any; otherwise the first monster takes the role.
            if (rec != null && _waveTypes.Contains(rec.MonsterType) && monster.TypeName != rec.MonsterType) return;

            var c = new VendettaState
            {
                Monster = monster,
                Target = target,
                IsGreedWave = _pendingGreed,
                IsNemesis = rec != null,
                SourceRecord = rec,
                Perk = rec != null ? rec.Perk : NemesisPerk.None,
                MonsterName = rec != null ? rec.MonsterName : MonsterNames.Generate(_rng),
                Stacks = new VendettaStacks(_settings, rec != null ? rec.CarriedHits : 0f),
                Regen = new VendettaRegen(_settings)
            };
            Current = c;
            _pendingTarget = null;

            if (rec != null)
            {
                _bridge.Empower(monster, 1.3f, rec.Perk == NemesisPerk.ThickHide ? 1.5f : 1.2f);
                rec.TimesReturned++;
                _nemesisReturnedThisRun = true;
                RaiseRosterChanged();
                Say(c.MonsterName + " has returned for you. (" + NemesisRecord.PerkLabel(rec.Perk) + ")", target.Id);
                Say("Nemesis! " + c.MonsterName + " is back for " + target.Name + ".", null);
            }
            else
            {
                Say(c.MonsterName + " has sworn a vendetta against you!", target.Id);
                Say(c.MonsterName + " hunts " + target.Name + (c.IsGreedWave ? " (Greed)" : "") + ".", null);
            }

            // Tick aims it at its target on the next frame: the game is still spawning it, and
            // changing its target now would send a network message from an unspawned object.
            ApplyAttackInterval(c);

            // Greed: if the miner is still underground, it goes straight to the mine entrance.
            if (c.IsGreedWave && target.Zone == Zone.Mine) c.MineChaseTimer = 0f;
        }

        public void OnWaveEnded()
        {
            if (!_bridge.IsHost) return;
            var c = Current;
            if (c != null && c.DownedItsTarget && c.Monster.IsAlive)
                CreateNemesis(c, "escaped");
            FlushSlainGrudge();
            Current = null;
            _waveActive = false;
            _pendingTarget = null;
        }

        public void OnRunEnded()
        {
            var c = Current;
            if (c != null && c.DownedItsTarget && c.Monster.IsAlive)
                CreateNemesis(c, "triumphed");
            FlushSlainGrudge();
            Current = null;
            _waveActive = false;
            ClearRevive();
        }

        // ======================= Stats (for picking targets) =======================

        public void RecordDamageDealt(IGamePlayer player, float amount)
        {
            if (player == null || amount <= 0f) return;
            Acc(player).DamageDealt += amount;
        }

        public void RecordOreMined(IGamePlayer player, float amount)
        {
            if (player == null || amount <= 0f) return;
            Acc(player).OreMined += amount;
        }

        // ======================= Combat =======================

        /// <summary>
        /// Call when a monster's attack reaches a player, before damage is applied. Returns the damage to use.
        /// Hits that don't involve the vendetta pair return baseDamage unchanged, and so do blocked hits
        /// (the game stops all of a blocked hit's damage). Doesn't count the hit: it may still be dodged.
        /// Call OnHitLanded afterwards if it wasn't.
        /// </summary>
        public float ModifyHitDamage(IGameMonster monster, IGamePlayer player, bool blocked, float baseDamage)
        {
            var c = Current;
            if (c == null || monster == null || player == null || monster.Id != c.Monster.Id || player.Id != c.Target.Id)
                return baseDamage;

            return blocked ? baseDamage : c.Stacks.DamageAgainst(player.MaxHealth, false);
        }

        /// <summary>Call after a hit that was blocked or did damage (not one that was dodged). Counts it toward stacks.</summary>
        public void OnHitLanded(IGameMonster monster, IGamePlayer player, bool blocked)
        {
            var c = Current;
            if (c == null || monster == null || player == null || monster.Id != c.Monster.Id || player.Id != c.Target.Id) return;
            Grow(c, () => c.Stacks.RegisterHitOnTarget(blocked));
        }

        private void Grow(VendettaState c, Action add)
        {
            bool wasMax = c.Stacks.Hits >= _settings().MaxHits;
            add();
            ApplyAttackInterval(c);
            if (!wasMax && c.Stacks.Hits >= _settings().MaxHits)
                Say(c.MonsterName + " is in a full fury!", null);
        }

        public void OnMonsterDamaged(IGameMonster monster, IGamePlayer byPlayer, float amount)
        {
            RecordDamageDealt(byPlayer, amount);
            var c = Current;
            if (c != null && monster != null && monster.Id == c.Monster.Id)
                c.Regen.OnDamagedByPlayer();
        }

        public void OnMonsterKilled(IGameMonster monster, IGamePlayer killer)
        {
            var c = Current;
            if (c == null || monster == null || monster.Id != c.Monster.Id) return;
            var s = _settings();
            bool byTarget = killer != null && killer.Id == c.Target.Id;
            bool beforeMax = c.Stacks.Hits < s.MaxHits;

            int killReward = s.RewardOnWave(s.TargetKillBonusOre, _wave);
            int groupReward = s.RewardOnWave(s.GroupKillBonusOre, _wave);
            if (byTarget && killReward > 0)
            {
                _bridge.GrantOre(c.Target, killReward);
                Say(c.Target.Name + " avenged themselves against " + c.MonsterName + "! (+" + killReward + " ore)", null);
            }
            else
            {
                Say(c.MonsterName + " has fallen.", null);
            }

            if (beforeMax && groupReward > 0)
            {
                foreach (var p in _players) _bridge.GrantOre(p, groupReward);
                Say("Slain before its full fury: +" + groupReward + " ore each.", null);
            }

            if (c.IsNemesis && Roster.Remove(c.MonsterName))
            {
                Say(c.MonsterName + "'s grudge is ended.", null);
                RaiseRosterChanged();
            }
            else if (c.DownedItsTarget)
            {
                _slainGrudge = c; // one of its wave takes up the grudge (see FlushSlainGrudge)
            }

            var handler = VendettaSlain;
            if (handler != null) handler(c.MonsterName, c.Target.Name, byTarget);
            Current = null;
        }

        // ======================= Downed / revive =======================

        public void OnPlayerDowned(IGamePlayer player, IGameMonster byMonster)
        {
            var c = Current;
            if (!_bridge.IsHost || c == null || player == null || player.Id != c.Target.Id || _runLost) return;

            if (byMonster != null && byMonster.Id == c.Monster.Id)
            {
                c.DownedItsTarget = true;
                c.KillZone = player.Zone;
            }

            var s = _settings();
            if (s.Loss == LossMode.InstantLoss)
            {
                if (c.DownedItsTarget) CreateNemesis(c, "triumphed");
                LoseRun(player.Name + " has fallen. The stronghold is lost.");
                return;
            }

            DownedTarget = player;
            ReviveSecured = false;
            ReviveRemaining = s.ReviveWindowSeconds;
            Say(player.Name + " is down! Revive them within " + (int)s.ReviveWindowSeconds + "s or the stronghold falls!", null);
        }

        /// <summary>The downed target's body was put on a revive spot: the countdown stops while they come back.</summary>
        public void OnRevivePlaced(IGamePlayer player)
        {
            if (DownedTarget == null || player == null || player.Id != DownedTarget.Id || ReviveSecured) return;
            ReviveSecured = true;
            Say(player.Name + " is being revived. The clock is stopped!", null);
        }

        public void OnPlayerRevived(IGamePlayer player)
        {
            if (DownedTarget == null || player == null || player.Id != DownedTarget.Id) return;
            Say(player.Name + " is back on their feet!", null);
            ClearRevive();
        }

        // ======================= Per-frame =======================

        public void Tick(float dt)
        {
            if (!_bridge.IsHost || dt <= 0f) return;
            var s = _settings();
            _bridge.Tick(dt);

            // The revive countdown keeps running even if the monster dies meanwhile.
            if (DownedTarget != null && !ReviveSecured && !_runLost)
            {
                ReviveRemaining -= dt;
                if (ReviveRemaining <= 0f)
                {
                    var c0 = Current;
                    if (c0 != null && c0.DownedItsTarget) CreateNemesis(c0, "triumphed");
                    LoseRun(DownedTarget.Name + " was not saved. The stronghold is lost.");
                    return;
                }
            }

            var c = Current;
            if (c == null) return;
            if (!c.Monster.IsAlive) { OnMonsterKilled(c.Monster, null); return; } // died without a hook firing
            var target = c.Target;
            var monster = c.Monster;

            // Keep it on its target (the game's own AI may try to switch).
            c.RetargetTimer -= dt;
            if (c.RetargetTimer <= 0f)
            {
                if (target.IsDowned) _bridge.HuntKing(monster);
                else if (c.WaitingAtMine && c.HasWaitPoint) _bridge.Guard(monster, c.WaitPoint, c.ArrivedAtMine);
                else _bridge.ForceTarget(monster, target);
                // Monsters that switch attacks get fresh attack stats, so set this again too.
                // Its slower swings only hit its dwarf harder, so against the King it keeps its first, normal pace.
                _bridge.SetAttackInterval(monster, target.IsDowned ? s.BaseAttackInterval : c.Stacks.CurrentAttackInterval);
                c.RetargetTimer = RetargetInterval;
            }

            // Heal when the group leaves it alone.
            float heal = c.Regen.Tick(dt, monster.MaxHealth);
            if (heal > 0f) _bridge.Heal(monster, heal);

            c.AtWallOnly = false; // set again below while it applies
            if (target.IsDowned != c.HuntingKing) SwitchKingHunt(c, target.IsDowned);
            if (!_bridge.IsLeaping(monster))
            {
                if (!c.HuntingKing) TickMines(c, s, dt);
                TickWallLeap(c, s, dt);
            }
            if (!c.AtWallOnly) c.HasWallPoint = false;
        }

        private static bool IsUp(Zone z) { return z == Zone.Wall || z == Zone.Courtyard; }

        /// <summary>While its dwarf is dead it goes for the King; once they're revived it hunts them again.</summary>
        private void SwitchKingHunt(VendettaState c, bool start)
        {
            c.HuntingKing = start;
            c.Sheltered = false;
            c.RetargetTimer = 0f;
            CancelLeap(c);
            if (start)
            {
                StopWaiting(c, true);
                if (!_runLost) Say(c.MonsterName + " turns on the King!", null);
            }
            else Say(c.MonsterName + " hunts " + c.Target.Name + " again!", null);
        }

        /// <summary>
        /// Sends a monster that can't walk in to the foot of the wall, so it only leaps from there (the game's own
        /// pathfinding stops well short of it). True once it's there.
        /// </summary>
        private bool ReadyAtWall(VendettaState c, Vec3 goal)
        {
            if (c.Monster.Zone != Zone.Field || _bridge.CanWalkTo(c.Monster, goal)) return false;
            if (!c.HasWallPoint && !_bridge.TryFindWallPoint(c.Monster, out c.WallPoint)) return _bridge.NeedsLeapTo(c.Monster, goal);
            c.HasWallPoint = true;
            c.AtWallOnly = true;
            c.WallGoal = goal;
            return _bridge.HasReached(c.Monster, c.WallPoint);
        }

        /// <summary>For the mine entrance and the King: from outside it leaps at the wall, inside only if it's stuck.</summary>
        private bool WantsLeapTo(VendettaState c, Vec3 goal)
        {
            return c.Monster.Zone == Zone.Field ? ReadyAtWall(c, goal) : _bridge.NeedsLeapTo(c.Monster, goal);
        }

        /// <summary>Up onto the wall after its dwarf, across gaps up there, over the wall once the courtyard stops being safe, and back down.</summary>
        private bool WantsLeap(VendettaState c, VendettaSettings s)
        {
            Zone to = c.Target.Zone;
            if (!s.WallJumpEnabled || to == Zone.Mine) return false;
            if (to == Zone.Courtyard && c.Monster.Zone == Zone.Field)
                return !_bridge.CanHit(c.Monster, c.Target) && ReadyAtWall(c, c.Target.Position);
            return (IsUp(to) || IsUp(c.Monster.Zone)) && _bridge.NeedsLeap(c.Monster, c.Target);
        }

        private void TickWallLeap(VendettaState c, VendettaSettings s, float dt)
        {
            if (c.LeapCooldown > 0f) c.LeapCooldown -= dt;

            if (c.TelegraphRemaining > 0f)
            {
                c.TelegraphRemaining -= dt;
                if (c.TelegraphRemaining <= 0f) Jump(c);
                return;
            }

            if (c.HuntingKing)
            {
                Vec3 king;
                if (s.WallJumpEnabled && _bridge.TryFindKing(out king) && WantsLeapTo(c, king) && c.LeapCooldown <= 0f)
                    StartLeap(c, s, LeapGoal.King, " is about to leap at the King!", null);
                return;
            }

            bool wants = WantsLeap(c, s);
            if (StillSheltered(c, s, wants, dt) || !wants || c.LeapCooldown > 0f) return;

            Zone to = c.Target.Zone, from = c.Monster.Zone;
            StartLeap(c, s, LeapGoal.Dwarf, !IsUp(to) ? " is about to leap down after you!"
                : IsUp(from) ? " is about to leap at you!"
                : to == Zone.Wall ? " is about to leap onto the wall!" : " is about to leap over the wall!", c.Target.Id);
        }

        private void StartLeap(VendettaState c, VendettaSettings s, LeapGoal goal, string message, string onlyFor)
        {
            float telegraph = s.WallJumpTelegraphSeconds * (c.Perk == NemesisPerk.WallBreaker ? 0.5f : 1f);
            c.TelegraphRemaining = Math.Max(0.05f, telegraph);
            c.LeapGoal = goal;
            _bridge.PlayTelegraph(c.Monster, c.TelegraphRemaining);
            Say(c.MonsterName + message, onlyFor);
        }

        private void Jump(VendettaState c)
        {
            Vec3 landing = c.WaitPoint;
            Vec3 king;
            bool go = c.LeapGoal == LeapGoal.Mine ? c.WaitingAtMine && c.HasWaitPoint && !c.ArrivedAtMine
                : c.LeapGoal == LeapGoal.King ? _bridge.TryFindKing(out king) && _bridge.TryFindLanding(c.Monster, king, out landing)
                : c.Target.Zone != Zone.Mine && (IsUp(c.Target.Zone) || IsUp(c.Monster.Zone)) && _bridge.TryFindLanding(c.Monster, c.Target.Position, out landing);
            if (go && !_bridge.Leap(c.Monster, landing)) { c.TelegraphRemaining = 0.1f; return; } // knocked back: jumps when it's over
            if (!go) _bridge.CancelTelegraph(c.Monster);
            c.LeapGoal = LeapGoal.Dwarf;
            c.LeapCooldown = LeapCooldownSeconds;
        }

        private void CancelLeap(VendettaState c)
        {
            if (c.TelegraphRemaining > 0f) _bridge.CancelTelegraph(c.Monster);
            c.TelegraphRemaining = 0f;
            c.LeapGoal = LeapGoal.Dwarf;
        }

        /// <summary>The courtyard and stairs are safe from a monster outside, for a while. The clock only runs while it's at the wall, ready to leap in. Shorter every wave.</summary>
        private bool StillSheltered(VendettaState c, VendettaSettings s, bool wants, float dt)
        {
            c.Sheltered = false;
            float full = s.CourtyardSafeOnWave(_wave);
            if (c.ShelterRemaining < 0f || c.ShelterRemaining > full) c.ShelterRemaining = full;
            Zone to = c.Target.Zone;
            if (to == Zone.Wall || to == Zone.Field)
            {
                c.ShelterRemaining = Math.Min(full, c.ShelterRemaining + dt);
                if (c.ShelterRemaining > 6f) c.ShelterWarned = false;
                if (c.ShelterRemaining >= full) c.ShelterAnnounced = false;
            }
            if (!wants || to != Zone.Courtyard || c.Monster.Zone != Zone.Field || c.ShelterRemaining <= 0f) return false;

            c.ShelterRemaining = Math.Max(0f, c.ShelterRemaining - dt);
            c.Sheltered = true;
            if (!c.ShelterAnnounced)
            {
                c.ShelterAnnounced = true;
                Say("The wall shelters you, but " + c.MonsterName + " won't wait for long...", c.Target.Id);
            }
            else if (!c.ShelterWarned && c.ShelterRemaining <= 5f && full > 7f)
            {
                c.ShelterWarned = true;
                Say(c.MonsterName + " is looking for a way over the wall!", c.Target.Id);
            }
            return true;
        }

        private const float MineTravelSeconds = 60f; // if it still hasn't got to the wall by then, it leaps from where it is

        /// <summary>Monsters can't walk in the caves, so it goes to the entrance, over the wall if it has to, and gets stronger the longer its dwarf hides.</summary>
        private void TickMines(VendettaState c, VendettaSettings s, float dt)
        {
            if (s.Mines == MineBehavior.Off || c.Target.Zone != Zone.Mine)
            {
                if (c.WaitingAtMine && c.ArrivedAtMine) Say(c.MonsterName + " has been waiting for you...", c.Target.Id);
                if (c.LeapGoal == LeapGoal.Mine) CancelLeap(c);
                StopWaiting(c, s.Mines == MineBehavior.Off); // it goes after its dwarf again on the next retarget
                return;
            }

            float speed = c.Perk == NemesisPerk.TunnelStalker ? 2f : 1f;
            if (!c.WaitingAtMine)
            {
                if (c.MineChaseTimer < 0f) c.MineChaseTimer = s.MineFollowDelaySeconds / speed;
                c.MineChaseTimer -= dt;
                if (c.MineChaseTimer > 0f) return;

                c.WaitingAtMine = true;
                c.MineWaitTimer = 0f;
                c.MineTravelTime = 0f;
                c.RetargetTimer = 0f; // set off now
                c.HasWaitPoint = _bridge.TryFindMineEntrance(out c.WaitPoint);
                if (!c.HasWaitPoint) ArriveAtMine(c); // nowhere to stand: it still grows, where it is
                else if (!IsThere(c)) Say(c.MonsterName + " is coming to the mine entrance for you!", c.Target.Id);
                return;
            }

            if (!c.ArrivedAtMine)
            {
                c.MineTravelTime += dt * speed;
                bool late = c.MineTravelTime > MineTravelSeconds;
                if (IsThere(c)) ArriveAtMine(c);
                else if (!s.WallJumpEnabled) { if (late) ArriveAtMine(c); } // can't get over the wall
                else if (c.TelegraphRemaining <= 0f && (WantsLeapTo(c, c.WaitPoint) || late) && c.LeapCooldown <= 0f)
                    StartLeap(c, s, LeapGoal.Mine, " is about to leap over the wall to the mine entrance!", c.Target.Id);
                return;
            }

            if (s.MineWaitSecondsPerHit <= 0f) return;
            c.MineWaitTimer += dt * speed;
            while (c.MineWaitTimer >= s.MineWaitSecondsPerHit)
            {
                c.MineWaitTimer -= s.MineWaitSecondsPerHit;
                Grow(c, () => c.Stacks.AddHits(1f));
            }
        }

        private static bool IsThere(VendettaState c) { return Vec3.Distance(c.Monster.Position, c.WaitPoint) < 3f; }

        private void ArriveAtMine(VendettaState c)
        {
            c.ArrivedAtMine = true;
            Say(c.MonsterName + " waits for you at the mine entrance, and its hatred grows...", c.Target.Id);
            Say(c.MonsterName + " is waiting for " + c.Target.Name + " at the mine entrance.", null);
        }

        private void StopWaiting(VendettaState c, bool handBackToGame)
        {
            bool held = c.WaitingAtMine && c.HasWaitPoint;
            c.MineChaseTimer = -1f;
            c.WaitingAtMine = false;
            c.ArrivedAtMine = false;
            c.HasWaitPoint = false;
            if (held) c.RetargetTimer = 0f;
            if (held && handBackToGame) _bridge.ReleaseTarget(c.Monster); // or it would guard that spot for good
        }

        // ======================= Helpers =======================

        private void ApplyAttackInterval(VendettaState c)
        {
            int step = c.Stacks.Step;
            if (step == c.LastAppliedStep) return;
            c.LastAppliedStep = step;
            _bridge.SetAttackInterval(c.Monster, c.Stacks.CurrentAttackInterval);
        }

        /// <summary>
        /// A wave only ends once every monster is dead, so a vendetta monster that downed its target
        /// is usually killed before it can escape. One of its wave takes up the grudge under a new name.
        /// </summary>
        private void FlushSlainGrudge()
        {
            var c = _slainGrudge;
            _slainGrudge = null;
            if (c != null) CreateNemesis(c, null);
        }

        /// <param name="verb">What the surviving monster did, or null if it was slain and another takes its place.</param>
        private void CreateNemesis(VendettaState c, string verb)
        {
            var s = _settings();
            if (!s.NemesisEnabled || c.Stacks == null) return;
            var old = c.SourceRecord;
            // The Nemesis comes back as a random kind of monster from this wave.
            string type = old != null ? old.MonsterType
                : _waveTypes.Count > 0 ? _waveTypes[_rng.Next(_waveTypes.Count)] : c.Monster.TypeName;
            string name = c.MonsterName;
            if (verb == null)
                for (int i = 0; i < 10 && (name == c.MonsterName || Roster.Records.Any(r => r.MonsterName == name)); i++)
                    name = MonsterNames.Generate(_rng);
            var rec = new NemesisRecord
            {
                MonsterName = name,
                MonsterType = type,
                TargetPlayerId = c.Target.Id,
                TargetPlayerName = c.Target.Name,
                CarriedHits = c.Stacks.NemesisCarryOverHits(),
                Perk = old != null ? old.Perk : NemesisRecord.PerkForKillZone(c.KillZone),
                TimesReturned = old != null ? old.TimesReturned : 0,
                CreatedUtcTicks = old != null ? old.CreatedUtcTicks : DateTime.UtcNow.Ticks
            };
            Roster.Add(rec, s.MaxActiveNemeses);
            c.DownedItsTarget = false; // record once
            RaiseRosterChanged();
            Say(verb != null
                ? name + " " + verb + " and will remember " + c.Target.Name + "..."
                : name + " takes up " + c.MonsterName + "'s grudge against " + c.Target.Name + "...", null);
        }

        private void LoseRun(string message)
        {
            _runLost = true;
            ClearRevive();
            Say(message, null);
            _bridge.LoseRun();
        }

        private void ClearRevive()
        {
            DownedTarget = null;
            ReviveSecured = false;
            ReviveRemaining = 0f;
        }

        private PlayerWaveStats Acc(IGamePlayer p)
        {
            PlayerWaveStats acc;
            if (!_accumulating.TryGetValue(p.Id, out acc))
            {
                acc = new PlayerWaveStats { PlayerId = p.Id, DisplayName = p.Name };
                _accumulating[p.Id] = acc;
            }
            return acc;
        }

        private void Say(string message, string onlyForPlayerId)
        {
            var handler = Announce;
            if (handler != null) handler(message, onlyForPlayerId);
        }

        private void RaiseRosterChanged()
        {
            var handler = RosterChanged;
            if (handler != null) handler();
        }
    }
}
