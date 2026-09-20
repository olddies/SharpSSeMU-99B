using MuServer.Shared.Config;

namespace MuServer.GameServer.Config;

/// <summary>
/// Puerto parcial de CServerInfo (ServerInfo.h/.cpp) — solo los campos necesarios para el núcleo de
/// conexión (Fase 1). CServerInfo original tiene ~500 campos de balance de juego (daño, drops,
/// resets, etc.) que se irán agregando en fases posteriores a medida que el sistema que los usa
/// (combate, items, comandos...) se vaya portando.
/// </summary>
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

    // Rutas de los 4 archivos GameServerInfo restantes (más Custom.dat) que antes NO se leían en
    // absoluto -- ver Config/GameServerInfoChaosMix.cs, GameServerInfoCommand.cs, GameServerInfoItem.cs,
    // GameServerInfoSkill.cs, GameServerInfoCustom.cs (cobertura completa de campos, sin consumidor
    // todavía para la mayoría, pero ya no hardcodeados: si el .dat real está en Data/, se lee tal cual).
    public required string ServerInfoChaosMixPath { get; init; } // Data/GameServerInfo - ChaosMix.dat
    public required string ServerInfoCommandPath { get; init; } // Data/GameServerInfo - Command.dat
    public required string ServerInfoItemDatPath { get; init; } // Data/GameServerInfo - Item.dat (distinto de ItemPath, que es Item/Item.txt)
    public required string ServerInfoSkillDatPath { get; init; } // Data/GameServerInfo - Skill.dat (distinto de SkillListPath/SkillDamagePath)
    public required string ServerInfoCustomPath { get; init; } // Data/GameServerInfo - Custom.dat
    public required string SkillListPath { get; init; } // Data/Skill/SkillList.txt (balance de skills)
    public required string SkillDamagePath { get; init; } // Data/Skill/SkillDamage.txt (multiplicador opcional por skill)
    public required string ShopManagerPath { get; init; } // Data/ShopManager.txt (NPCs de tienda -- ver World/Shop.cs)
    public required string ShopDataPath { get; init; } // Data/Shop (un .txt de items por NPC, referenciado por ShopManager.txt)

    // Data/Quest/*.txt (ver Config/QuestTable.cs, QuestObjectiveTable.cs, QuestRewardTable.cs) --
    // motor real de misiones (Sebina/235 = 2da clase, Marlon/229 = otra recompensa a nivel 220),
    // reemplaza una versión anterior que hardcodeaba esos NPCs con nivel/items adivinados en C#.
    public required string QuestPath { get; init; }
    public required string QuestObjectivePath { get; init; }
    public required string QuestRewardPath { get; init; }

    public static GameServerConfig Load(string iniPath, string baseDir)
    {
        var ini = IniFile.Load(iniPath);

        // Puerto exacto de ServerInfo.cpp (líneas 396-408): NO son los primeros 5 bytes crudos del
        // string -- el original arma m_ServerVersion tomando los índices {0,2,3,5,6} de un string
        // con puntos tipo "1.02.00" (para descartar los dos '.'). Con el valor real del paquete
        // ("1.02.00" en GameServerInfo - Common.dat) esto da los bytes ASCII {'1','0','2','0','0'}.
        // Copiar los primeros 5 bytes tal cual (como se hacía antes) da un ClientVersion incorrecto
        // que el cliente real rechazaría con resultado 6.
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
            // Defaults iguales a MuServer99B/GameServer/DATA/GameServerInfo - Common.dat (el paquete
            // original real), para que ande de una si al usuario se le olvida algún valor del .ini.
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
            // OJO: este es el puerto UDP de heartbeat de ConnectServer (ConnectServerPortUDP en su
            // .ini), NO el puerto TCP 44405 al que se conecta el cliente -- son dos sockets distintos
            // del lado ConnectServer.
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
