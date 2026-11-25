using System;
using System.Globalization;

namespace Vendetta
{
    /// <summary>
    /// Everything the screen shows about the vendetta. The host makes one from the director; the other
    /// players get it as text (see LobbySync). Countdowns tick down locally between updates.
    /// </summary>
    public class VendettaView
    {
        private const char Separator = '\u001f';
        private const int FieldCount = 14;

        public uint MonsterNetId; // 0 = no vendetta right now
        public string MonsterName = "", TargetName = "", Perk = "", Status = "";
        public bool IsNemesis, IsGreed, HuntingKing;
        public int Hits, MaxHits = 1;
        public float LeapsIn;     // > 0: the courtyard countdown
        public string DownedName = "";
        public bool ReviveSecured;
        public float ReviveRemaining;
        public bool HasMonster { get { return MonsterName.Length > 0; } }

        public static VendettaView From(VendettaDirector d, VendettaSettings s, Func<IGameMonster, uint> netId)
        {
            var v = new VendettaView();
            if (d.DownedTarget != null)
            {
                v.DownedName = d.DownedTarget.Name;
                v.ReviveSecured = d.ReviveSecured;
                v.ReviveRemaining = Math.Max(0f, d.ReviveRemaining);
            }
            var c = d.Current;
            if (c == null) return v;
            v.MonsterNetId = netId(c.Monster);
            v.MonsterName = c.MonsterName;
            v.TargetName = c.Target.Name;
            v.IsNemesis = c.IsNemesis;
            v.IsGreed = c.IsGreedWave;
            v.HuntingKing = c.HuntingKing;
            v.Perk = c.IsNemesis && c.Perk != NemesisPerk.None ? NemesisRecord.PerkLabel(c.Perk) : "";
            v.MaxHits = Math.Max(1, (int)Math.Ceiling(s.MaxHits));
            v.Hits = Math.Min(v.MaxHits, (int)c.Stacks.Hits);
            v.Status = c.TelegraphRemaining > 0f ? "LEAPING!"
                : c.WaitingAtMine ? (c.ArrivedAtMine ? "WAITING AT THE MINES" : "HEADING TO THE MINES") : "";
            v.LeapsIn = v.Status.Length == 0 && c.Sheltered ? c.ShelterRemaining : 0f;
            return v;
        }

        /// <summary>Moves the countdowns on while waiting for the next update.</summary>
        public void Age(float dt)
        {
            if (LeapsIn > 0f) LeapsIn = Math.Max(0.01f, LeapsIn - dt);
            if (!ReviveSecured) ReviveRemaining = Math.Max(0f, ReviveRemaining - dt);
        }

        /// <summary>Changes only when something other than a running countdown does.</summary>
        public string ChangeKey() { return Write(LeapsIn > 0f ? "1" : "0", ""); }

        public string Serialize() { return Write(Num(LeapsIn), Num(ReviveRemaining)); }

        private string Write(string leapsIn, string revive)
        {
            return string.Join(Separator.ToString(), new[]
            {
                MonsterNetId.ToString(CultureInfo.InvariantCulture), Clean(MonsterName), Clean(TargetName), Clean(Perk), Clean(Status),
                IsNemesis ? "1" : "0", IsGreed ? "1" : "0", HuntingKing ? "1" : "0",
                Hits.ToString(CultureInfo.InvariantCulture), MaxHits.ToString(CultureInfo.InvariantCulture), leapsIn,
                Clean(DownedName), ReviveSecured ? "1" : "0", revive
            });
        }

        /// <summary>Null if the text isn't a view. Never throws: it comes from another computer.</summary>
        public static VendettaView Parse(string text)
        {
            var p = text != null ? text.Split(Separator) : new string[0];
            if (p.Length < FieldCount) return null;
            var v = new VendettaView
            {
                MonsterName = p[1], TargetName = p[2], Perk = p[3], Status = p[4],
                IsNemesis = p[5] == "1", IsGreed = p[6] == "1", HuntingKing = p[7] == "1",
                DownedName = p[11], ReviveSecured = p[12] == "1"
            };
            if (!uint.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out v.MonsterNetId)
                || !int.TryParse(p[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out v.Hits)
                || !int.TryParse(p[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out v.MaxHits)
                || !float.TryParse(p[10], NumberStyles.Float, CultureInfo.InvariantCulture, out v.LeapsIn)
                || !float.TryParse(p[13], NumberStyles.Float, CultureInfo.InvariantCulture, out v.ReviveRemaining)) return null;
            v.MaxHits = Math.Min(50, Math.Max(1, v.MaxHits));
            return v;
        }

        private static string Num(float f) { return f.ToString("0.00", CultureInfo.InvariantCulture); }
        private static string Clean(string s) { return (s ?? "").Replace(Separator, ' '); }
    }

    /// <summary>
    /// The text the host sends: "VNDT1" + S (state) or M (message). Players without the mod never see it.
    /// The number changes when an older version couldn't read it any more.
    /// </summary>
    public static class SyncText
    {
        private const string Magic = "VNDT1";
        public const int MaxBytes = 4000; // Steam allows 4096

        public static string State(VendettaView v) { return Magic + "S" + v.Serialize(); }

        /// <param name="onlyFor">Player id, or null for everyone.</param>
        public static string Message(string text, string onlyFor)
        {
            if (text != null && text.Length > 300) text = text.Substring(0, 300);
            return Magic + "M" + (onlyFor ?? "") + "\n" + text;
        }

        /// <summary>Exactly one of view and message is set, or false if it isn't ours or can't be read.</summary>
        public static bool TryRead(string raw, out VendettaView view, out string message, out string onlyFor)
        {
            view = null; message = null; onlyFor = null;
            if (raw == null || raw.Length <= Magic.Length || !raw.StartsWith(Magic, StringComparison.Ordinal)) return false;
            string body = raw.Substring(Magic.Length + 1);
            switch (raw[Magic.Length])
            {
                case 'S':
                    view = VendettaView.Parse(body);
                    return view != null;
                case 'M':
                    int split = body.IndexOf('\n');
                    if (split < 0) return false;
                    onlyFor = split > 0 ? body.Substring(0, split) : null;
                    message = body.Substring(split + 1);
                    return true;
            }
            return false;
        }
    }
}
