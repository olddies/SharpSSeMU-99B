using MuServer.Shared.Config;

namespace MuServer.GameServer.Config;

/// <summary> PARTIAL port of <c>CServerInfo</c> (ServerInfo.h/.cpp, ~520 fields across 7 real INI files:
/// <c>GameServerInfo - {ChaosMix,Command,Common,Custom,Event,Item,Skill}.dat</c>, all plain text despite the
/// .dat extension, read with GetPrivateProfileInt/String just like <see cref="IniFile"/>). This class ONLY
/// loads the fields of <c>Common.dat</c> and <c>Event.dat</c> that have a real consumer already ported in this
/// project (verified by grepping each <c>gServerInfo.m_Xxx</c> against the whole source code, not just where
/// the .dat is read). The rest of the fields of those 2 files (PK, Trade, Duel, Guild, Jewel, Fruit, Quest,
/// group experience sharing) and the other 5 files in full stay UNPORTED because the system that would consume
/// them does not exist yet in this port: - <c>ChaosMix.dat</c> (~90 fields): item combination/mix rates (Chaos
/// Mix, 2nd-gen wings, Dinorant, Fruit, pets) -- the Chaos Mix NPC/mechanic is not ported. - <c>Command.dat</c>
/// (~90 fields): configuration of <c>/reset</c> and <c>/masterreset</c> (points, daily/weekly/monthly limits,
/// per-class requirements) -- the reset system does not exist. - <c>Custom.dat</c>: Custom Arena/Attack/Pick
/// (SSeMU custom events, they are not even <c>CServerInfo</c> -- 3 classes of their own read them,
/// <c>CCustomArena</c>/<c>CCustomAttack</c>/ <c>CCustomPick</c>) -- none of the 3 is ported. - <c>Item.dat</c>:
/// Transformation Ring constants, Satan/Dinorant/Angel/Dark Horse damage, potion rates (small-medium-large
/// Apple/Life/Mana) per class, Ale/Olive/Love Remedy -- none of these special items/effects is implemented as a
/// game system. - <c>Skill.dat</c>: Mana Shield constants (absorbed damage/time/rate per class) -- the Mana
/// Shield skill is not ported (<see cref="SkillInfoTable"/> only covers attack skills). - From
/// <c>Common.dat</c>: PK (the whole points/announcements/limits system), Trade/PersonalShop/Duel/ Guild
/// switches (those systems do not exist), Jewel (Soul/Life/Luck success rates -- crafting jewels is not
/// ported), Fruit (Fruit of Life/Power -- not ported), Quest (<c>QuestMonsterItemDropParty</c> -- quest system
/// not ported), <c>PartyGeneralExperience</c>/<c>PartySpecialExperience</c>/<c>PartyMaxGapLevel</c> (this
/// port's party experience sharing, <see cref="World.PlayerObject"/> via <c>GrantPartyExperienceAsync</c>, uses
/// a structurally different formula from the real <c>CharacterCalcExperienceParty</c> --
/// ObjectManager.cpp:895-970 -- reconciling it is separate work, plugging in these 3 constants is not enough).
/// - From <c>Event.dat</c>: everything about Blood Castle/Chaos Castle/Bonus Manager/Drop Event/Invasion
/// Manager (those events are not ported) -- of Devil Square only <see cref="DevilSquareMaxUser"/> is used, the
/// rest (<c>DevilSquareMaxEntryCount_AL0-3</c>, daily cap per account) is not tracked either. All the
/// <c>_AL0-3</c> are per "AccountLevel" (account/VIP level 0-3, ObjectManager.cpp) -- indexed with <see
/// cref="World.PlayerObject.AccountLevel"/>, which JoinServer really computes (WZ_GetAccountLevel) and sends to
/// the GameServer when the account connects. </summary>
public sealed class ServerInfoConfig
{
    // ---- Common.dat: CServerInfo::ReadStartupInfo (ServerInfo.cpp:382-447) ----

    /// <summary>Port of gObjSetExperienceTable (User.cpp:276-297): together with <see
    /// cref="ExperienceMultiplierConstB"/> it builds the real required-experience table per level,
    /// <c>gLevelExperience[n] = (n+9)*n*n*ConstA</c> (plus an extra term with ConstB for levels above 255) --
    /// it replaces the <c>level²*1000</c> placeholder that <see
    /// cref="Protocol.WorldPacketBuilder.NextExperience"/> had before this file was ported.</summary>
    public int ExperienceMultiplierConstA { get; private set; } = 10;

    public int ExperienceMultiplierConstB { get; private set; } = 1000;

    /// <summary>Pet experience table (gPetExperience) -- loaded for completeness, this port has no pets
    /// yet.</summary>
    public int PetExperienceMultiplierConstA { get; private set; } = 100;

    public int MaxLevel { get; private set; } = 400;

    public int MaxPetLevel { get; private set; } = 50;

