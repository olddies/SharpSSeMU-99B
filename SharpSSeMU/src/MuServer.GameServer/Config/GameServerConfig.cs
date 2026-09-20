using MuServer.Shared.Config;

namespace MuServer.GameServer.Config;

/// <summary> Partial port of CServerInfo (ServerInfo.h/.cpp) — only the fields needed for the connection core
/// (Phase 1). The original CServerInfo has ~500 game balance fields (damage, drops, resets, etc.) that will be
/// added in later phases as the system that uses them (combat, items, commands...) gets ported. </summary>
public sealed class GameServerConfig
{
    public required string ServerName { get; init; }
    public ushort ServerCode { get; init; }
    public ushort ServerPort { get; init; }
    public required byte[] ServerVersion { get; init; } // 5 bytes, ej. {1,3,'k',0,0}
    public required byte[] ServerSerial { get; init; } // 17 bytes (16 + nulo)
    public byte ServerEncDecKey1 { get; init; }
    public byte ServerEncDecKey2 { get; init; }
    public int ServerMaxUserNumber { get; init; }

    public required string JoinServerAddress { get; init; }
    public ushort JoinServerPort { get; init; }
    public required string DataServerAddress { get; init; }
    public ushort DataServerPort { get; init; }
    public required string ConnectServerAddress { get; init; }
    public ushort ConnectServerPort { get; init; }

    public int MaxConnectionIdle { get; init; }
    public int MaxConnectionPerIP { get; init; }
    public int MaxPacketPerSecond { get; init; }

    public required string EncryptionKeyPath { get; init; } // Hack/Enc2.dat
    public required string DecryptionKeyPath { get; init; } // Hack/Dec1.dat
    public required string TerrainPath { get; init; } // Data/Terrain (TerrainN.att)
    public required string MonsterListPath { get; init; } // Data/Monster/MonsterList.txt
    public required string MonsterSpawnPath { get; init; } // Data/Monster/Spawn/*.txt
    public required string EventPath { get; init; } // Data/Event (DevilSquare.dat, EventEntryLevel.dat, EventStageSpawn.dat)
    public required string ItemPath { get; init; } // Data/Item/Item.txt (balance real de items -- Fase 4, segunda pasada)
    public required string ItemValuePath { get; init; } // Data/Item/ItemValue.txt (precios explicitos -- ver World/ItemValue.cs)
    public required string CharacterInfoPath { get; init; } // Data/GameServerInfo - Character.dat (constantes de CharacterCalcAttribute)
    public required string ServerInfoCommonPath { get; init; } // Data/GameServerInfo - Common.dat (rates globales -- ver Config/ServerInfoConfig.cs)
    public required string ServerInfoEventPath { get; init; } // Data/GameServerInfo - Event.dat (idem, solo los campos ya consumidos por Devil Square)

    // Paths of the 4 remaining GameServerInfo files (plus Custom.dat) that were NOT read at all before -- see
    // Config/GameServerInfoChaosMix.cs, GameServerInfoCommand.cs, GameServerInfoItem.cs,
    // GameServerInfoSkill.cs, GameServerInfoCustom.cs (full field coverage, no consumer yet for most, but no
    // longer hardcoded: if the real .dat is in Data/, it is read as is).
    public required string ServerInfoChaosMixPath { get; init; } // Data/GameServerInfo - ChaosMix.dat
    public required string ServerInfoCommandPath { get; init; } // Data/GameServerInfo - Command.dat
    public required string ServerInfoItemDatPath { get; init; } // Data/GameServerInfo - Item.dat (distinto de ItemPath, que es Item/Item.txt)
    public required string ServerInfoSkillDatPath { get; init; } // Data/GameServerInfo - Skill.dat (distinto de SkillListPath/SkillDamagePath)
    public required string ServerInfoCustomPath { get; init; } // Data/GameServerInfo - Custom.dat
    public required string SkillListPath { get; init; } // Data/Skill/SkillList.txt (balance de skills)
    public required string SkillDamagePath { get; init; } // Data/Skill/SkillDamage.txt (multiplicador opcional por skill)
    public required string ShopManagerPath { get; init; } // Data/ShopManager.txt (NPCs de tienda -- ver World/Shop.cs)
    public required string ShopDataPath { get; init; } // Data/Shop (un .txt de items por NPC, referenciado por ShopManager.txt)

