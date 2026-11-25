using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace Vendetta
{
    /// <summary>
    /// Display hints read by BepInEx.ConfigurationManager and Stonewards ModSettings.
    /// Both find this class by name, so neither needs to be referenced.
    /// </summary>
    internal sealed class ConfigurationManagerAttributes
    {
        public int? Order;
    }

    /// <summary>
    /// Binds every VendettaSettings field to a BepInEx config entry. Values are
    /// saved in BepInEx/config/&lt;plugin GUID&gt;.cfg and appear in the in-game
    /// ConfigurationManager window (F1 by default). Choosing a preset overwrites
    /// every value; changing any single value switches the preset to Custom.
    /// </summary>
    public class VendettaConfig
    {
        public ConfigEntry<Preset> PresetEntry { get; private set; }

        /// <summary>This player's own settings. During multiplayer, gameplay code should read Plugin.Active instead.</summary>
        public VendettaSettings Local { get; private set; }


        private readonly List<Action<VendettaSettings>> _readEntriesInto = new List<Action<VendettaSettings>>();
        private readonly List<Action<VendettaSettings>> _writeEntriesFrom = new List<Action<VendettaSettings>>();
        private bool _applyingPreset;
        private int _order = 1000; // menus list higher Order first, so count down to keep the order above

        public VendettaConfig(ConfigFile cfg)
        {
            // Section names start with numbers so the menu shows them in this order.
            PresetEntry = cfg.Bind("1. Preset", "Preset", Preset.Standard,
                new ConfigDescription("Casual / Standard / Brutal overwrite every value below. Changing any single value switches to Custom."));

            const string T = "2. Targeting";
            Bind(cfg, T, "Blood vendetta target", "Who is hunted on normal waves.", null,
                s => s.BloodTarget, (s, v) => s.BloodTarget = v);
            Bind(cfg, T, "Greed vendetta every N waves", "Targets the top ore miner, and goes straight to the mine entrance if they're underground. 0 = off.",
                new AcceptableValueRange<int>(0, 10), s => s.GreedEveryNWaves, (s, v) => s.GreedEveryNWaves = v);
            Bind(cfg, T, "Max waves in a row for one target", "Same dwarf can't be hunted more than this many waves running. 0 = no limit.",
                new AcceptableValueRange<int>(0, 5), s => s.MaxConsecutiveTargetWaves, (s, v) => s.MaxConsecutiveTargetWaves = v);

            const string S = "3. Scaling";
            Bind(cfg, S, "Hits per stack step", "Hits on the target needed for each damage/speed step.",
                new AcceptableValueRange<float>(1f, 5f), s => s.HitsPerStep, (s, v) => s.HitsPerStep = v);
            Bind(cfg, S, "Max hits", "Hits stop counting past this.",
                new AcceptableValueRange<float>(1f, 20f), s => s.MaxHits, (s, v) => s.MaxHits = v);
            Bind(cfg, S, "Base damage", "Damage per hit at 0 stacks.",
                new AcceptableValueRange<float>(1f, 100f), s => s.BaseDamage, (s, v) => s.BaseDamage = v);
            Bind(cfg, S, "Damage growth per step", "Damage is multiplied by this each step.",
                new AcceptableValueRange<float>(1f, 3f), s => s.DamageGrowthPerStep, (s, v) => s.DamageGrowthPerStep = v);
            Bind(cfg, S, "Max damage", "Damage per hit never goes above this.",
                new AcceptableValueRange<float>(1f, 200f), s => s.MaxDamage, (s, v) => s.MaxDamage = v);
            Bind(cfg, S, "Base attack interval (s)", "Seconds between attacks at 0 stacks.",
                new AcceptableValueRange<float>(0.3f, 5f), s => s.BaseAttackInterval, (s, v) => s.BaseAttackInterval = v);
            Bind(cfg, S, "Attack interval added per step (s)", "Each step slows its attacks by this much.",
                new AcceptableValueRange<float>(0f, 2f), s => s.IntervalPerStep, (s, v) => s.IntervalPerStep = v);
            Bind(cfg, S, "Blocked hit value", "How much a blocked hit counts toward stacks. 0 = blocking stops growth, 1 = counts fully.",
                new AcceptableValueRange<float>(0f, 1f), s => s.BlockedHitValue, (s, v) => s.BlockedHitValue = v);
            Bind(cfg, S, "Max single hit (% of target HP)", "No hit can do more than this share of the target's max HP.",
                new AcceptableValueRange<float>(10f, 100f), s => s.MaxHitPercentOfTargetHp, (s, v) => s.MaxHitPercentOfTargetHp = v);

            const string H = "4. Healing";
            Bind(cfg, H, "Heal delay (s)", "Seconds without being damaged before the monster starts healing.",
                new AcceptableValueRange<float>(1f, 30f), s => s.HealDelaySeconds, (s, v) => s.HealDelaySeconds = v);
            Bind(cfg, H, "Heal rate (% max HP per second)", "0 = no healing.",
                new AcceptableValueRange<float>(0f, 20f), s => s.HealPercentPerSecond, (s, v) => s.HealPercentPerSecond = v);

            const string M = "5. Movement";
            Bind(cfg, M, "Wall jump", "Monster can leap onto the walls after its target, and back down when they jump off.", null,
                s => s.WallJumpEnabled, (s, v) => s.WallJumpEnabled = v);
            Bind(cfg, M, "Wall jump telegraph (s)", "Crouch-and-roar warning before it leaps.",
                new AcceptableValueRange<float>(0.2f, 3f), s => s.WallJumpTelegraphSeconds, (s, v) => s.WallJumpTelegraphSeconds = v);
            Bind(cfg, M, "Courtyard safe time (s)", "Wave 1: how long its target is safe in the courtyard or on the stairs once the monster is at the wall, before it leaps over. The time comes back while they're outside or on the wall. 0 = never safe.",
                new AcceptableValueRange<float>(0f, 60f), s => s.CourtyardSafeSeconds, (s, v) => s.CourtyardSafeSeconds = v);
            Bind(cfg, M, "Courtyard safe time lost per wave (s)", "The safe time gets this much shorter every wave. 0 = stays the same.",
                new AcceptableValueRange<float>(0f, 10f), s => s.CourtyardSafeLossPerWave, (s, v) => s.CourtyardSafeLossPerWave = v);
            Bind(cfg, M, "Courtyard safe time minimum (s)", "The safe time never gets shorter than this.",
                new AcceptableValueRange<float>(0f, 60f), s => s.CourtyardSafeMinSeconds, (s, v) => s.CourtyardSafeMinSeconds = v);
            Bind(cfg, M, "Mine behavior", "WaitAtEntrance: while its target is in the mines, it waits at the entrance and gets stronger. Off: it ignores them until they come out.", null,
                s => s.Mines, (s, v) => s.Mines = v);
            Bind(cfg, M, "Mine follow delay (s)", "How long after its target enters the mines before it sets off for the entrance.",
                new AcceptableValueRange<float>(0f, 15f), s => s.MineFollowDelaySeconds, (s, v) => s.MineFollowDelaySeconds = v);
            Bind(cfg, M, "Mine wait growth (s per hit)", "While it waits at the mines, it gains one hit toward stacks every this many seconds. 0 = no growth.",
                new AcceptableValueRange<float>(0f, 60f), s => s.MineWaitSecondsPerHit, (s, v) => s.MineWaitSecondsPerHit = v);

            const string L = "6. Losing";
            Bind(cfg, L, "Loss mode", "ReviveOrLose: the target goes down and can be revived. InstantLoss: the target's death ends the run.", null,
                s => s.Loss, (s, v) => s.Loss = v);
            Bind(cfg, L, "Revive window (s)", "Time the group has to revive the downed target. The clock stops once the body is on a revive spot.",
                new AcceptableValueRange<float>(3f, 60f), s => s.ReviveWindowSeconds, (s, v) => s.ReviveWindowSeconds = v);

            const string N = "7. Nemesis";
            Bind(cfg, N, "Nemesis enabled", "When a vendetta monster downs its target, a random monster type from that wave returns as a Nemesis in a later run.", null,
                s => s.NemesisEnabled, (s, v) => s.NemesisEnabled = v);
            Bind(cfg, N, "Stack carry-over (%)", "Share of its stacks a Nemesis keeps when it returns.",
                new AcceptableValueRange<float>(0f, 100f), s => s.NemesisStackCarryOverPercent, (s, v) => s.NemesisStackCarryOverPercent = v);
            Bind(cfg, N, "Max active nemeses", "Oldest is dropped when a new one would go over this.",
                new AcceptableValueRange<int>(1, 10), s => s.MaxActiveNemeses, (s, v) => s.MaxActiveNemeses = v);
            Bind(cfg, N, "Earliest return wave", "A returning Nemesis appears on this wave or later (once per run).",
                new AcceptableValueRange<int>(1, 15), s => s.NemesisEarliestWave, (s, v) => s.NemesisEarliestWave = v);

            const string R = "8. Rewards";
            Bind(cfg, R, "Target kill bonus (ore)", "Iron on wave 1 for the hunted dwarf when they land the killing blow. 8 = one stack.",
                new AcceptableValueRange<int>(0, 500), s => s.TargetKillBonusOre, (s, v) => s.TargetKillBonusOre = v);
            Bind(cfg, R, "Early kill bonus (ore each)", "Iron on wave 1 for every player if it dies before reaching max stacks.",
                new AcceptableValueRange<int>(0, 500), s => s.GroupKillBonusOre, (s, v) => s.GroupKillBonusOre = v);
            Bind(cfg, R, "Reward growth (%)", "Both rewards grow by this share of their wave 1 size each time they step up.",
                new AcceptableValueRange<float>(0f, 200f), s => s.RewardGrowthPercent, (s, v) => s.RewardGrowthPercent = v);
            Bind(cfg, R, "Reward growth every N waves", "How often the rewards step up. 2 = every other wave, 1 = every wave, 0 = never.",
                new AcceptableValueRange<int>(0, 10), s => s.RewardGrowthEveryNWaves, (s, v) => s.RewardGrowthEveryNWaves = v);

            PresetEntry.SettingChanged += (sender, args) => ApplyPreset(PresetEntry.Value);
            // Re-apply a named preset on load, so a mod update that changes its values isn't
            // hidden by the old numbers in the saved .cfg. Custom values are left alone.
            ApplyPreset(PresetEntry.Value);
        }

        private void Bind<TValue>(ConfigFile cfg, string section, string key, string description,
            AcceptableValueBase range, Func<VendettaSettings, TValue> get, Action<VendettaSettings, TValue> set)
        {
            TValue standardDefault = get(VendettaSettings.ForPreset(Preset.Standard));
            var tags = new ConfigurationManagerAttributes { Order = _order-- };
            ConfigEntry<TValue> entry = cfg.Bind(section, key, standardDefault, new ConfigDescription(description, range, tags));

            _readEntriesInto.Add(s => set(s, entry.Value));
            _writeEntriesFrom.Add(s => entry.Value = get(s));

            entry.SettingChanged += (sender, args) =>
            {
                if (!_applyingPreset && PresetEntry.Value != Preset.Custom)
                {
                    _applyingPreset = true;   // keeps this switch to Custom from re-applying a preset
                    PresetEntry.Value = Preset.Custom;
                    _applyingPreset = false;
                }
                if (!_applyingPreset) Rebuild();
            };
        }

        private void ApplyPreset(Preset preset)
        {
            if (_applyingPreset) return;
            if (preset != Preset.Custom)
            {
                _applyingPreset = true;
                var values = VendettaSettings.ForPreset(preset);
                foreach (var write in _writeEntriesFrom) write(values);
                _applyingPreset = false;
            }
            Rebuild();
        }

        private void Rebuild()
        {
            var s = new VendettaSettings();
            foreach (var read in _readEntriesInto) read(s);
            Local = s;
        }
    }
}