    /// <summary>Port of CObjectManager::CharacterLevelUp (ObjectManager.cpp:983-1041) -- cap on the levels that
    /// a single experience event (one monster kill) can raise at once. On reaching the cap, ALL the leftover
    /// experience of that event is discarded -- it is not saved for the next kill (<c>AddExperience -=
    /// (((--MaxLevelUp)==0)?AddExperience:...)</c>). Before porting this field, the port let as many levels be
    /// gained as the experience won in a single kill reached, with no limit or discard.</summary>
    public int MaxLevelUp { get; private set; } = 1;

    // ---- Common.dat: CServerInfo::ReadCommonInfo (ServerInfo.cpp:1104-1328), solo lo consumido ----

    /// <summary>Port of CMonsterManager::SetInfo (MonsterManager.cpp:166-183) -- global server multipliers
    /// applied over the raw MonsterList.txt columns when spawning (<see
    /// cref="World.MonsterRegistry.SpawnAll"/>), all as a percentage (100 = no change).</summary>
    public int MonsterMaxLifeRate { get; private set; } = 100;
    public int MonsterDefenseRate { get; private set; } = 100;
    public int MonsterDefenseSuccessRateRate { get; private set; } = 100;
    public int MonsterPhysiDamageRate { get; private set; } = 100;
    public int MonsterAttackSuccessRateRate { get; private set; } = 100;

    /// <summary>Port of CharacterCalcExperienceAlone (ObjectManager.cpp:845) -- DIRECT multiplier (not a
    /// percentage, there is no "/100") over the already computed experience of a solo kill. Indexed by
    /// AccountLevel (PlayerObject.AccountLevel).</summary>
    public int[] AddExperienceRate { get; private set; } = { 1, 1, 1, 1 };
    public int[] MoneyAmountDropRate { get; private set; } = { 100, 100, 100, 100 };

    /// <summary>ItemDropRate_AL0..3: a monster drops an item when <c>random(monster ItemRate) &lt; ItemDropRate</c>
    /// (times the map's rate), so the larger it is the more kills drop one -- at or above the monster's ItemRate, all
    /// of them.</summary>
    public int[] ItemDropRate { get; private set; } = { 100, 100, 100, 100 };

    /// <summary>Port of m_ItemDropTime/m_MoneyDropTime (Common.dat) -- lifetime (seconds) of an item/money
    /// dropped on the ground before it disappears on its own; the owner's loot-lock lasts HALF this time
    /// (<c>m_ItemDropTime*500</c> in ms, MapItem.cpp:29-99) -- see <see cref="World.GroundItem"/>. FIXED:
    /// before porting this file the port used a hardcoded 60s/ 30s (double the real one).</summary>
    public int ItemDropTimeSeconds { get; private set; } = 30;
    public int MoneyDropTimeSeconds { get; private set; } = 30;

    // ---- Event.dat: CServerInfo::ReadEventInfo (ServerInfo.cpp:1330-1372), solo Devil Square ----

    /// <summary>Port of the real MAX_DS_USER (m_DevilSquareMaxUser) -- cap on participants per Devil Square
    /// bracket (<see cref="World.DevilSquareManager"/>). FIXED: before porting this file the port had a
    /// hardcoded cap of 50 (arbitrary); the real one is 15.</summary>
    public int DevilSquareMaxUser { get; private set; } = 15;

    /// <summary>BloodCastleEvent: the whole event on or off. BloodCastleMaxUser: players per castle.
    /// BloodCastleMaxEntryCount_ALx: entries per day by account level (0 = no limit). BloodCastleNPCWithoutEntrance:
    /// the Archangel's messenger talks to a player who has no cloak.</summary>
    public bool BloodCastleEvent { get; private set; } = true;
    public int BloodCastleMaxUser { get; private set; } = 10;
    public int[] BloodCastleMaxEntryCount { get; private set; } = new int[4];
    public bool BloodCastleNpcWithoutEntrance { get; private set; }

    /// <summary>The daily entry caps of the other two entry events by account level (0 = no limit).</summary>
    public int[] DevilSquareMaxEntryCount { get; private set; } = new int[4];
    public int[] ChaosCastleMaxEntryCount { get; private set; } = new int[4];

    /// <summary>PKLimitFree (everybody may do everything) and PKLimitEventEntry (a murderer may not enter an event)
    /// from Common.dat.</summary>
    public int PKLimitFree { get; internal set; }
    public int PKLimitEventEntry { get; internal set; }

    /// <summary>True when a player's PK level keeps them out of the events: the check every event's entry makes,
    /// <c>PKLimitFree == 0 &amp;&amp; PKLevel &gt; 4 &amp;&amp; PKLimitEventEntry != 0</c>.</summary>
    public bool PkBlocksEventEntry(int pkLevel) => PKLimitFree == 0 && pkLevel > 4 && PKLimitEventEntry != 0;

