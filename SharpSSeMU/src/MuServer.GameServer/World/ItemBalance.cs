using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary>
/// Puerto de ITEM_INFO (ItemManager.h) -- una fila de balance de Data/Item/Item.txt (Fase 4, segunda
/// pasada: balance real de combate). El layout de columnas varía por sección (0-15 = categoría del
/// item, ver comentario de cabecera de <see cref="ItemBalanceTable.Load"/>); esta clase junta todos
/// los campos posibles de cualquier sección en un solo tipo (como MonsterInfo), dejando en 0/vacío
/// los que no aplican a la sección de esa fila en particular.
///
/// OJO con <see cref="Level"/>: es la columna "Level" de la FILA de balance (una especie de "nivel de
/// poder" fijo del tipo de item, ej. "Kris" Level=6), completamente distinto de <see cref="Item.Level"/>
/// (el nivel de mejora +0..+15 de la instancia concreta del item en el inventario de un jugador).
/// </summary>
public sealed class ItemBalance
{
    public required int Index { get; init; } // Item.GetItem(Section, Sub)
    public required int Section { get; init; }
    public required int Sub { get; init; }
    public int Slot { get; init; } // -1 = cualquier slot ('*' en el archivo, ej. pociones/joyas)
    public int Skill { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool HaveSerial { get; init; }
    public bool HaveOption { get; init; }
    public bool DropItem { get; init; }
    public string Name { get; init; } = string.Empty;

    public int Level { get; init; }
    public int DamageMin { get; init; }
    public int DamageMax { get; init; }
    public int AttackSpeed { get; init; }
    public int Durability { get; init; } // durabilidad de fábrica (no confundir con Item.Durability, la actual)
    public int MagicDurability { get; init; }
    public int MagicDamageRate { get; init; }
    public int Defense { get; init; }
    public int DefenseSuccessRate { get; init; }
    public int MagicDefense { get; init; }
    public int WalkSpeed { get; init; }
    public int Value { get; init; } // sección 14 (joyas/pociones): precio de tienda

    public int RequireLevel { get; init; }
    public int RequireStrength { get; init; }
    public int RequireDexterity { get; init; }
    public int RequireEnergy { get; init; }
    public int RequireVitality { get; init; }
    public int RequireLeadership { get; init; }
    public int BuyMoney { get; init; }
    public int SetAttr { get; init; }
    public int[] Resistance { get; init; } = new int[7]; // sección 13: hielo/veneno/rayo/fuego/tierra/viento/agua

    /// <summary>Uso por clase (DW,DK,FE,MG,DL, en ese orden) -- el archivo trae valores 0/1/2 (0=no
    /// puede usarlo, 1/2=sí, el 2 aparece en alas/joyas de 2da generación). Se guarda crudo tal cual
    /// para cuando la validación de equipo se porte de verdad; el balance de daño/defensa de esta
    /// pasada no lo necesita.</summary>
    public int[] RequireClass { get; init; } = new int[5];

    public bool IsWeapon => Section is >= 0 and <= 5;
    public bool IsShield => Section == 6;

    /// <summary>Puerto de TwoHand (derivado, ItemManager.cpp: TwoHand = Width>=2) -- solo tiene
    /// sentido para armas (secciones 0-5).</summary>
    public bool TwoHand => IsWeapon && Width >= 2;

    /// <summary>Munición (flecha/perno, sección 4 pero sin daño propio: DamageMin=DamageMax=0 en
    /// Item.txt) -- se equipa en la mano izquierda junto a un arco/ballesta y NO cuenta como "arma"
    /// para las reglas de doble-empuñadura/daño de mano izquierda; su nivel de mejora sí aporta un
    /// bono porcentual al daño del arco (ver PlayerObject.RecalcCombatStats).</summary>
    public bool IsAmmo => Section == 4 && DamageMin == 0 && DamageMax == 0;
}

/// <summary>
/// Puerto de CItemManager::Load (ItemManager.cpp:52-259) -- lee Data/Item/Item.txt (formato MemScript,
/// 16 secciones numeradas 0-15, cada una con su propio layout de columnas después de las 9 columnas
/// comunes; layout confirmado línea por línea contra los comentarios de cabecera del archivo real).
/// No incluye el sistema de niveles de excelencia/opciones (ItemOption.txt/SetItemOption.txt) ni el
/// escalado por nivel +0..+15 (eso vive en <see cref="ItemCombatMath"/>, aplicado sobre esta tabla).
/// </summary>
public sealed class ItemBalanceTable
{
    private readonly Dictionary<int, ItemBalance> _byIndex = new();

