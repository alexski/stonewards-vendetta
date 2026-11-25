using System;
using System.Text;
using BepInEx.Logging;
using Steamworks;
using Vendetta.Game;

namespace Vendetta
{
    /// <summary>
    /// Sends the marker, messages and countdowns from the host to the other players, as Steam lobby messages.
    /// Not as Mirror messages: Mirror disconnects anyone who gets a message type it doesn't know, so a player
    /// without the mod would be kicked. The game ignores lobby messages. Steam also sends them back to the
    /// host, which is how a solo test shows that it works.
    /// </summary>
    public class LobbySync
    {
        private const float Heartbeat = 2f;      // repeats the state for players who join later
        private const float MinGap = 0.25f;
        private const float ExpireAfter = 8f;    // the host stopped sending

        private readonly ManualLogSource _log;
        private readonly StonewardsBridge _bridge;
        private readonly byte[] _buffer = new byte[4096];
        private Callback<LobbyChatMsg_t> _callback; // must be kept, or it stops
        private string _lastKey = "";
        private float _sinceSent, _sinceReceived;
        private bool _idleSent = true, _echoLogged, _receivedLogged, _failedLogged;

        /// <summary>What the host last sent. Null when there's nothing to show.</summary>
        public VendettaView Received { get; private set; }
        /// <summary>(text, onlyForPlayerId) from the host.</summary>
        public event Action<string, string> MessageReceived;

        public LobbySync(ManualLogSource log, StonewardsBridge bridge)
        {
            _log = log;
            _bridge = bridge;
        }

        private ulong Lobby { get { return _bridge.LobbyId; } }

        public void Tick(float dt, VendettaView hostView)
        {
            ulong lobby = Lobby;
            if (lobby == 0) { Received = null; return; }
            if (_callback == null) _callback = Callback<LobbyChatMsg_t>.Create(OnLobbyMessage); // Steam is running once there's a lobby

            if (hostView == null) // not the host
            {
                if (Received == null) return;
                Received.Age(dt);
                _sinceReceived += dt;
                if (_sinceReceived > ExpireAfter) Received = null;
                return;
            }

            Received = null;
            _sinceSent += dt;
            bool idle = !hostView.HasMonster && hostView.DownedName.Length == 0;
            string key = hostView.ChangeKey();
            bool due = key != _lastKey ? _sinceSent >= MinGap : _sinceSent >= Heartbeat && !(idle && _idleSent);
            if (!due) return;
            Send(lobby, SyncText.State(hostView));
            _idleSent = idle && key == _lastKey; // say "nothing any more" twice, then go quiet
            _lastKey = key;
            _sinceSent = 0f;
        }

        public void SendMessage(string text, string onlyFor)
        {
            ulong lobby = Lobby;
            if (lobby != 0) Send(lobby, SyncText.Message(text, onlyFor));
        }

        private void Send(ulong lobby, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > SyncText.MaxBytes) return;
            if (SteamMatchmaking.SendLobbyChatMsg(new CSteamID(lobby), bytes, bytes.Length) || _failedLogged) return;
            _failedLogged = true;
            _log.LogWarning("Vendetta: Steam didn't accept a lobby message, so other players may not see the marker (shown once).");
        }

        private void OnLobbyMessage(LobbyChatMsg_t m)
        {
            try { Read(m); }
            catch (Exception e) { Plugin.LogOnce("LobbySync", e); }
        }

        private void Read(LobbyChatMsg_t m)
        {
            ulong lobby = Lobby;
            if (lobby == 0 || m.m_ulSteamIDLobby != lobby) return;
            CSteamID sender;
            EChatEntryType type;
            int length = SteamMatchmaking.GetLobbyChatEntry(new CSteamID(lobby), (int)m.m_iChatID, out sender, _buffer, _buffer.Length, out type);
            if (length <= 0 || type != EChatEntryType.k_EChatEntryTypeChatMsg) return;

            VendettaView view;
            string message, onlyFor;
            if (!SyncText.TryRead(Encoding.UTF8.GetString(_buffer, 0, length), out view, out message, out onlyFor)) return;

            if (_bridge.IsHost)
            {
                if (_echoLogged || sender != SteamUser.GetSteamID()) return;
                _echoLogged = true;
                _log.LogInfo("Multiplayer sync: working (Steam sent the host's own update back).");
                return;
            }
            if (sender.m_SteamID.ToString() != SteamMatchmaking.GetLobbyData(new CSteamID(lobby), "HostAdress")) return; // only from the host (the game's own spelling)
            if (!_receivedLogged)
            {
                _receivedLogged = true;
                _log.LogInfo("Multiplayer sync: receiving from the host.");
            }
            if (view != null)
            {
                Received = view;
                _sinceReceived = 0f;
            }
            else if (MessageReceived != null) MessageReceived(message, onlyFor);
        }
    }
}
