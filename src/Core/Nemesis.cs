using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Vendetta
{
    public enum NemesisPerk
    {
        None,
        WallBreaker,   // got its kill on the walls: half-length leap telegraph
        TunnelStalker, // got its kill in the mines: reaches the entrance and grows there twice as fast
        ThickHide      // got its kill anywhere else: +50% health
    }

    [Serializable]
    public class NemesisRecord
    {
        public string MonsterName;
        public string MonsterType;
        public string TargetPlayerId;
        public string TargetPlayerName;
        public float CarriedHits;
        public NemesisPerk Perk;
        public int TimesReturned;
        public long CreatedUtcTicks;

        public static NemesisPerk PerkForKillZone(Zone zone)
        {
            switch (zone)
            {
                case Zone.Wall: return NemesisPerk.WallBreaker;
                case Zone.Mine: return NemesisPerk.TunnelStalker;
                default: return NemesisPerk.ThickHide;
            }
        }

        public static string PerkLabel(NemesisPerk perk)
        {
            switch (perk)
            {
                case NemesisPerk.WallBreaker: return "Wall-Breaker";
                case NemesisPerk.TunnelStalker: return "Tunnel Stalker";
                case NemesisPerk.ThickHide: return "Thick Hide";
                default: return "";
            }
        }
    }

    /// <summary>
    /// The saved list of Nemeses. Saved as text, one Nemesis per line, so the
    /// file is easy to read and hand-edit. The Unity layer handles reading and writing the file.
    /// </summary>
    public class NemesisRoster
    {
        private readonly List<NemesisRecord> _records = new List<NemesisRecord>();
        public IList<NemesisRecord> Records { get { return _records.AsReadOnly(); } }

        public void Add(NemesisRecord record, int maxActive)
        {
            // One grudge per monster name; if it already exists, replace it.
            _records.RemoveAll(r => r.MonsterName == record.MonsterName);
            _records.Add(record);
            while (maxActive > 0 && _records.Count > maxActive)
            {
                var oldest = _records.OrderBy(r => r.CreatedUtcTicks).First();
                _records.Remove(oldest);
            }
        }

        public bool Remove(string monsterName)
        {
            return _records.RemoveAll(r => r.MonsterName == monsterName) > 0;
        }

        /// <summary>The oldest Nemesis hunting any of these players, or null.</summary>
        public NemesisRecord FindForPlayers(IEnumerable<string> playerIds, ICollection<string> excludeNames)
        {
            var ids = new HashSet<string>(playerIds);
            return _records
                .Where(r => ids.Contains(r.TargetPlayerId) && (excludeNames == null || !excludeNames.Contains(r.MonsterName)))
                .OrderBy(r => r.CreatedUtcTicks)
                .FirstOrDefault();
        }

        // ---- Text format: fields separated by '|', with '|' and newlines escaped ----

        public string Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Vendetta nemeses: name|type|targetId|targetName|carriedHits|perk|timesReturned|createdTicks");
            foreach (var r in _records)
            {
                sb.AppendLine(string.Join("|", new[]
                {
                    Esc(r.MonsterName), Esc(r.MonsterType), Esc(r.TargetPlayerId), Esc(r.TargetPlayerName),
                    r.CarriedHits.ToString(CultureInfo.InvariantCulture), r.Perk.ToString(),
                    r.TimesReturned.ToString(CultureInfo.InvariantCulture), r.CreatedUtcTicks.ToString(CultureInfo.InvariantCulture)
                }));
            }
            return sb.ToString();
        }

        public static NemesisRoster Load(string text)
        {
            var roster = new NemesisRoster();
            if (string.IsNullOrEmpty(text)) return roster;
            foreach (var rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                var f = line.Split('|');
                if (f.Length < 8) continue;
                try
                {
                    roster._records.Add(new NemesisRecord
                    {
                        MonsterName = Unesc(f[0]),
                        MonsterType = Unesc(f[1]),
                        TargetPlayerId = Unesc(f[2]),
                        TargetPlayerName = Unesc(f[3]),
                        CarriedHits = float.Parse(f[4], CultureInfo.InvariantCulture),
                        Perk = (NemesisPerk)Enum.Parse(typeof(NemesisPerk), f[5]),
                        TimesReturned = int.Parse(f[6], CultureInfo.InvariantCulture),
                        CreatedUtcTicks = long.Parse(f[7], CultureInfo.InvariantCulture)
                    });
                }
                catch (Exception) { /* skip a damaged line, keep the rest */ }
            }
            return roster;
        }

        private static string Esc(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("|", "\\p").Replace("\n", "\\n").Replace("\r", "");
        }

        private static string Unesc(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char c = s[++i];
                    sb.Append(c == 'p' ? '|' : c == 'n' ? '\n' : c);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }

    public static class MonsterNames
    {
        private static readonly string[] First =
        {
            "Grubnak", "Skarr", "Mogg", "Vrask", "Thul", "Gorza", "Krell", "Uzgob", "Ratch", "Bolg",
            "Snikrat", "Durza", "Ghorm", "Yazgul", "Brakka", "Nurk"
        };
        private static readonly string[] Epithet =
        {
            "the Beard-Taker", "Dwarfbane", "the Grudgeholder", "Stonebiter", "the Patient", "Ironjaw",
            "the Unforgiving", "Axe-Eater", "the Long Memory", "Kingsbane"
        };

        public static string Generate(Random rng)
        {
            return First[rng.Next(First.Length)] + " " + Epithet[rng.Next(Epithet.Length)];
        }
    }
}
