using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Vendetta.Game;

namespace Vendetta
{
    /// <summary>
    /// Drives the director every frame and draws Vendetta's on-screen parts with
    /// Unity's immediate-mode GUI (no game assets needed): the marker above the
    /// monster (visible through walls), announcement banners, the revive countdown,
    /// and an optional debug overlay for filling in GameNames.
    /// </summary>
    public class VendettaRunner : MonoBehaviour
    {
        private const float MessageSeconds = 5f;
        private const int MaxMessages = 4;
        private const float MarkerHeight = 2.6f;

        private struct Message { public string Text; public float Until; }
        private readonly List<Message> _messages = new List<Message>();

        private GUIStyle _name, _target, _pips, _banner, _revive, _debug;

        private LobbySync _sync;
        private VendettaView _view; // what to draw this frame; null = nothing

        private void Start()
        {
            Plugin.Director.Announce += OnHostAnnounce;
            try { StartSync(); }
            catch (Exception e) { Plugin.LogOnce("Multiplayer sync", e); } // the rest works without it
        }

        // In a method of its own, so a missing Steam library can only fail here.
        private void StartSync()
        {
            _sync = new LobbySync(Plugin.Log, Plugin.Bridge);
            _sync.MessageReceived += (text, onlyFor) =>
            {
                Plugin.Log.LogInfo("[Vendetta, from host] " + text);
                OnAnnounce(text, onlyFor);
            };
        }

        private void OnDestroy()
        {
            if (Plugin.Director != null) Plugin.Director.Announce -= OnHostAnnounce;
        }

        private void OnHostAnnounce(string text, string onlyForPlayerId)
        {
            OnAnnounce(text, onlyForPlayerId);
            if (_sync == null) return;
            try { _sync.SendMessage(text, onlyForPlayerId); }
            catch (Exception e) { Plugin.LogOnce("Multiplayer sync", e); }
        }

        private void OnAnnounce(string text, string onlyForPlayerId)
        {
            if (onlyForPlayerId != null)
            {
                var local = Plugin.Bridge.LocalPlayer;
                // Until GameNames.PlayerIsLocal is set, nobody counts as local, so show personal messages to everyone.
                if (local != null && local.Id != onlyForPlayerId) return;
            }
            _messages.Add(new Message { Text = text, Until = Time.time + MessageSeconds });
            if (_messages.Count > MaxMessages) _messages.RemoveAt(0);
        }

        private void Update()
        {
            try { Plugin.Director.Tick(Time.deltaTime); }
            catch (Exception e) { Plugin.LogOnce("Tick", e); }
            try { UpdateView(); }
            catch (Exception e) { Plugin.LogOnce("View", e); }
        }

        private void UpdateView()
        {
            bool host = Plugin.Bridge.IsHost;
            _view = host ? VendettaView.From(Plugin.Director, Plugin.Active, Plugin.Bridge.NetIdOf) : null;
            if (_sync == null) return;
            try { _sync.Tick(Time.unscaledDeltaTime, _view); }
            catch (Exception e) { Plugin.LogOnce("Multiplayer sync", e); }
            if (!host) _view = _sync.Received;
        }

        private Transform MonsterTransform()
        {
            var c = Plugin.Director.Current;
            var monster = Plugin.Bridge.IsHost
                ? (c != null ? ((MonsterHandle)c.Monster).C : null)
                : Plugin.Bridge.FindByNetId(_view.MonsterNetId);
            return monster ? monster.transform : null;
        }

        private void OnGUI()
        {
            try
            {
                EnsureStyles();
                DrawMarker();
                DrawRevive();
                DrawMessages();
                if (Plugin.DebugOverlay.Value) DrawDebug();
            }
            catch (Exception e) { Plugin.LogOnce("OnGUI", e); }
        }

        // ---------------- Marker ----------------

        private void DrawMarker()
        {
            var v = _view;
            var cam = Camera.main;
            if (v == null || !v.HasMonster || cam == null) return;
            var monster = MonsterTransform();
            if (!monster) return;

            Vector3 screen = cam.WorldToScreenPoint(monster.position + Vector3.up * MarkerHeight * monster.localScale.y);
            if (screen.z <= 0f) return; // behind the camera
            float x = screen.x, y = Screen.height - screen.y;
            const float w = 320f;

            string title = v.IsNemesis ? "NEMESIS  " + v.MonsterName : v.MonsterName;
            if (v.Perk.Length > 0) title += "  [" + v.Perk + "]";
            Shadowed(new Rect(x - w / 2, y - 62, w, 24), title, _name);
            Shadowed(new Rect(x - w / 2, y - 40, w, 22), (v.IsGreed ? "GREED VENDETTA: " : "VENDETTA: ") + (v.HuntingKing ? "THE KING" : v.TargetName), _target);

            var pips = new StringBuilder();
            for (int i = 0; i < v.MaxHits; i++) pips.Append(i < v.Hits ? "●" : "○");
            string status = v.LeapsIn > 0f ? "LEAPS IN " + (int)Math.Ceiling(v.LeapsIn) + "s" : v.Status;
            if (status.Length > 0) pips.Append("   ").Append(status);
            Shadowed(new Rect(x - w / 2, y - 20, w, 22), pips.ToString(), _pips);
        }

