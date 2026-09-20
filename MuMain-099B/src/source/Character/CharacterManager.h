#pragma once

class CCharacterManager
{
public:
    CCharacterManager();
    virtual ~CCharacterManager();
    CLASS_TYPE ChangeServerClassTypeToClientClassType(const SERVER_CLASS_TYPE byServerClassType);

    /// Same as the one above but for 0.99B, which numbers classes differently: SERVER_CLASS_TYPE is the Season
    /// 6 packing (class * 4) and is no use here. It takes the base class (0-4) and the evolution flag already
    /// unpacked, which is how Mu099B::DecodeClassByte hands them over.
    CLASS_TYPE ChangeServer099BClassTypeToClientClassType(const BYTE byBaseClass,
                                                          const BYTE byChangeUp);

    /// Inverse, for creating characters: returns the base class index that 0.99B expects, without evolution.
    BYTE ChangeClientClassTypeToServer099BBaseClass(const CLASS_TYPE byClientClassType);
    bool IsSecondClass(const CLASS_TYPE byClass);
    bool IsThirdClass(const CLASS_TYPE byClass);
    bool IsMasterLevel(const CLASS_TYPE byClass);
    bool IsMasterExperienceActive(const CLASS_TYPE byClass, const int level);
    CLASS_TYPE GetBaseClass(CLASS_TYPE iClass);
    const wchar_t* GetCharacterClassText(const CLASS_TYPE byClass);
    
    int IsFemale(CLASS_TYPE iClass) { return (this->GetBaseClass(iClass) == CLASS_ELF || this->GetBaseClass(iClass) == CLASS_SUMMONER); }
    CLASS_SKIN_INDEX GetSkinModelIndex(const CLASS_TYPE byClass);
    BYTE GetStepClass(const CLASS_TYPE byClass);
    int GetEquipedBowType(CHARACTER* pChar);
    int GetEquipedBowType();
    int GetEquipedBowType(ITEM* pItem);
    int GetEquipedBowType_Skill();
    bool IsEquipedWing();
    void GetMagicSkillDamage(int iType, int* piMinDamage, int* piMaxDamage);
    void GetCurseSkillDamage(int iType, int* piMinDamage, int* piMaxDamage);
    void GetSkillDamage(int iType, int* piMinDamage, int* piMaxDamage);
public:
};

extern CCharacterManager gCharacterManager;
