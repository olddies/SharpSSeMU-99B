#pragma once

class CCharacterManager
{
public:
    CCharacterManager();
    virtual ~CCharacterManager();
    CLASS_TYPE ChangeServerClassTypeToClientClassType(const SERVER_CLASS_TYPE byServerClassType);

    /// Igual que la de arriba pero para 0.99B, que numera las clases distinto:
    /// SERVER_CLASS_TYPE es el empaquetado de Season 6 (clase * 4) y no sirve
    /// acá. Recibe la clase base (0-4) y la bandera de evolución ya desarmadas,
    /// que es como las entrega Mu099B::DecodeClassByte.
    CLASS_TYPE ChangeServer099BClassTypeToClientClassType(const BYTE byBaseClass,
                                                          const BYTE byChangeUp);

    /// Inversa, para crear personajes: devuelve el índice de clase base que
    /// espera 0.99B, sin evolución.
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
