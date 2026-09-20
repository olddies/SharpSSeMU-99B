namespace MuServer.DataServer.Db;

public sealed record AccountSlots(byte MoveCnt, int ExtClass, string?[] Names); // Names.Length == 5

public sealed record CharacterListRow(int CLevel, int Class, byte[] Inventory, int CtlCode);

public sealed record CharacterFullRow(
    int CLevel, int Class, int LevelUpPoint, long Experience, int Strength, int Dexterity, int Vitality,
    int Energy, int Leadership, byte[] Inventory, byte[] MagicList, long Money, float Life, float MaxLife,
    float Mana, float MaxMana, float BP, float MaxBP, int MapNumber, int MapPosX, int MapPosY, int MapDir,
    int PkCount, int PkLevel, int PkTime, int CtlCode, byte[] Quest, int ChatLimitTime, byte[] EffectList,
    int FruitAddPoint, int FruitSubPoint);

public sealed record ResetInfo(int Reset, int ResetDay, int ResetWek, int ResetMon);

public sealed record MasterResetInfo(int MasterReset, int MasterResetDay, int MasterResetWek, int MasterResetMon);

public sealed record EventEntryInfo(int BCCount, int CCCount, int DSCount);

public sealed record OptionDataRow(byte[] SkillKey, int GameOption, int QKey, int WKey, int EKey, int ChatWindow);

public sealed record PetItemRow(int Level, long Experience);