    /// <summary>True when the player has used up the entries the cap allows for the day.</summary>
    public static bool EntryCapReached(int[] caps, int accountLevel, int entries)
    {
        int cap = caps[Math.Clamp(accountLevel, 0, caps.Length - 1)];
        return cap != 0 && entries >= cap;
    }

    /// <summary>DropEventSwitch: the drop events of DropEvent.dat run.</summary>
    public bool DropEventSwitch { get; private set; } = true;

    public static ServerInfoConfig Load(string commonPath, string eventPath)
    {
        var common = IniFile.Load(commonPath);
        var evt = IniFile.Load(eventPath);
        var cfg = new ServerInfoConfig
        {
            ExperienceMultiplierConstA = common.GetInt("GameServerInfo", "ExperienceMultiplierConstA", 10),
            ExperienceMultiplierConstB = common.GetInt("GameServerInfo", "ExperienceMultiplierConstB", 1000),
            PetExperienceMultiplierConstA = common.GetInt("GameServerInfo", "PetExperienceMultiplierConstA", 100),
            MaxLevel = common.GetInt("GameServerInfo", "MaxLevel", 400),
            MaxPetLevel = common.GetInt("GameServerInfo", "MaxPetLevel", 50),
            MaxLevelUp = common.GetInt("GameServerInfo", "MaxLevelUp", 1),

            MonsterMaxLifeRate = common.GetInt("GameServerInfo", "MonsterMaxLifeRate", 100),
            MonsterDefenseRate = common.GetInt("GameServerInfo", "MonsterDefenseRate", 100),
            MonsterDefenseSuccessRateRate = common.GetInt("GameServerInfo", "MonsterDefenseSuccessRateRate", 100),
            MonsterPhysiDamageRate = common.GetInt("GameServerInfo", "MonsterPhysiDamageRate", 100),
            MonsterAttackSuccessRateRate = common.GetInt("GameServerInfo", "MonsterAttackSuccessRateRate", 100),

            AddExperienceRate = new[]
            {
                common.GetInt("GameServerInfo", "AddExperienceRate_AL0", 1),
                common.GetInt("GameServerInfo", "AddExperienceRate_AL1", 1),
                common.GetInt("GameServerInfo", "AddExperienceRate_AL2", 1),
                common.GetInt("GameServerInfo", "AddExperienceRate_AL3", 1),
            },

            MoneyAmountDropRate = new[]
            {
                common.GetInt("GameServerInfo", "MoneyAmountDropRate_AL0", 100),
                common.GetInt("GameServerInfo", "MoneyAmountDropRate_AL1", 100),
                common.GetInt("GameServerInfo", "MoneyAmountDropRate_AL2", 100),
                common.GetInt("GameServerInfo", "MoneyAmountDropRate_AL3", 100),
            },

            ItemDropRate = new[]
            {
                common.GetInt("GameServerInfo", "ItemDropRate_AL0", 100),
                common.GetInt("GameServerInfo", "ItemDropRate_AL1", 100),
                common.GetInt("GameServerInfo", "ItemDropRate_AL2", 100),
                common.GetInt("GameServerInfo", "ItemDropRate_AL3", 100),
            },

            ItemDropTimeSeconds = common.GetInt("GameServerInfo", "ItemDropTime", 30),
            MoneyDropTimeSeconds = common.GetInt("GameServerInfo", "MoneyDropTime", 30),

            DevilSquareMaxUser = evt.GetInt("GameServerInfo", "DevilSquareMaxUser", 15),

            BloodCastleEvent = evt.GetInt("GameServerInfo", "BloodCastleEvent", 1) != 0,
            BloodCastleMaxUser = evt.GetInt("GameServerInfo", "BloodCastleMaxUser", 10),
            BloodCastleMaxEntryCount = Enumerable.Range(0, 4).Select(n => evt.GetInt("GameServerInfo", $"BloodCastleMaxEntryCount_AL{n}", 0)).ToArray(),
            BloodCastleNpcWithoutEntrance = evt.GetInt("GameServerInfo", "BloodCastleNPCWithoutEntrance", 0) != 0,
            DevilSquareMaxEntryCount = Enumerable.Range(0, 4).Select(n => evt.GetInt("GameServerInfo", $"DevilSquareMaxEntryCount_AL{n}", 0)).ToArray(),
            ChaosCastleMaxEntryCount = Enumerable.Range(0, 4).Select(n => evt.GetInt("GameServerInfo", $"ChaosCastleMaxEntryCount_AL{n}", 0)).ToArray(),
            PKLimitFree = common.GetInt("GameServerInfo", "PKLimitFree", 0),
            PKLimitEventEntry = common.GetInt("GameServerInfo", "PKLimitEventEntry", 0),
            DropEventSwitch = evt.GetInt("GameServerInfo", "DropEventSwitch", 1) != 0,
        };

        return cfg;
    }
}