    public int Count => _byIndex.Count;

    public ItemBalance? Get(int index) => _byIndex.GetValueOrDefault(index);

    public IEnumerable<ItemBalance> All => _byIndex.Values;

    public int Load(string path)
    {
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[ItemBalanceTable] {0}", script.GetLastError());
            return 0;
        }

        while (true)
        {
            if (script.GetToken() == TokenResult.End)
            {
                break;
            }

            int section = script.GetNumber();

            while (true)
            {
                if (script.GetToken() == TokenResult.End)
                {
                    break;
                }

                if (script.GetString() == "end")
                {
                    break;
                }

                int sub = script.GetNumber();
                int slot = script.GetAsNumber();
                int skill = script.GetAsNumber();
                int width = script.GetAsNumber();
                int height = script.GetAsNumber();
                int haveSerial = script.GetAsNumber();
                int haveOption = script.GetAsNumber();
                int dropItem = script.GetAsNumber();
                string name = script.GetAsString();

                var common = new
                {
                    Index = Item.GetItem(section, sub), Section = section, Sub = sub, Slot = slot, Skill = skill,
                    Width = width, Height = height, HaveSerial = haveSerial != 0, HaveOption = haveOption != 0,
                    DropItem = dropItem != 0, Name = name,
                };

                switch (section)
                {
                    case >= 0 and <= 5: // armas: espada/hacha/maza/lanza/arco/báculo
                    {
                        int level = script.GetAsNumber();
                        int damageMin = script.GetAsNumber();
                        int damageMax = script.GetAsNumber();
                        int attackSpeed = script.GetAsNumber();
                        int durability = script.GetAsNumber();
                        int magicDurability = script.GetAsNumber();
                        int magicDamageRate = script.GetAsNumber();
                        int reqLevel = script.GetAsNumber();
                        int reqStr = script.GetAsNumber();
                        int reqDex = script.GetAsNumber();
                        int reqEne = script.GetAsNumber();
                        int reqVit = script.GetAsNumber();
                        int reqLead = script.GetAsNumber();
                        int setAttr = script.GetAsNumber();
                        var reqClass = ReadClassRow(script);

                        _byIndex[common.Index] = new ItemBalance
                        {
                            Index = common.Index, Section = common.Section, Sub = common.Sub, Slot = common.Slot,
                            Skill = common.Skill, Width = common.Width, Height = common.Height,
                            HaveSerial = common.HaveSerial, HaveOption = common.HaveOption, DropItem = common.DropItem,
                            Name = common.Name, Level = level, DamageMin = damageMin, DamageMax = damageMax,
                            AttackSpeed = attackSpeed, Durability = durability > 0 ? durability : magicDurability, MagicDurability = magicDurability,
                            MagicDamageRate = magicDamageRate, RequireLevel = reqLevel, RequireStrength = reqStr,
                            RequireDexterity = reqDex, RequireEnergy = reqEne, RequireVitality = reqVit,
                            RequireLeadership = reqLead, SetAttr = setAttr, RequireClass = reqClass,
                        };
                        break;
                    }

                    case 6: // escudo
                    {
                        int level = script.GetAsNumber();
                        int defense = script.GetAsNumber();
                        int defenseSuccessRate = script.GetAsNumber();
                        int durability = script.GetAsNumber();
                        int reqLevel = script.GetAsNumber();
                        int reqStr = script.GetAsNumber();
                        int reqDex = script.GetAsNumber();
                        int reqEne = script.GetAsNumber();
                        int reqVit = script.GetAsNumber();
                        int reqLead = script.GetAsNumber();
                        int setAttr = script.GetAsNumber();
                        var reqClass = ReadClassRow(script);

                        _byIndex[common.Index] = new ItemBalance
                        {
                            Index = common.Index, Section = common.Section, Sub = common.Sub, Slot = common.Slot,
                            Skill = common.Skill, Width = common.Width, Height = common.Height,
                            HaveSerial = common.HaveSerial, HaveOption = common.HaveOption, DropItem = common.DropItem,
                            Name = common.Name, Level = level, Defense = defense, DefenseSuccessRate = defenseSuccessRate,
                            Durability = durability, RequireLevel = reqLevel, RequireStrength = reqStr,
                            RequireDexterity = reqDex, RequireEnergy = reqEne, RequireVitality = reqVit,
                            RequireLeadership = reqLead, SetAttr = setAttr, RequireClass = reqClass,
                        };
                        break;
                    }

                    case 7 or 8 or 9: // casco/armadura/pantalón
                    {
                        int level = script.GetAsNumber();
                        int defense = script.GetAsNumber();
                        int magicDefense = script.GetAsNumber();
                        int durability = script.GetAsNumber();
                        int reqLevel = script.GetAsNumber();
                        int reqStr = script.GetAsNumber();
                        int reqDex = script.GetAsNumber();
                        int reqEne = script.GetAsNumber();
                        int reqVit = script.GetAsNumber();
                        int reqLead = script.GetAsNumber();
                        int setAttr = script.GetAsNumber();
                        var reqClass = ReadClassRow(script);

                        _byIndex[common.Index] = new ItemBalance
                        {
                            Index = common.Index, Section = common.Section, Sub = common.Sub, Slot = common.Slot,
                            Skill = common.Skill, Width = common.Width, Height = common.Height,
                            HaveSerial = common.HaveSerial, HaveOption = common.HaveOption, DropItem = common.DropItem,
                            Name = common.Name, Level = level, Defense = defense, MagicDefense = magicDefense,
                            Durability = durability, RequireLevel = reqLevel, RequireStrength = reqStr,
                            RequireDexterity = reqDex, RequireEnergy = reqEne, RequireVitality = reqVit,
                            RequireLeadership = reqLead, SetAttr = setAttr, RequireClass = reqClass,
                        };
                        break;
                    }

                    case 10: // guantes
                    {
                        int level = script.GetAsNumber();
                        int defense = script.GetAsNumber();
                        int attackSpeed = script.GetAsNumber();
                        int durability = script.GetAsNumber();
                        int reqLevel = script.GetAsNumber();
                        int reqStr = script.GetAsNumber();
                        int reqDex = script.GetAsNumber();
                        int reqEne = script.GetAsNumber();
                        int reqVit = script.GetAsNumber();
                        int reqLead = script.GetAsNumber();
                        int setAttr = script.GetAsNumber();
                        var reqClass = ReadClassRow(script);

                        _byIndex[common.Index] = new ItemBalance
                        {
                            Index = common.Index, Section = common.Section, Sub = common.Sub, Slot = common.Slot,
                            Skill = common.Skill, Width = common.Width, Height = common.Height,
                            HaveSerial = common.HaveSerial, HaveOption = common.HaveOption, DropItem = common.DropItem,
                            Name = common.Name, Level = level, Defense = defense, AttackSpeed = attackSpeed,
                            Durability = durability, RequireLevel = reqLevel, RequireStrength = reqStr,
                            RequireDexterity = reqDex, RequireEnergy = reqEne, RequireVitality = reqVit,
                            RequireLeadership = reqLead, SetAttr = setAttr, RequireClass = reqClass,
                        };
                        break;
                    }

                    case 11: // botas
                    {
                        int level = script.GetAsNumber();
                        int defense = script.GetAsNumber();
                        int walkSpeed = script.GetAsNumber();
                        int durability = script.GetAsNumber();
                        int reqLevel = script.GetAsNumber();
                        int reqStr = script.GetAsNumber();
                        int reqDex = script.GetAsNumber();
                        int reqEne = script.GetAsNumber();
                        int reqVit = script.GetAsNumber();
                        int reqLead = script.GetAsNumber();
                        int setAttr = script.GetAsNumber();
                        var reqClass = ReadClassRow(script);

                        _byIndex[common.Index] = new ItemBalance
                        {
                            Index = common.Index, Section = common.Section, Sub = common.Sub, Slot = common.Slot,
                            Skill = common.Skill, Width = common.Width, Height = common.Height,
                            HaveSerial = common.HaveSerial, HaveOption = common.HaveOption, DropItem = common.DropItem,
                            Name = common.Name, Level = level, Defense = defense, WalkSpeed = walkSpeed,
                            Durability = durability, RequireLevel = reqLevel, RequireStrength = reqStr,
                            RequireDexterity = reqDex, RequireEnergy = reqEne, RequireVitality = reqVit,
                            RequireLeadership = reqLead, SetAttr = setAttr, RequireClass = reqClass,
                        };
                        break;
                    }

                    case 12: // alas -- OJO: orden de columnas distinto (ReqEnergy antes que ReqStr/ReqDex), sin SetAttr
                    {
                        int level = script.GetAsNumber();
                        int defense = script.GetAsNumber();
                        int durability = script.GetAsNumber();
                        int reqLevel = script.GetAsNumber();
                        int reqEne = script.GetAsNumber();
                        int reqStr = script.GetAsNumber();
                        int reqDex = script.GetAsNumber();
                        int reqLead = script.GetAsNumber();
                        int buyMoney = script.GetAsNumber();
                        var reqClass = ReadClassRow(script);

                        _byIndex[common.Index] = new ItemBalance
                        {
                            Index = common.Index, Section = common.Section, Sub = common.Sub, Slot = common.Slot,
                            Skill = common.Skill, Width = common.Width, Height = common.Height,
                            HaveSerial = common.HaveSerial, HaveOption = common.HaveOption, DropItem = common.DropItem,
                            Name = common.Name, Level = level, Defense = defense, Durability = durability,
                            RequireLevel = reqLevel, RequireEnergy = reqEne, RequireStrength = reqStr,
                            RequireDexterity = reqDex, RequireLeadership = reqLead, BuyMoney = buyMoney,
                            RequireClass = reqClass,
                        };
                        break;
                    }

                    case 13: // mascotas/joyas de anillo-pendiente/misceláneo
                    {
                        int level = script.GetAsNumber();
                        int durability = script.GetAsNumber();
                        var resistance = new int[7];
                        for (int n = 0; n < 7; n++)
                        {
                            resistance[n] = script.GetAsNumber();
                        }

                        int setAttr = script.GetAsNumber();
                        var reqClass = ReadClassRow(script);

                        _byIndex[common.Index] = new ItemBalance
                        {
                            Index = common.Index, Section = common.Section, Sub = common.Sub, Slot = common.Slot,
                            Skill = common.Skill, Width = common.Width, Height = common.Height,
                            HaveSerial = common.HaveSerial, HaveOption = common.HaveOption, DropItem = common.DropItem,
                            Name = common.Name, Level = level, Durability = durability, Resistance = resistance,
                            SetAttr = setAttr, RequireClass = reqClass,
                        };
                        break;
                    }

                    case 14: // joyas/pociones/consumibles -- SOLO Value y Level, sin requisitos ni ReqClass
                    {
                        int value = script.GetAsNumber();
                        int level = script.GetAsNumber();

                        _byIndex[common.Index] = new ItemBalance
                        {
                            Index = common.Index, Section = common.Section, Sub = common.Sub, Slot = common.Slot,
                            Skill = common.Skill, Width = common.Width, Height = common.Height,
                            HaveSerial = common.HaveSerial, HaveOption = common.HaveOption, DropItem = common.DropItem,
                            Name = common.Name, Value = value, Level = level,
                        };
                        break;
                    }

                    case 15: // orbes/pergaminos
                    {
                        int level = script.GetAsNumber();
                        int reqLevel = script.GetAsNumber();
                        int reqEne = script.GetAsNumber();
                        int buyMoney = script.GetAsNumber();
                        var reqClass = ReadClassRow(script);

                        _byIndex[common.Index] = new ItemBalance
                        {
                            Index = common.Index, Section = common.Section, Sub = common.Sub, Slot = common.Slot,
                            Skill = common.Skill, Width = common.Width, Height = common.Height,
                            HaveSerial = common.HaveSerial, HaveOption = common.HaveOption, DropItem = common.DropItem,
                            Name = common.Name, Level = level, RequireLevel = reqLevel, RequireEnergy = reqEne,
                            BuyMoney = buyMoney, RequireClass = reqClass,
                        };
                        break;
                    }

                    default:
                        // Sección desconocida (el archivo real solo trae 0-15) -- se descarta el resto de la
                        // fila token por token para no desincronizar el resto del parseo.
                        while (true)
                        {
                            var s = script.GetAsString();
                            if (s.Length == 0)
                            {
                                break;
                            }
                        }
                        break;
                }
            }
        }

