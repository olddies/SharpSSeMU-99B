namespace MuServer.AdminPanel.Repositories;

/// <summary>Un ajuste de la página de Tasas. <see cref="PerAccountLevel"/> marca las claves que en el
/// archivo están repetidas por nivel de cuenta (<c>_AL0</c>..<c>_AL3</c>): se muestran como un solo
/// campo y se escriben en los 4, porque el servidor no tiene sistema de cuentas premium (el nivel de
/// cuenta siempre vale 0) y dejar los otros 3 con valores distintos sólo genera confusión más
/// adelante. Quien necesite los 4 por separado los tiene en la página de Configuración.</summary>
public sealed record RateSetting(string Key, string Label, string Help, bool PerAccountLevel = false);

public sealed record RateGroup(string Title, string Icon, RateSetting[] Settings);

/// <summary>Los ajustes que un admin toca de verdad para configurar su servidor, sacados a mano de
/// <c>GameServerInfo - Common.dat</c> con nombre y explicación en castellano. No reemplaza a la
/// página de Configuración (que muestra los ~845 campos crudos): es el atajo a lo que se usa siempre.
/// </summary>
public static class RateSettings
{
    public const string FileName = "GameServerInfo - Common.dat";
    public const string Section = "GameServerInfo";

    public static readonly RateGroup[] Groups =
    [
        new("Experiencia", "exp",
        [
            new("AddExperienceRate", "Multiplicador de experiencia",
                "El clásico \"servidor x10\": multiplica la experiencia de cada monstruo. 1 = normal.", true),
            new("AddEventExperienceRate", "Multiplicador de experiencia en eventos",
                "Igual que el anterior pero para los eventos (Devil Square, Blood Castle...).", true),
            new("MaxLevel", "Nivel máximo",
                "Tope de nivel de personaje. Al llegar acá deja de acumular experiencia."),
            new("MaxLevelUp", "Niveles máximos por golpe de experiencia",
                "Cuántos niveles puede subir de una sola vez. Importante en servidores con exp muy alta."),
            new("MaxLevelUpEvent", "Niveles máximos por golpe (eventos)",
                "Lo mismo, pero para la experiencia ganada en eventos."),
            new("ExperienceMultiplierConstA", "Constante A de la fórmula de experiencia",
                "Parte de la fórmula original que calcula cuánta experiencia da cada monstruo. Tocalo sólo si sabés qué hace."),
            new("ExperienceMultiplierConstB", "Constante B de la fórmula de experiencia",
                "Idem: es la otra constante de la fórmula original."),
        ]),

        new("Drop de items", "drop",
        [
            new("ItemDropRate", "Probabilidad de drop de item",
                "Chance de que un monstruo tire un item al morir (sobre 1000: 100 = 10%).", true),
            new("ItemDropTime", "Segundos que el item queda en el piso",
                "Pasado ese tiempo el item desaparece del suelo."),
            new("MaxItemOption", "Opciones máximas de item",
                "Tope de opciones adicionales que puede tener un item que dropea."),
        ]),

        new("Drop de zen", "zen",
        [
            new("MoneyAmountDropRate", "Multiplicador de zen",
                "Cuánto zen tira cada monstruo, en porcentaje: 100 = normal, 200 = el doble.", true),
            new("MoneyDropTime", "Segundos que el zen queda en el piso",
                "Igual que el de items, pero para las pilas de zen."),
        ]),

        new("Joyas y mejora de items", "jewel",
        [
            new("SoulSuccessRate", "Éxito de Jewel of Soul (%)",
                "Probabilidad de que la joya suba el nivel del item en vez de romperlo.", true),
            new("LifeSuccessRate", "Éxito de Jewel of Life (%)",
                "Probabilidad de que la Jewel of Life agregue nivel de opción.", true),
            new("AddLuckSuccessRate1", "Éxito de suerte 1 (%)",
                "Probabilidad base de agregar el atributo Luck.", true),
            new("AddLuckSuccessRate2", "Éxito de suerte 2 (%)",
                "La segunda tasa de suerte; también la usa la mezcla de +10/+11/+12/+13 de la Chaos Machine.", true),
        ]),

        new("Fruta (puntos de stat)", "fruit",
        [
            new("FruitAddPointMin", "Puntos mínimos que da la fruta",
                "Cuando la fruta sale bien, el mínimo de puntos que suma."),
            new("FruitAddPointMax", "Puntos máximos que da la fruta",
                "El máximo del mismo rango."),
            new("FruitAddPointSuccessRate", "Éxito de la fruta (%)",
                "Probabilidad de que la fruta sume puntos en vez de fallar.", true),
        ]),

        new("Puntos por nivel", "points",
        [
            new("DWLevelUpPoint", "Puntos por nivel -- Dark Wizard", "Puntos de stat que da cada nivel.", true),
            new("DKLevelUpPoint", "Puntos por nivel -- Dark Knight", "Puntos de stat que da cada nivel.", true),
            new("FELevelUpPoint", "Puntos por nivel -- Fairy Elf", "Puntos de stat que da cada nivel.", true),
            new("MGLevelUpPoint", "Puntos por nivel -- Magic Gladiator", "Puntos de stat que da cada nivel.", true),
            new("DLLevelUpPoint", "Puntos por nivel -- Dark Lord", "Puntos de stat que da cada nivel.", true),
            new("MaxStatPoint", "Tope por stat", "Máximo que puede alcanzar un stat individual.", true),
            new("PlusStatPoint", "Puntos extra", "Puntos adicionales que se otorgan a partir del nivel de abajo."),
            new("PlusStatMinLevel", "Nivel para los puntos extra", "A partir de qué nivel se dan esos puntos extra."),
        ]),

        new("Monstruos (multiplicadores globales)", "monster",
        [
            new("MonsterMaxLifeRate", "Vida de los monstruos (%)",
                "Se aplica sobre la vida de MonsterList.txt: 100 = tal cual el archivo, 200 = el doble."),
            new("MonsterPhysiDamageRate", "Daño de los monstruos (%)", "Idem, sobre el daño físico."),
            new("MonsterDefenseRate", "Defensa de los monstruos (%)", "Idem, sobre la defensa."),
            new("MonsterDefenseSuccessRateRate", "Evasión de los monstruos (%)", "Idem, sobre la tasa de evasión."),
            new("MonsterAttackSuccessRateRate", "Acierto de los monstruos (%)", "Idem, sobre la tasa de acierto."),
        ]),

        new("Durabilidad", "durability",
        [
            new("WeaponDurabilityRate", "Durabilidad de armas (%)", "Cuánto aguanta antes de gastarse: más alto = dura más."),
            new("ArmorDurabilityRate", "Durabilidad de armaduras (%)", "Idem para las piezas de armadura."),
            new("WingDurabilityRate", "Durabilidad de alas (%)", "Idem para las alas."),
            new("PendantDurabilityRate", "Durabilidad de pendientes (%)", "Idem para pendientes."),
            new("RingDurabilityRate", "Durabilidad de anillos (%)", "Idem para anillos."),
            new("PetDurabilityRate", "Durabilidad de mascotas (%)", "Idem para mascotas."),
            new("GuardianDurabilityRate", "Durabilidad de guardianes (%)", "Idem para los guardianes."),
        ]),

        new("Personajes", "character",
        [
            new("CharacterCreateSwitch", "Permitir crear personajes", "0 = desactivado, 1 = activado."),
            new("CharacterDeleteSwitch", "Permitir borrar personajes", "0 = desactivado, 1 = activado."),
            new("CharacterDeleteMaxLevel", "Nivel máximo para borrar", "Arriba de este nivel ya no se puede borrar el personaje."),
            new("MGCreateLevel", "Nivel para crear Magic Gladiator", "Nivel que tiene que tener otro personaje de la cuenta.", true),
            new("DLCreateLevel", "Nivel para crear Dark Lord", "Idem para el Dark Lord.", true),
        ]),
    ];
}
