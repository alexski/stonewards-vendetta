namespace Vendetta.Game
{
    /// <summary>
    /// EVERYTHING Vendetta needs to know about Stonewards' code, as names.
    /// Fill these in from dnSpy/ILSpy (see DISCOVERY.md). Leave a name empty and
    /// the feature that needs it stays off; the log says which ones.
    ///
    /// Formats:
    ///   Class:   full name with namespace as dnSpy shows it, e.g. "Stonewards.Enemy" (or "Enemy" if no namespace)
    ///   Method:  "Class:Method", e.g. "Stonewards.Enemy:TakeDamage"
    ///   Member:  a field or property name on that class, e.g. "currentHealth", or a dotted
    ///            path through other objects, e.g. "PlayerStats.isDead"
    ///   ArgIndex: 0-based position of the parameter in the method; -1 = not available
    ///
    /// Filled in from the game build of 2026-11-07 (Assembly-CSharp.dll). Networking is Mirror, over Steam.
    /// "UserCode_..." names are the bodies Mirror generates for [Command] methods. They run on the
    /// host for every player; the plain method only sends a message when a client calls it.
    /// </summary>
    public static class GameNames
    {
        // ---------------- Classes ----------------
        public static string PlayerClass = "FirstPersonController"; // the dwarf; its PlayerStats component holds health, death and blocking
        public static string MonsterClass = "EnemyController"; // abstract base of EnemyNavMeshController and EnemyClimberController

        // ---------------- Player members ----------------
        public static string PlayerName = "playerName";
        public static string PlayerSteamId = "playerID"; // ulong Steam ID, set by the host
        public static string PlayerHealth = "PlayerStats.syncCurrentHealth";
        public static string PlayerMaxHealth = "PlayerStats.syncMaxHealth";
        public static string PlayerIsDowned = "PlayerStats.isDead"; // the game has no separate downed state: dead players wait to be revived
        public static string PlayerIsBlocking = "PlayerStats.syncIsDefending";
        public static string PlayerIsLocal = "isLocalPlayer"; // from Mirror.NetworkBehaviour

        // ---------------- Monster members ----------------
        public static string MonsterHealth = "NetworkcurrentHealth"; // the Mirror property, so healing syncs to clients
        public static string MonsterMaxHealth = "currentMaxHealth"; // only filled in on the host
        public static string MonsterIsDead = "isDead";
        public static string MonsterBehaviour = "currentEnemyBehaviour";  // the attack script currently driving the monster
        public static string MonsterTypeId = "syncID";                    // EnemyDataSO.ID, e.g. for picking a Nemesis's kind

        public static string MonsterCanMove = "CanMove";                  // false while stunned or frozen
        public static string MonsterMoveTo = "MoveTo";                    // method: position
        public static string MonsterStop = "StopMovement";                // method
        public static string MonsterFindPath = "TryGetValidPathPosition"; // method: (position, out walkable spot) -> bool, true if it can walk all the way

        // ---------------- Behaviour members (on MonsterBehaviour's object) ----------------
        public static string BehaviourController = "controller";          // back to the monster
        public static string BehaviourTargetPlayer = "currentTarget.Player";
        public static string BehaviourLastAttacker = "lastAttacker";      // the AI also prefers whoever hit it last
        public static string BehaviourMakeTarget = "GetTargetPlayer";     // method: player -> EnemyTarget struct
        public static string BehaviourMakeWallTarget = "GetTargetWallPart"; // method -> EnemyTarget; a wall target is just a position, so it's used to guard a spot
        public static string BehaviourMakeKingTarget = "GetTargetKing";   // method -> EnemyTarget
        public static string BehaviourPickTarget = "SelectTarget";        // method -> EnemyTarget the AI would choose now
        public static string BehaviourIsBusy = "IsSpecialState";          // method -> bool: in the middle of an attack it must finish
        public static string BehaviourCanWalk = "CanMoveTowardsTarget";   // method -> bool
        public static string BehaviourInRange = "IsTargetInRange";        // method -> bool: can hit its target from here
        public static string BehaviourTarget = "currentTarget";           // EnemyTarget struct
        public static string TargetPosition = "WorldPosition";            // inside EnemyTarget
        public static string TargetPlayer = "Player";                     // inside EnemyTarget; null for companions
        public static string TargetType = "Type";                         // inside EnemyTarget; PLAYER and COMPANION are someone to fight
        public static string BehaviourSetTarget = "BaseEnemyBehaviour:SetTarget"; // every target change goes through this; its only parameter is the EnemyTarget
        // Runs every frame. Replaced while a vendetta monster holds the mine entrance (shooters would walk back to the wall after each shot).
        public static string BehaviourExecute = "BaseEnemyBehaviour:ExecuteBehaviour";
        public static string BehaviourExecuteExploder = "ExplodeEnemyBehavior:ExecuteBehaviour"; // its own version; the others use the base one
        public static string MonsterKnockback = "EnemyNavMeshController:UserCode_CmdApplyKnockback__Vector3"; // ignored during a leap
        // attackStats is shared by every monster of the type (it lives in EnemyDataSO), so the bridge copies it before changing it.
        public static string BehaviourAttackStats = "attackStats";
        public static string AttackStatsInterval = "attackCooldown";

        // ---------------- Hooks (methods Vendetta listens to) ----------------
        public static string RunStarted = "EnemyManager:ServerInit"; // host only, once per level load
        public static string RunEnded = "NetworkHelper:RPCEndGame"; // host only; victory or defeat
        public static string RunLeft = "LevelManager:OnDestroy"; // the level unloads, including quitting from the pause menu
        public static string WaveStarted = "EnemyManager:SpawnCurrentWave"; // runs just before the first spawn. LaunchWave would be too late: its first monsters spawn before it returns. Includes reinforcement waves
        public static int WaveStarted_MonstersArg = 0;          // list of entries...
        public static string WaveEntryTypeId = "enemy.ID";      // ...and the monster type in each
        public static string WaveEnded = "NetworkHelper:ServerOnEndWave"; // host only; the game calls it once every monster is dead

        public static string MonsterSpawned = "EnemyController:ServerInit"; // host only; also runs for cave monsters (DiggingWaypointEnemy)

        public static string PlayerTakeDamage = "PlayerStats:UserCode_ServerApplyDamage__DamageResult__WeaponType__NetworkIdentity";
        // The damage is always the method's first parameter (Harmony can only replace one it knows by position).
        public static string DamageAmount = "Damage";           // the number inside it (DamageResult struct). Empty = the parameter is a number
        public static int PlayerTakeDamage_SourceArg = 2; // NetworkIdentity on the attacker's GameObject

        public static string MonsterTakeDamage = "EnemyController:UserCode_ServerApplyDamage__DamageResult__WeaponType__NetworkIdentity";
        public static int MonsterTakeDamage_SourceArg = 2; // NetworkIdentity

        public static string MonsterDied = "EnemyController:DeathCo"; // host only, once per death. Only 14 bytes, so Mono usually inlines it and this hook is skipped: MonsterTakeDamage spots the death instead

        public static string PlayerDowned = "PlayerCarryHandler:OnPlayerDeath"; // on the player; runs on death only (PlayerStats:HandleDeathChanged also runs on revive). Runs inside PlayerTakeDamage, which is where the attacker comes from
        public static string RevivePlaced = "FirstPersonController:ServerPlaceOnReviveSpot"; // host only; instance = the body. Stops the revive clock
        public static string PlayerRevived = "PlayerStats:ServerRevive"; // host only; every revive goes through this

        public static string OreMined = "DiggingManager:ServerTrySpawnResource"; // host only; once per block broken (pickaxe or bomb), whether or not it drops ore
        public static int OreMined_PlayerArg = 0;
        public static int OreMined_AmountArg = -1;    // -1 = counts as 1

        // ---------------- Actions (methods Vendetta calls) ----------------
        // "Ore" is an inventory item (STONE, IRON or GOLD). Don't use ResourceManager:AddMetaCurrency: it changes the permanent save.
        public static string OreItemId = "IRON";
        public static string ItemManager = "ItemManager:Instance";
        public static string ItemById = "GetItemDataSoByID";              // method on ItemManager: id -> item
        public static string ItemDrop = "ServerInstantiatePickableItem";  // method on ItemManager: drops what doesn't fit
        public static string PlayerInventory = "InventorySystem";         // on the player
        public static string InventoryAdd = "ServerAddItem";              // method on it: (item, count) -> how many didn't fit
        public static string LoseRun = "LevelManager:KillAllPlayers"; // what the game itself does when the King dies: everyone falls, defeat screen 3 s later

        // ---------------- Multiplayer ----------------
        public static string Lobby = "SteamLobby:Instance";
        public static string LobbyId = "currentLobbyID";                    // ulong, on Lobby; 0 = not in one
        public static string NetId = "netId";                               // on every networked object; the same for all players
        public static string SpawnedObjects = "Mirror.NetworkClient:spawned"; // netId -> NetworkIdentity, on every client
        public static string HostCheck = "Mirror.NetworkServer:active"; // true on the host and in solo

        // ---------------- Zones ----------------
        // The game itself separates caves by X: you're in a cave when your X is below the cave entrance's X + 1.
        public static string CaveEntrance = "DiggingManager:Instance.CaveLevelGenerator.StartingCavePosition"; // Vector3. Empty = use MineBelowY
        // Map 1 (Nov 2026): wall top and courtyard are both at Y 8, the field at 0. Map 2: 6.4 and 0. So height alone can't tell the wall
        // from the courtyard; these objects can. The caves are towards -X and the field towards +X on both.
        public static string Wall = "LevelManager:Instance.Wall";
        public static string King = "LevelManager:Instance.King";
        public static string WallHitBoxes = "Stats.wallHitBoxes";     // on it: one box per gate
        public static string WallWalkObject = "GroundColliders";      // child object on it; its children lie along the walkway on top
        // Measure these with the debug overlay (F1 > 9. Debug). Stand in the mines / on the wall and read your Y.
        public static float MineBelowY = -5f;         // only used when CaveEntrance is empty
        public static float WallAboveY = 5f;          // above this = up on the wall or in the courtyard, not in the field (measured: ground 0.7, wall 9.2)
    }
}