        Log.Add(LogColor.Blue, "[ItemBalanceTable] {0} items de balance cargados desde {1}", _byIndex.Count, path);
        return _byIndex.Count;
    }

    /// <summary>
    /// Puerto simplificado de CMonsterManager::GetMonsterItem (MonsterManager.cpp:284-320): entre
    /// todos los items con <c>DropItem=true</c> cuyo <see cref="ItemBalance.Level"/> "encaja" con el
    /// nivel del monstruo (<c>(ItemLevel+4) >= MonsterLevel &amp;&amp; (ItemLevel-2) &lt;= MonsterLevel</c>,
    /// fórmula exacta del original), elige uno al azar de forma uniforme. Simplificado: el original
    /// pre-arma una tabla de candidatos por nivel de monstruo al cargar Item.txt (m_MonsterItemInfo,
    /// hasta 100 candidatos/nivel) y filtra excelente/socket-elegibilidad en el momento del roll; acá
    /// se recalcula la lista de candidatos en cada roll con una pasada lineal sobre todos los items
    /// cargados (unas pocas centenas en un Item.txt real, insignificante en costo) y NO se filtra por
    /// elegibilidad excelente/socket (ese sistema de rareza-extra no está portado en esta pasada, ver
    /// README) -- cualquier item candidato puede salir como drop normal.
    /// </summary>
    public ItemBalance? PickRandomDropItem(int monsterLevel, Random rng)
    {
        var candidates = new List<ItemBalance>();

        foreach (var item in _byIndex.Values)
        {
            if (!item.DropItem)
            {
                continue;
            }

            if (item.Level + 4 >= monsterLevel && item.Level - 2 <= monsterLevel)
            {
                candidates.Add(item);
            }
        }

        if (candidates.Count == 0)
        {
            foreach (var item in _byIndex.Values)
            {
                if (item.DropItem && item.Level <= monsterLevel + 5)
                {
                    candidates.Add(item);
                }
            }
        }

        return candidates.Count == 0 ? null : candidates[rng.Next(candidates.Count)];
    }

    private static int[] ReadClassRow(MemScript script)
    {
        var reqClass = new int[5];
        for (int n = 0; n < 5; n++)
        {
            reqClass[n] = script.GetAsNumber();
        }
        return reqClass;
    }
}
