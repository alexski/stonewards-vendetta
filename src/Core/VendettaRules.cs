using System;
using System.Collections.Generic;
using System.Linq;

namespace Vendetta
{
    /// <summary>
    /// The vendetta monster's growth. One instance per vendetta monster.
    /// Call RegisterHitOnTarget when the monster's attack reaches its target,
    /// blocked or not, then read CurrentDamage / CurrentAttackInterval.
    /// </summary>
    public class VendettaStacks
    {
        private readonly Func<VendettaSettings> _settings;
        public float Hits { get; private set; }

        public VendettaStacks(Func<VendettaSettings> settings, float startingHits = 0f)
        {
            _settings = settings;
            Hits = Math.Min(Math.Max(0f, startingHits), settings().MaxHits);
        }

        public void RegisterHitOnTarget(bool blocked)
        {
            var s = _settings();
            Hits = Math.Min(Hits + (blocked ? s.BlockedHitValue : 1f), s.MaxHits);
        }

        /// <summary>Growth that doesn't come from a hit (waiting at the mines).</summary>
        public void AddHits(float hits) { Hits = Math.Min(Hits + Math.Max(0f, hits), _settings().MaxHits); }

        /// <summary>0 at the start, +1 every HitsPerStep hits, up to MaxHits / HitsPerStep.</summary>
        public int Step
        {
            get
            {
                var s = _settings();
                if (s.HitsPerStep <= 0f) return 0;
                // small epsilon so 0.5 + 0.5 + 1 reliably reaches 2.0
                return (int)Math.Floor((Hits + 1e-4f) / s.HitsPerStep);
            }
        }

        public float CurrentDamage
        {
            get
            {
                var s = _settings();
                return Math.Min(s.BaseDamage * (float)Math.Pow(s.DamageGrowthPerStep, Step), s.MaxDamage);
            }
        }

        public float CurrentAttackInterval
        {
            get
            {
                var s = _settings();
                return s.BaseAttackInterval + s.IntervalPerStep * Step;
            }
        }

        /// <summary>Damage to actually apply to the target: 0 if blocked, and never more than the per-hit cap.</summary>
        public float DamageAgainst(float targetMaxHp, bool blocked)
        {
            if (blocked) return 0f;
            float cap = targetMaxHp * _settings().MaxHitPercentOfTargetHp / 100f;
            return Math.Min(CurrentDamage, cap);
        }

        /// <summary>Stacks a Nemesis keeps when it returns in a later run.</summary>
        public float NemesisCarryOverHits()
        {
            return Hits * _settings().NemesisStackCarryOverPercent / 100f;
        }
    }

    /// <summary>
    /// The monster heals once the group stops hitting it. Call OnDamagedByPlayer
    /// whenever any player damages it, and Tick every frame. Tick returns the HP to restore.
    /// </summary>
    public class VendettaRegen
    {
        private readonly Func<VendettaSettings> _settings;
        public float SecondsSinceDamaged { get; private set; }

        public VendettaRegen(Func<VendettaSettings> settings) { _settings = settings; }

        public void OnDamagedByPlayer() { SecondsSinceDamaged = 0f; }

        public float Tick(float deltaTime, float monsterMaxHp)
        {
            var s = _settings();
            float before = SecondsSinceDamaged;
            SecondsSinceDamaged += deltaTime;
            float healingTime = SecondsSinceDamaged - Math.Max(before, s.HealDelaySeconds);
            if (healingTime <= 0f) return 0f;
            return monsterMaxHp * s.HealPercentPerSecond / 100f * healingTime;
        }
    }

    public class PlayerWaveStats
    {
        public string PlayerId;
        public string DisplayName;
        public float DamageDealt; // last wave
        public float OreMined;    // last dig phase
    }

    public struct VendettaPick
    {
        public PlayerWaveStats Target;
        public bool IsGreedWave;
    }

    /// <summary>Chooses who the vendetta monster hunts each wave.</summary>
    public class VendettaTargeting
    {
        private readonly Func<VendettaSettings> _settings;
        private readonly Random _rng;
        private readonly List<string> _history = new List<string>(); // target id per wave, most recent last

        public VendettaTargeting(Func<VendettaSettings> settings, Random rng = null)
        {
            _settings = settings;
            _rng = rng ?? new Random();
        }

        public bool IsGreedWave(int waveNumber)
        {
            int n = _settings().GreedEveryNWaves;
            return n > 0 && waveNumber > 0 && waveNumber % n == 0;
        }

        /// <param name="waveNumber">1-based wave about to start.</param>
        /// <param name="players">Living players with last wave's stats.</param>
        public VendettaPick Pick(int waveNumber, IList<PlayerWaveStats> players)
        {
            if (players == null || players.Count == 0)
                return new VendettaPick();

            var s = _settings();
            bool greed = IsGreedWave(waveNumber);
            List<PlayerWaveStats> ranked;

            if (greed)
                ranked = players.OrderByDescending(p => p.OreMined).ThenBy(_ => _rng.Next()).ToList();
            else if (s.BloodTarget == BloodTargetMode.MostDamage)
                ranked = players.OrderByDescending(p => p.DamageDealt).ThenBy(_ => _rng.Next()).ToList();
            else if (s.BloodTarget == BloodTargetMode.Rotating)
                ranked = RotationOrder(players);
            else
                ranked = players.OrderBy(_ => _rng.Next()).ToList();

            // No stats yet (first wave): choose at random instead of always the first player.
            bool noStats = greed ? players.All(p => p.OreMined <= 0) : players.All(p => p.DamageDealt <= 0);
            if (noStats && s.BloodTarget != BloodTargetMode.Rotating)
                ranked = players.OrderBy(_ => _rng.Next()).ToList();

            var target = ranked.FirstOrDefault(p => !HitConsecutiveLimit(p.PlayerId, s)) ?? ranked[0];
            _history.Add(target.PlayerId);
            return new VendettaPick { Target = target, IsGreedWave = greed };
        }

        private bool HitConsecutiveLimit(string playerId, VendettaSettings s)
        {
            int limit = s.MaxConsecutiveTargetWaves;
            if (limit <= 0 || _history.Count < limit) return false;
            for (int i = _history.Count - limit; i < _history.Count; i++)
                if (_history[i] != playerId) return false;
            return true;
        }

        private List<PlayerWaveStats> RotationOrder(IList<PlayerWaveStats> players)
        {
            var ordered = players.OrderBy(p => p.PlayerId, StringComparer.Ordinal).ToList();
            string last = _history.Count > 0 ? _history[_history.Count - 1] : null;
            int lastIndex = ordered.FindIndex(p => p.PlayerId == last);
            int start = lastIndex < 0 ? 0 : lastIndex + 1;
            var result = new List<PlayerWaveStats>();
            for (int i = 0; i < ordered.Count; i++)
                result.Add(ordered[(start + i) % ordered.Count]);
            return result;
        }

        public void ResetForNewRun() { _history.Clear(); }
    }
}
