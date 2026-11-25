# Finding Stonewards' names (filling in GameNames.cs)

> **Already filled in** for the game build of 2026-11-07. Use this guide to find names again after a game update. Some entries below have changed since it was written (`MonsterTarget`, `MonsterAttackInterval`, `GrantOre`, `_DamageArg`, `_KillerArg`, `MineBelowY`); the comments in `GameNames.cs` are current.

Vendetta's mechanics are complete, but they stay inactive until `src/Game/GameNames.cs` says where things are in the game's code. That file is the only one you edit. Every entry is optional: an empty entry turns off only the feature that needs it.

## 1. Open the game's code
1. Install **dnSpy** (dnSpyEx fork) or **ILSpy**.
2. Open `D:\SteamLibrary\steamapps\common\Stonewards\Stonewards_Data\Managed\Assembly-CSharp.dll`.
3. Use search (dnSpy: **Ctrl+Shift+K**) with the terms below. Click a class to see its fields and methods.
4. A class's full name is its namespace plus the class name, e.g. `Stonewards.Enemy`. If it has no namespace (shown as `-`), use just the class name.

## 2. What to look for

Fill these in roughly in this order. Each group makes a part of the mod work.

### A. Players and monsters (required for everything)
| GameNames entry | Search for | What it is |
|---|---|---|
| `PlayerClass` | Player, Dwarf, Character, PlayerController | The component on each player's dwarf. It usually has a health field. |
| `MonsterClass` | Enemy, Monster, Mob, AI | The base class shared by all monsters. Choose the parent class, not one specific monster. |
| `PlayerHealth`, `PlayerMaxHealth` | health, hp, maxHealth | Fields or properties on the player class |
| `MonsterHealth`, `MonsterMaxHealth` | (same, on the monster class) | |
| `PlayerName` | name, playerName, nickname | A string |
| `PlayerIsLocal` | isLocalPlayer, IsOwner, isMine, IsLocal | A bool that's true for your own dwarf |

### B. Waves (makes vendettas start)
| Entry | Search for | Tip |
|---|---|---|
| `WaveStarted` | Wave, StartWave, Horn, BeginWave | Format `Class:Method`, e.g. `WaveManager:StartWave` |
| `WaveEnded` | EndWave, WaveCleared, OnWaveComplete | |
| `RunStarted` / `RunEnded` | StartMission, StartGame, GameOver, Victory | |
| `MonsterSpawned` | Use the monster class's `Start` or `Awake` | e.g. `Stonewards.Enemy:Start` |

### C. Combat (makes stacks and damage work)
| Entry | Search for | Tip |
|---|---|---|
| `PlayerTakeDamage` | TakeDamage, Damage, Hit, ApplyDamage on the player class | Set `_DamageArg` to the position of the damage number (0 = first parameter), and `_SourceArg` to the position of the attacker parameter, if there is one. |
| `PlayerIsBlocking` | block, isBlocking, guarding | A bool on the player |
| `MonsterTakeDamage` | (same, on the monster class) | Set the indexes the same way |
| `MonsterDied` | Die, Death, OnDeath, Kill | `_KillerArg` is the position of the killer parameter, if there is one |
| `MonsterTarget` | target, currentTarget, chaseTarget | What the monster is chasing |
| `MonsterAttackInterval` | attackCooldown, attackRate, attackDelay | Seconds between attacks |

If the damage parameter is a struct or class (like `DamageInfo`) rather than a number, note what's inside it and tell me. The hook needs a small change for that.

### D. Downed, revive, losing
| Entry | Search for |
|---|---|
| `PlayerDowned` | Down, Knockdown, Incapacitate, Die (on the player) |
| `PlayerRevived` | Revive, GetUp, Rescue |
| `PlayerIsDowned` | isDowned, isDead, incapacitated |
| `LoseRun` | GameOver, Lose, Defeat, KingDied, MissionFailed |

### E. Ore and rewards
| Entry | Search for |
|---|---|
| `OreMined` | Mine, Deposit, AddOre, AddResource, Collect |
| `GrantOre` | AddOre, AddResource, AddCurrency (a method that takes a number) |

### F. Multiplayer
| Entry | Search for |
|---|---|
| `HostCheck` | IsHost, IsServer, isServer, IsMasterClient, NetworkManager |
| `PlayerSteamId` | SteamId, CSteamID, steamID, ownerId |

Also check which networking library the game uses. If any of these namespaces appear in the assembly list, tell me which: `Mirror`, `Unity.Netcode`, `FishNet`, `Photon`, `Steamworks`, `Netcode`. Syncing settings and the marker to clients depends on it.

## 3. Test it
1. Fill in group A, rebuild (`dotnet build -c Release`), and turn on **9. Debug → Show debug overlay** in the settings menu.
2. The overlay should show both classes found, plus your dwarf with health and position.
3. **Zones:** stand in a mine and on the wall, and note the `Y=` value in each spot. Set `MineBelowY` and `WallAboveY` between those values and the bastion floor.
4. Fill in group B. Start a mission: you should get a "sworn a vendetta" banner when the horn sounds and see a marker above one monster.
5. Fill in group C. Let it hit you and watch the stack dots fill.
6. Then do D, E and F.

The overlay lists every hook as `[OK]` (attached) or `[--]` (not set or not found). `BepInEx\LogOutput.log` says why a hook failed.

## 4. What to send me
For anything that doesn't behave, paste:
- the relevant `GameNames` lines you set,
- the method's signature as dnSpy shows it, e.g. `public void TakeDamage(float amount, Enemy source)`,
- the relevant lines from `LogOutput.log`.