        // ---------------- Banners ----------------

        private void DrawRevive()
        {
            var v = _view;
            if (v == null || v.DownedName.Length == 0) return;
            string name = v.DownedName.ToUpperInvariant();
            string text = v.ReviveSecured ? name + " IS BEING REVIVED" : "REVIVE " + name + "  " + v.ReviveRemaining.ToString("0.0") + "s";
            Shadowed(new Rect(0, Screen.height * 0.22f, Screen.width, 40), text, _revive);
        }

        private void DrawMessages()
        {
            _messages.RemoveAll(m => m.Until < Time.time);
            for (int i = 0; i < _messages.Count; i++)
                Shadowed(new Rect(0, 70 + i * 30, Screen.width, 28), _messages[i].Text, _banner);
        }

        // ---------------- Debug overlay ----------------

        private void DrawDebug()
        {
            var sb = new StringBuilder();
            sb.AppendLine("VENDETTA DEBUG   (turn off: F1 > 9. Debug)");
            sb.AppendLine("Player class: " + Name(Plugin.Bridge.PlayerType, GameNames.PlayerClass) + "   Monster class: " + Name(Plugin.Bridge.MonsterType, GameNames.MonsterClass));
            sb.AppendLine("Host: " + Plugin.Bridge.IsHost + "   Wave: " + GameHooks.WaveNumber);
            foreach (var h in Plugin.Hooks)
                sb.AppendLine((h.Attached ? "[OK] " : "[--] ") + h.Name + "  " + h.Target);

            var players = Plugin.Bridge.GetPlayers();
            sb.AppendLine("Players: " + players.Count);
            foreach (PlayerHandle p in players)
            {
                var pos = p.Position;
                sb.AppendLine("  " + p.Name + (p.IsLocal ? " (you)" : "") + "  Y=" + pos.Y.ToString("0.0") + "  zone=" + p.Zone
                              + "  hp=" + Reflect.GetFloat(p.C, GameNames.PlayerHealth, -1f).ToString("0") + "/" + p.MaxHealth.ToString("0")
                              + (p.IsBlocking ? "  BLOCKING" : "") + (p.IsDowned ? "  DOWNED" : ""));
            }

            var c = Plugin.Director.Current;
            if (c != null)
                sb.AppendLine("Vendetta: " + c.MonsterName + " -> " + c.Target.Name + "  hits " + c.Stacks.Hits.ToString("0.0")
                              + "  dmg " + c.Stacks.CurrentDamage.ToString("0.0") + "  every " + c.Stacks.CurrentAttackInterval.ToString("0.00") + "s"
                              + "  monster zone=" + c.Monster.Zone);
            sb.AppendLine("Nemeses saved: " + Plugin.Director.Roster.Records.Count);
            GUI.Label(new Rect(12, 12, 900, 600), sb.ToString(), _debug);
        }

        private static string Name(Type found, string configured)
        {
            if (string.IsNullOrEmpty(configured)) return "(not set)";
            return found != null ? found.Name : "NOT FOUND '" + configured + "'";
        }

        // ---------------- Styles ----------------

        private void EnsureStyles()
        {
            if (_name != null) return;
            _name = Style(18, FontStyle.Bold, new Color(1f, 0.84f, 0.35f));
            _target = Style(16, FontStyle.Bold, new Color(1f, 0.3f, 0.25f));
            _pips = Style(16, FontStyle.Normal, new Color(1f, 0.55f, 0.3f));
            _banner = Style(20, FontStyle.Bold, new Color(1f, 0.9f, 0.75f));
            _revive = Style(30, FontStyle.Bold, new Color(1f, 0.25f, 0.2f));
            _debug = Style(13, FontStyle.Normal, Color.white);
            _debug.alignment = TextAnchor.UpperLeft;
        }

        private static GUIStyle Style(int size, FontStyle fontStyle, Color color)
        {
            var s = new GUIStyle(GUI.skin.label) { fontSize = size, fontStyle = fontStyle, alignment = TextAnchor.MiddleCenter };
            s.normal.textColor = color;
            return s;
        }

        private static void Shadowed(Rect r, string text, GUIStyle style)
        {
            var color = style.normal.textColor;
            style.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), text, style);
            style.normal.textColor = color;
            GUI.Label(r, text, style);
        }
    }
}
