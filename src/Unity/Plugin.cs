using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Vendetta.Game;

namespace Vendetta
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        // Thunderstore team BaranMods. Don't change it: it names everyone's settings and Nemesis files.
        public const string PluginGuid = "baranmods.stonewards.vendetta";
        public const string PluginName = "Vendetta";
        public const string PluginVersion = "0.2.1";

        public static Plugin Instance { get; private set; }
        public static BepInEx.Logging.ManualLogSource Log { get { return Instance.Logger; } }
        public static VendettaConfig Settings { get; private set; }
        public static ConfigEntry<bool> DebugOverlay { get; private set; }
        public static StonewardsBridge Bridge { get; private set; }
        public static VendettaDirector Director { get; private set; }
        public static List<GameHooks.HookStatus> Hooks { get; private set; }

        private static readonly HashSet<string> _loggedOnce = new HashSet<string>();
        private string _rosterPath;

        /// <summary>Everything runs on the host, so in multiplayer these are only used there: the host's settings apply to everyone.</summary>
        public static VendettaSettings Active { get { return Settings.Local; } }

        private void Awake()
        {
            Instance = this;
            Settings = new VendettaConfig(Config);
            DebugOverlay = Config.Bind("9. Debug", "Show debug overlay", false,
                "Shows hook status, player positions/zones and the live vendetta. Use it while filling in GameNames.");

            _rosterPath = Path.Combine(Paths.ConfigPath, PluginGuid + ".nemeses.txt");
            var roster = NemesisRoster.Load(File.Exists(_rosterPath) ? File.ReadAllText(_rosterPath) : null);

            Bridge = new StonewardsBridge(Logger);
            Director = new VendettaDirector(Bridge, () => Active, roster);
            Director.RosterChanged += SaveRoster;
            Director.Announce += (text, who) => Logger.LogInfo("[Vendetta] " + text);

            Hooks = GameHooks.Apply(new Harmony(PluginGuid), Logger, Bridge, Director);

            // Our own GameObject, so the runner keeps working if the game removes BepInEx's.
            var go = new GameObject("Vendetta");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<VendettaRunner>();

            int attached = Hooks.FindAll(h => h.Attached).Count;
            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded. Preset: " + Settings.PresetEntry.Value
                           + ". Hooks attached: " + attached + "/" + Hooks.Count
                           + (Bridge.IsReady ? "" : ". GameNames not filled in yet, so the mechanics are inactive."));
        }

        private void SaveRoster()
        {
            try { File.WriteAllText(_rosterPath, Director.Roster.Save()); }
            catch (Exception e) { LogOnce("SaveRoster", e); }
        }

        public static void LogOnce(string key, Exception e)
        {
            if (Instance != null && _loggedOnce.Add(key)) Instance.Logger.LogError("Vendetta " + key + " error (shown once): " + e);
        }

    }
}