    // Data/Quest/*.txt (see Config/QuestTable.cs, QuestObjectiveTable.cs, QuestRewardTable.cs) -- real quest
    // engine (Sebina/235 = 2nd class, Marlon/229 = another reward at level 220), replaces an earlier version
    // that hardcoded those NPCs with a guessed level/items in C#.
    public required string QuestPath { get; init; }
    public required string QuestObjectivePath { get; init; }
    public required string QuestRewardPath { get; init; }

    public static GameServerConfig Load(string iniPath, string baseDir)
    {
        var ini = IniFile.Load(iniPath);

        // Exact port of ServerInfo.cpp (lines 396-408): they are NOT the first 5 raw bytes of the string -- the
        // original builds m_ServerVersion by taking indices {0,2,3,5,6} of a dotted string like "1.02.00" (to
        // discard the two '.'). With the real package value ("1.02.00" in GameServerInfo - Common.dat) this
        // gives the ASCII bytes {'1','0','2','0','0'}. Copying the first 5 bytes as they are (as was done
        // before) gives an incorrect ClientVersion that the real client would reject with result 6.
        string version = ini.GetString("GameServerInfo", "ServerVersion", "1.02.00");
        var versionBuff = version.PadRight(7, '\0');
        var versionBytes = new byte[5]
        {
            (byte)versionBuff[0], (byte)versionBuff[2], (byte)versionBuff[3], (byte)versionBuff[5], (byte)versionBuff[6],
        };

        string serial = ini.GetString("GameServerInfo", "ServerSerial", "SharpSSeMU99B-v1");
        var serialBytes = new byte[17];
        System.Text.Encoding.ASCII.GetBytes(serial.PadRight(17, '\0')[..17]).CopyTo(serialBytes, 0);

        return new GameServerConfig
        {
            // Defaults equal to MuServer99B/GameServer/DATA/GameServerInfo - Common.dat (the real original
            // package), so that it works right away if the user forgets some value in the .ini.
            ServerName = ini.GetString("GameServerInfo", "ServerName", "SSeMU GameServer_0"),
            ServerCode = (ushort)ini.GetInt("GameServerInfo", "ServerCode", 0),
            ServerPort = (ushort)ini.GetInt("GameServerInfo", "ServerPort", 55900),
            ServerVersion = versionBytes,
            ServerSerial = serialBytes,
            ServerEncDecKey1 = (byte)ini.GetInt("GameServerInfo", "ServerEncDecKey1", 0),
            ServerEncDecKey2 = (byte)ini.GetInt("GameServerInfo", "ServerEncDecKey2", 0),
            ServerMaxUserNumber = ini.GetInt("GameServerInfo", "ServerMaxUserNumber", 1000),

            JoinServerAddress = ini.GetString("GameServerInfo", "JoinServerAddress", "127.0.0.1"),
            JoinServerPort = (ushort)ini.GetInt("GameServerInfo", "JoinServerPort", 55970),
            DataServerAddress = ini.GetString("GameServerInfo", "DataServerAddress", "127.0.0.1"),
            DataServerPort = (ushort)ini.GetInt("GameServerInfo", "DataServerPort", 55960),
            // WATCH OUT: this is the ConnectServer's UDP heartbeat port (ConnectServerPortUDP in its .ini), NOT
            // the TCP port 44405 the client connects to -- they are two different sockets on the ConnectServer
            // side.
            ConnectServerAddress = ini.GetString("GameServerInfo", "ConnectServerAddress", "127.0.0.1"),
            ConnectServerPort = (ushort)ini.GetInt("GameServerInfo", "ConnectServerPort", 55557),

            MaxConnectionIdle = ini.GetInt("GameServerInfo", "MaxConnectionIdle", 0),
            MaxConnectionPerIP = ini.GetInt("GameServerInfo", "MaxConnectionPerIP", 0),
            MaxPacketPerSecond = ini.GetInt("GameServerInfo", "MaxPacketPerSecond", 0),

            EncryptionKeyPath = Path.Combine(baseDir, "Hack", "Enc2.dat"),
            DecryptionKeyPath = Path.Combine(baseDir, "Hack", "Dec1.dat"),
            TerrainPath = ini.GetString("GameServerInfo", "TerrainPath", Path.Combine(baseDir, "Data", "Terrain")),
            MonsterListPath = ini.GetString("GameServerInfo", "MonsterListPath", Path.Combine(baseDir, "Data", "Monster", "MonsterList.txt")),
            MonsterSpawnPath = ini.GetString("GameServerInfo", "MonsterSpawnPath", Path.Combine(baseDir, "Data", "Monster", "Spawn")),
            EventPath = ini.GetString("GameServerInfo", "EventPath", Path.Combine(baseDir, "Data", "Event")),
            ItemPath = ini.GetString("GameServerInfo", "ItemPath", Path.Combine(baseDir, "Data", "Item", "Item.txt")),
            ItemValuePath = ini.GetString("GameServerInfo", "ItemValuePath", Path.Combine(baseDir, "Data", "Item", "ItemValue.txt")),
            CharacterInfoPath = ini.GetString("GameServerInfo", "CharacterInfoPath", Path.Combine(baseDir, "Data", "GameServerInfo - Character.dat")),
            ServerInfoCommonPath = ini.GetString("GameServerInfo", "ServerInfoCommonPath", Path.Combine(baseDir, "Data", "GameServerInfo - Common.dat")),
            ServerInfoEventPath = ini.GetString("GameServerInfo", "ServerInfoEventPath", Path.Combine(baseDir, "Data", "GameServerInfo - Event.dat")),
            ServerInfoChaosMixPath = ini.GetString("GameServerInfo", "ServerInfoChaosMixPath", Path.Combine(baseDir, "Data", "GameServerInfo - ChaosMix.dat")),
            ServerInfoCommandPath = ini.GetString("GameServerInfo", "ServerInfoCommandPath", Path.Combine(baseDir, "Data", "GameServerInfo - Command.dat")),
            ServerInfoItemDatPath = ini.GetString("GameServerInfo", "ServerInfoItemDatPath", Path.Combine(baseDir, "Data", "GameServerInfo - Item.dat")),
            ServerInfoSkillDatPath = ini.GetString("GameServerInfo", "ServerInfoSkillDatPath", Path.Combine(baseDir, "Data", "GameServerInfo - Skill.dat")),
            ServerInfoCustomPath = ini.GetString("GameServerInfo", "ServerInfoCustomPath", Path.Combine(baseDir, "Data", "GameServerInfo - Custom.dat")),
            SkillListPath = ini.GetString("GameServerInfo", "SkillListPath", Path.Combine(baseDir, "Data", "Skill", "SkillList.txt")),
            SkillDamagePath = ini.GetString("GameServerInfo", "SkillDamagePath", Path.Combine(baseDir, "Data", "Skill", "SkillDamage.txt")),
            ShopManagerPath = ini.GetString("GameServerInfo", "ShopManagerPath", Path.Combine(baseDir, "Data", "ShopManager.txt")),
            QuestPath = ini.GetString("GameServerInfo", "QuestPath", Path.Combine(baseDir, "Data", "Quest", "Quest.txt")),
            QuestObjectivePath = ini.GetString("GameServerInfo", "QuestObjectivePath", Path.Combine(baseDir, "Data", "Quest", "QuestObjective.txt")),
            QuestRewardPath = ini.GetString("GameServerInfo", "QuestRewardPath", Path.Combine(baseDir, "Data", "Quest", "QuestReward.txt")),
            ShopDataPath = ini.GetString("GameServerInfo", "ShopDataPath", Path.Combine(baseDir, "Data", "Shop")),
        };
    }
}
