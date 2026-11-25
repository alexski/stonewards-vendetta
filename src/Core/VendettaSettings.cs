using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Vendetta
{
    public enum Preset { Casual, Standard, Brutal, Custom }
    public enum BloodTargetMode { MostDamage, Random, Rotating }
    public enum MineBehavior { WaitAtEntrance, Off }
    public enum LossMode { ReviveOrLose, InstantLoss }

    /// <summary>
    /// Every tunable number in the mod, as plain fields with no BepInEx or Unity
    /// dependency. Gameplay code reads from one of these. The host sends theirs to
    /// clients so everyone runs the same numbers.
    /// To add a setting: add a field here, give it a value in each preset, and bind
    /// it in VendettaConfig.
    /// </summary>
    [Serializable]
    public class VendettaSettings
    {
        // Targeting
        public BloodTargetMode BloodTarget = BloodTargetMode.MostDamage;
        public int GreedEveryNWaves = 3;          // 0 = Greed waves off
        public int MaxConsecutiveTargetWaves = 2; // same dwarf can't be targeted more than this many waves in a row

        // Scaling
        public float HitsPerStep = 2f;
        public float MaxHits = 6f;
        public float BaseDamage = 10f;
        public float DamageGrowthPerStep = 1.65f; // damage multiplier per step
        public float MaxDamage = 45f;
        public float BaseAttackInterval = 1.0f;   // seconds between attacks
        public float IntervalPerStep = 0.75f;
        public float BlockedHitValue = 0.5f;      // how much a blocked hit counts toward stacks (0 / 0.5 / 1)
        public float MaxHitPercentOfTargetHp = 60f;

        // Healing (the monster heals when the group stops hitting it)
        public float HealDelaySeconds = 8f;
        public float HealPercentPerSecond = 5f;

        // Movement
        public bool WallJumpEnabled = true;
        public float WallJumpTelegraphSeconds = 1.0f;
        public float CourtyardSafeSeconds = 15f;   // wave 1: how long a dwarf inside the walls is safe from it leaping in. Comes back while they're out
        public float CourtyardSafeLossPerWave = 1f; // it gets this much shorter every wave...
        public float CourtyardSafeMinSeconds = 5f;  // ...down to this
        public MineBehavior Mines = MineBehavior.WaitAtEntrance;
        public float MineFollowDelaySeconds = 4f;  // how long before it sets off for the mine entrance
        public float MineWaitSecondsPerHit = 10f;  // it gains one hit toward stacks for each of these its dwarf hides. 0 = no growth

        // Losing
        public LossMode Loss = LossMode.ReviveOrLose;
        public float ReviveWindowSeconds = 25f;

        // Nemesis
        public bool NemesisEnabled = true;
        public float NemesisStackCarryOverPercent = 50f;
        public int MaxActiveNemeses = 3;
        public int NemesisEarliestWave = 3;       // a returning Nemesis appears on this wave or later

        // Rewards
        public int TargetKillBonusOre = 8;        // wave 1: the hunted dwarf lands the killing blow (8 = one stack of iron)
        public int GroupKillBonusOre = 4;         // wave 1: each player, if it dies before reaching max stacks
        public float RewardGrowthPercent = 50f;   // rewards grow by this share of their wave 1 size...
        public int RewardGrowthEveryNWaves = 2;   // ...every this many waves. 0 = no growth

        /// <summary>How long the courtyard is safe on this wave (1-based).</summary>
        public float CourtyardSafeOnWave(int wave)
        {
            float left = CourtyardSafeSeconds - CourtyardSafeLossPerWave * Math.Max(0, wave - 1);
            return Math.Max(left, Math.Min(CourtyardSafeSeconds, CourtyardSafeMinSeconds));
        }

        /// <summary>A reward's size on this wave (1-based).</summary>
        public int RewardOnWave(int baseAmount, int wave)
        {
            int steps = RewardGrowthEveryNWaves > 0 ? Math.Max(0, wave - 1) / RewardGrowthEveryNWaves : 0;
            return (int)Math.Round(baseAmount * (1.0 + steps * RewardGrowthPercent / 100.0), MidpointRounding.AwayFromZero);
        }

        public static VendettaSettings ForPreset(Preset preset)
        {
            var s = new VendettaSettings(); // Standard = field defaults
            switch (preset)
            {
                case Preset.Casual:
                    s.GreedEveryNWaves = 4;
                    s.DamageGrowthPerStep = 1.5f;
                    s.MaxDamage = 35f;
                    s.BlockedHitValue = 0f;
                    s.MaxHitPercentOfTargetHp = 45f;
                    s.HealDelaySeconds = 12f;
                    s.HealPercentPerSecond = 3f;
                    s.WallJumpTelegraphSeconds = 1.5f;
                    s.CourtyardSafeSeconds = 25f;
                    s.CourtyardSafeMinSeconds = 10f;
                    s.ReviveWindowSeconds = 35f;
                    s.NemesisStackCarryOverPercent = 25f;
                    s.MineFollowDelaySeconds = 6f;
                    s.NemesisEarliestWave = 4;
                    s.MineWaitSecondsPerHit = 15f;
                    s.TargetKillBonusOre = 12;
                    s.GroupKillBonusOre = 6;
                    break;
                case Preset.Brutal:
                    s.GreedEveryNWaves = 2;
                    s.MaxConsecutiveTargetWaves = 3;
                    s.DamageGrowthPerStep = 1.8f;
                    s.MaxDamage = 60f;
                    s.BlockedHitValue = 1f;
                    s.MaxHitPercentOfTargetHp = 75f;
                    s.HealDelaySeconds = 5f;
                    s.HealPercentPerSecond = 8f;
                    s.WallJumpTelegraphSeconds = 0.7f;
                    s.CourtyardSafeSeconds = 8f;
                    s.CourtyardSafeMinSeconds = 3f;
                    s.ReviveWindowSeconds = 15f;
                    s.NemesisStackCarryOverPercent = 75f;
                    s.MaxActiveNemeses = 5;
                    s.MineFollowDelaySeconds = 2f;
                    s.NemesisEarliestWave = 2;
                    s.MineWaitSecondsPerHit = 6f;
                    s.TargetKillBonusOre = 6;
                    s.GroupKillBonusOre = 2;
                    break;
            }
            return s;
        }

        public VendettaSettings Clone()
        {
            return (VendettaSettings)MemberwiseClone();
        }
    }
}
