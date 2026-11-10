using MuServer.GameServer.Protocol;
using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary>The effects of EffectList.txt the server knows how to apply (eEffectNumber): buffs the Elf, the Dark Wizard, the
/// Dark Knight and the Dark Lord cast, the ones skills put on their targets, and the immunities.</summary>
public static class EffectIds
{
    public const int GreaterDamage = 1;
    public const int GreaterDefense = 2;
    public const int ManaShield = 4;
    public const int GreaterCriticalDamage = 5;
    public const int BpRecovery = 7;
    public const int GreaterLife = 8;
    public const int DisableMagic = 12;
    public const int Poison = 55;
    public const int Ice = 56;
    public const int IceArrow = 57;
    public const int FireSlash = 58;
    public const int PhysiDamageImmunity = 59;
    public const int MagicDamageImmunity = 60;
    public const int MonsterPhysiDamageImmunity = 63;
    public const int MonsterMagicDamageImmunity = 64;
}

/// <summary>A row of EffectList.txt (EFFECT_INFO): <see cref="Group"/> is what overwrites what (two effects of a group do not
/// stack), <see cref="Save"/> whether it is kept when the player leaves, <see cref="Type"/> 0 for the ones that die with the
/// player and 2 for those that run on the clock, <see cref="Flag"/> 1 for a debuff, <see cref="Count"/> the fixed duration in
/// seconds or -1 when the skill decides, and <see cref="Value"/> the fixed powers or -1.</summary>
public sealed record EffectInfo(int Index, int Group, int ItemIndex, string Name, int Save, int Type, int Flag, int Count, int[] Value);

/// <summary>Port of CEffectManager::Load (EffectManager.cpp:47): Data/Skill/EffectList.txt.</summary>
public sealed class EffectTable
{
    /// <summary>MAX_EFFECT: the indexes go from 0 to 127.</summary>
    public const int MaxEffect = 128;

    private readonly Dictionary<int, EffectInfo> _byIndex = new();

    public int Count => _byIndex.Count;

    public EffectInfo? Get(int index) => _byIndex.GetValueOrDefault(index);

    public void Add(EffectInfo info)
    {
        if (info.Index is >= 0 and < MaxEffect)
        {
            _byIndex[info.Index] = info;
        }
    }

    public int Load(string path)
    {
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[EffectTable] {0}", script.GetLastError());
            return 0;
        }

        while (true)
        {
            var token = script.GetToken();

            if (token == TokenResult.End || script.GetString() == "end")
            {
                break;
            }

            int index = script.GetNumber();
            int group = script.GetAsNumber();
            int itemIndex = script.GetAsNumber();

            if (itemIndex != -1)
            {
                itemIndex = Item.GetItem(itemIndex, script.GetAsNumber());
            }

            string name = script.GetAsString();
            int save = script.GetAsNumber();
            int type = script.GetAsNumber();
            int flag = script.GetAsNumber();
            int count = script.GetAsNumber();
            var value = new[] { script.GetAsNumber(), script.GetAsNumber(), script.GetAsNumber(), script.GetAsNumber() };

            Add(new EffectInfo(index, group, itemIndex, name, save, type, flag, count, value));
        }

        Log.Add(LogColor.Blue, "[EffectTable] {0} effect(s) loaded from {1}", Count, path);
        return Count;
    }
}

/// <summary>One of the sixteen effect slots of a player (CEffect). An empty one has index 0xFF.</summary>
public sealed class ActiveEffect
{
    public const int Slots = 16;
    public const byte Empty = 0xFF;

    public byte Index { get; private set; } = Empty;
    public byte Group { get; private set; }
    public byte Save { get; private set; }
    public byte Type { get; private set; }
    public byte Flag { get; private set; }

    /// <summary>For the effects that run on the clock (type 2): the moment they end, in seconds of the Unix epoch.</summary>
    public uint Time { get; private set; }

    /// <summary>Seconds left (zero for one that never ends).</summary>
    public uint Count { get; set; }

    public ushort[] Value { get; } = new ushort[4];

    public bool IsEffect => Index != Empty;

    /// <summary>The sixteen empty slots of a carrier.</summary>
    public static ActiveEffect[] CreateSlots()
    {
        var slots = new ActiveEffect[Slots];

        for (int n = 0; n < slots.Length; n++)
        {
            slots[n] = new ActiveEffect();
        }

        return slots;
    }

    public void Clear()
    {
        Index = Empty;
        Group = Save = Type = Flag = 0;
        Time = Count = 0;
        Array.Clear(Value);
    }

    /// <summary>Port of CEffect::Set (Effect.cpp:48): false if the slot is taken, or if a clock effect has already ended.</summary>
    public bool Set(byte index, byte group, byte save, byte type, byte flag, uint time, uint count, ushort v1, ushort v2, ushort v3, ushort v4, long nowUnix)
    {
        if (IsEffect)
        {
            return false;
        }

        if (type == 2)
        {
            time = count;
            long left = (long)count - nowUnix;

            if (left <= 0)
            {
                return false;
            }

            count = (uint)left;
        }

        Index = index;
        Group = group;
        Save = save;
        Type = type;
        Flag = flag;
        Time = time;
        Count = count;
        Value[0] = v1;
        Value[1] = v2;
        Value[2] = v3;
        Value[3] = v4;
        return true;
    }
}

/// <summary>What the active effects add to (or take from) a player's numbers (EFFECT_OPTION): the damage and defense a hit uses, the
/// damage a hit lets through, the maximum life. Worked out again from the effects every time one comes or goes.</summary>
public sealed class EffectBonus
{
    public int AddPhysiDamage;
    public int AddMagicDamage;
    public int AddCurseDamage;
    public int AddDefense;
    public int SubDefense;
    public int MulDefense;
    public int DivDefense;
    public int AddDamageReduction;
    public int AddCriticalDamage;
    public int MulMaxHP;

    /// <summary>The slowness the Ice effect puts on a creature (DelayLevel / DelayActionTime): how long its actions wait.</summary>
    public int DelayLevel;
    public int DelayActionTime;
}

/// <summary>Whatever can carry effects: a player or a monster (the original keeps the sixteen slots and the EffectOption on every
/// object, gObj[], and the skills that hurt put their effects on monsters as well as on players).</summary>
public interface IEffectCarrier
{
    int Index { get; }
    byte Map { get; }
    ActiveEffect[] ActiveEffects { get; }
    EffectBonus EffectBonus { get; }
}

/// <summary>
/// Port of CEffectManager (EffectManager.cpp): the buffs and debuffs a player carries. Skills put them on (Greater Defense on
/// a party member, Mana Shield on oneself...), they count down once a second and go, they change what a hit does through
/// <see cref="EffectBonus"/>, the client is told when one comes and goes (C1:07), those that are saved come back when the
/// player does, and dying ends the ones that are not kept.
/// </summary>
public sealed class EffectManager
{
    private readonly EffectTable _table;
    private readonly PlayerRegistry _players;

    public EffectManager(EffectTable table, PlayerRegistry players)
    {
        _table = table;
        _players = players;
    }

    /// <summary>EffectOverwriteMode: when not zero a new effect always replaces one of its group; when zero it does only if
    /// it is not weaker.</summary>
    public int OverwriteMode { get; set; }

    /// <summary>The clock, as seconds of the Unix epoch (tests move it).</summary>
    internal Func<long> Clock { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>The stats of a player must be worked out again (and the client told): an effect changed them.</summary>
    public Func<PlayerObject, CancellationToken, Task>? OnStatsChanged { get; set; }

    // ---------------------------------------------------------------- finding

    public ActiveEffect? Get(IEffectCarrier player, int index) =>
        player.ActiveEffects.FirstOrDefault(e => e.IsEffect && e.Index == index);

    public ActiveEffect? GetByGroup(IEffectCarrier player, int group) =>
        player.ActiveEffects.FirstOrDefault(e => e.IsEffect && e.Group == group);

    public bool Check(IEffectCarrier player, int index) => Get(player, index) != null;

    public bool CheckByGroup(IEffectCarrier player, int group) => GetByGroup(player, group) != null;

    // ---------------------------------------------------------------- adding and removing

    /// <summary>Port of CEffectManager::AddEffect (EffectManager.cpp:158). <paramref name="restoring"/> is the original's
    /// <c>type</c>: true when the effect comes back from the database, with its own count and powers; false for a skill, where
    /// the fixed duration and powers of EffectList.txt, when it has them, take over. False when nothing was added: no such
    /// effect, no free slot, or a stronger one of its group is running.</summary>
    public async Task<bool> AddAsync(IEffectCarrier target, bool restoring, int index, int count, ushort v1, ushort v2, ushort v3, ushort v4, CancellationToken ct)
    {
        var info = _table.Get(index);

        if (info == null)
        {
            return false;
        }

        long now = Clock();

        if (!restoring && info.Count != -1)
        {
            count = info.Type == 2 ? (int)(now + info.Count) : info.Count;
        }

        if (info.Value[0] != -1) v1 = (ushort)info.Value[0];
        if (info.Value[1] != -1) v2 = (ushort)info.Value[1];
        if (info.Value[2] != -1) v3 = (ushort)info.Value[2];
        if (info.Value[3] != -1) v4 = (ushort)info.Value[3];

        var current = GetByGroup(target, info.Group);

        if (current == null)
        {
            foreach (var slot in target.ActiveEffects)
            {
                if (slot.Set((byte)index, (byte)info.Group, (byte)info.Save, (byte)info.Type, (byte)info.Flag, 0, (uint)count, v1, v2, v3, v4, now))
                {
                    await SendStateAsync(target, 1, slot.Index, ct);
                    await InsertAsync(target, slot, ct);
                    return true;
                }
            }

            return false;
        }

        if (index != current.Index || OverwriteMode != 0
            || (v1 >= current.Value[0] && v2 >= current.Value[1] && v3 >= current.Value[2] && v4 >= current.Value[3]))
        {
            await RemoveAsync(target, current, ct);
            await SendStateAsync(target, 0, current.Index, ct);
            current.Clear();

            if (current.Set((byte)index, (byte)info.Group, (byte)info.Save, (byte)info.Type, (byte)info.Flag, 0, (uint)count, v1, v2, v3, v4, now))
            {
                await SendStateAsync(target, 1, current.Index, ct);
                await InsertAsync(target, current, ct);
                return true;
            }
        }

        return false;
    }

    /// <summary>Port of DelEffect: ends the effect with this index.</summary>
    public async Task<bool> DelAsync(IEffectCarrier target, int index, CancellationToken ct)
    {
        var effect = Get(target, index);

        if (effect == null)
        {
            return false;
        }

        await EndAsync(target, effect, ct);
        return true;
    }

    /// <summary>Port of DelEffectByGroup.</summary>
    public async Task<bool> DelByGroupAsync(IEffectCarrier target, int group, CancellationToken ct)
    {
        var effect = GetByGroup(target, group);

        if (effect == null)
        {
            return false;
        }

        await EndAsync(target, effect, ct);
        return true;
    }

    private async Task EndAsync(IEffectCarrier target, ActiveEffect effect, CancellationToken ct)
    {
        byte index = effect.Index;
        await RemoveAsync(target, effect, ct);
        await SendStateAsync(target, 0, index, ct);
        effect.Clear();
    }

    /// <summary>Port of ClearAllEffect: the effects that die with the player (type 0): dying ends them.</summary>
    public async Task ClearAllAsync(IEffectCarrier target, CancellationToken ct)
    {
        foreach (var effect in target.ActiveEffects)
        {
            if (effect.IsEffect && effect.Type == 0)
            {
                await EndAsync(target, effect, ct);
            }
        }
    }

    /// <summary>Port of ClearDebuffEffect: ends up to <paramref name="count"/> of the debuffs.</summary>
    public async Task ClearDebuffsAsync(IEffectCarrier target, int count, CancellationToken ct)
    {
        foreach (var effect in target.ActiveEffects)
        {
            if (effect.IsEffect && effect.Type == 0 && effect.Flag != 0 && --count >= 0)
            {
                await EndAsync(target, effect, ct);
            }
        }
    }

    // ---------------------------------------------------------------- the clock

    /// <summary>Port of the loop of CEffectManager::MainProc (EffectManager.cpp:121), once a second for each player: an effect
    /// with time left loses a second and, at the last, goes. One with no count never ends by itself.</summary>
    public async Task TickAsync(IEffectCarrier carrier, CancellationToken ct)
    {
        foreach (var effect in carrier.ActiveEffects)
        {
            if (!effect.IsEffect || effect.Count == 0)
            {
                continue;
            }

            effect.Count--;

            // PeriodicEffect: the effects that act every few seconds (Poison) do it with the count already lowered.
            if (OnPeriodic != null)
            {
                await OnPeriodic(carrier, effect, ct);
            }

            if (effect.IsEffect && effect.Count == 0)
            {
                await EndAsync(carrier, effect, ct);
            }
        }
    }

    /// <summary>What an effect does every second it runs (CEffectManager::PeriodicEffect): the poison takes its toll. Called with
    /// the count already lowered; the handler decides whether this second is one of the effect's own.</summary>
    public Func<IEffectCarrier, ActiveEffect, CancellationToken, Task>? OnPeriodic { get; set; }

    /// <summary>Port of CheckImmobilizeEffect: the Ice Arrow roots whoever it hits to the ground.</summary>
    public bool CheckImmobilize(IEffectCarrier carrier) => Check(carrier, EffectIds.IceArrow);

    /// <summary>The immunities (Attack.cpp:196-234): a player with the physical immunity takes no plain blow and with the magic one
    /// no skill; a monster has its own pair. The attack that meets one is a miss.</summary>
    public bool IsImmune(IEffectCarrier carrier, bool bySkill)
    {
        if (carrier is Monster monster && monster.Immune)
        {
            return true; // the Blood Castle gate and statues until their stage, and the Kalima gate
        }

        int effect = carrier is Monster
            ? (bySkill ? EffectIds.MonsterMagicDamageImmunity : EffectIds.MonsterPhysiDamageImmunity)
            : (bySkill ? EffectIds.MagicDamageImmunity : EffectIds.PhysiDamageImmunity);

        return Check(carrier, effect);
    }

    // ---------------------------------------------------------------- what an effect does

    private Task InsertAsync(IEffectCarrier target, ActiveEffect effect, CancellationToken ct) => ApplyAsync(target, effect, +1, ct);

    private Task RemoveAsync(IEffectCarrier target, ActiveEffect effect, CancellationToken ct) => ApplyAsync(target, effect, -1, ct);

    /// <summary>Port of InsertEffect and RemoveEffect (EffectManager.cpp:266-340), which are the same with the sign changed.</summary>
    private async Task ApplyAsync(IEffectCarrier target, ActiveEffect effect, int sign, CancellationToken ct)
    {
        var bonus = target.EffectBonus;
        int value = effect.Value[0] * sign;

        switch (effect.Index)
        {
            case EffectIds.GreaterDamage:
                bonus.AddPhysiDamage += value;
                bonus.AddMagicDamage += value;
                bonus.AddCurseDamage += value;
                break;

            case EffectIds.GreaterDefense:
                bonus.AddDefense += value;
                break;

            case EffectIds.ManaShield:
                bonus.AddDamageReduction += value;
                break;

            case EffectIds.GreaterCriticalDamage:
                bonus.AddCriticalDamage += value;
                break;

            case EffectIds.GreaterLife:
                bonus.MulMaxHP += value;
                break;

            case EffectIds.Ice:
                bonus.DelayLevel = sign > 0 ? 1 : 0;
                bonus.DelayActionTime = sign > 0 ? 800 : 0;
                break;

            case EffectIds.FireSlash:
                bonus.DivDefense += value;
                break;
        }

        // CharacterCalcAttribute: a player's numbers are worked out again; a monster has nothing to recalculate.
        if (target is PlayerObject player && OnStatsChanged != null)
        {
            await OnStatsChanged(player, ct);
        }
    }

    // ---------------------------------------------------------------- telling the client

    /// <summary>The bit the client shows for an effect (GetEffectState), 0 for those it shows none for.</summary>
    public static ushort StateBit(int effect) => effect switch
    {
        EffectIds.Poison => 1 << 0,
        EffectIds.Ice => 1 << 1,
        EffectIds.GreaterDamage => 1 << 2,
        EffectIds.GreaterDefense => 1 << 3,
        EffectIds.GreaterLife => 1 << 4,
        EffectIds.IceArrow => 1 << 5,
        EffectIds.FireSlash => 1 << 6,
        EffectIds.ManaShield => 1 << 8,
        _ => 0,
    };

    /// <summary>Port of GenerateEffectList: every state bit of the player's effects together, which travels in the viewport
    /// packets (the "effect list" count) so others see the auras.</summary>
    public static ushort StateBits(IEffectCarrier player)
    {
        ushort bits = 0;

        foreach (var effect in player.ActiveEffects)
        {
            if (effect.IsEffect)
            {
                bits |= StateBit(effect.Index);
            }
        }

        return bits;
    }

    /// <summary>Port of GCEffectStateSend (EffectManager.cpp:628): the player and, outside the Chaos Castle, everybody who sees
    /// him are told an effect came (<paramref name="state"/> 1) or went (0).</summary>
    private async Task SendStateAsync(IEffectCarrier target, byte state, byte effect, CancellationToken ct)
    {
        ushort bit = StateBit(effect);

        if (bit == 0)
        {
            return;
        }

        var packet = EffectPacketBuilder.StateSend(state, bit, target.Index);

        if (target is PlayerObject self)
        {
            await self.Session.SendAsync(packet, ct);
        }

        if (target.Map is >= 18 and <= 23)
        {
            return;
        }

        // MsgSendV2: everybody who sees the carrier (the players for a player, the players that have the monster in view).
        if (target is Monster monster)
        {
            foreach (int viewerIndex in monster.VisibleTo.ToList())
            {
                if (_players.TryGet(viewerIndex, out var viewer) && viewer.WorldEntered)
                {
                    await viewer.Session.SendAsync(packet, ct);
                }
            }

            return;
        }

        foreach (var other in _players.All)
        {
            if (other.Index != target.Index && other.WorldEntered && other.VisibleTo.Contains(target.Index))
            {
                await other.Session.SendAsync(packet, ct);
            }
        }
    }

    // ---------------------------------------------------------------- the database

    /// <summary>Port of EffectByteConvert (EffectManager.cpp:431): the effects that are kept, as the sixteen 13-byte records of the
    /// character's row; a slot that is empty or not kept is filled with 0xFF.</summary>
    public static void Encode(PlayerObject player, byte[] blob)
    {
        Array.Fill(blob, (byte)0xFF);

        for (int n = 0; n < ActiveEffect.Slots && (n + 1) * 13 <= blob.Length; n++)
        {
            var effect = player.ActiveEffects[n];

            if (!effect.IsEffect || effect.Save == 0)
            {
                continue;
            }

            var record = blob.AsSpan(n * 13, 13);
            record[0] = effect.Index;
            uint count = effect.Type == 2 ? effect.Time : effect.Count;
            record[1] = (byte)(count >> 24);
            record[2] = (byte)(count >> 16);
            record[3] = (byte)(count >> 8);
            record[4] = (byte)count;

            for (int v = 0; v < 4; v++)
            {
                record[5 + (v * 2)] = (byte)(effect.Value[v] >> 8);
                record[6 + (v * 2)] = (byte)effect.Value[v];
            }
        }
    }

    /// <summary>Port of the loop in CharacterInfoSet (ObjectManager.cpp:2709): the effects the database kept are put back, each
    /// with the count and the powers it had.</summary>
    public async Task RestoreAsync(PlayerObject player, byte[] blob, CancellationToken ct)
    {
        foreach (var effect in player.ActiveEffects)
        {
            effect.Clear();
        }

        for (int n = 0; n < ActiveEffect.Slots && (n + 1) * 13 <= blob.Length; n++)
        {
            var record = blob.AsSpan(n * 13, 13).ToArray();

            if (record[0] == 0xFF)
            {
                continue;
            }

            int count = (int)(((uint)record[1] << 24) | ((uint)record[2] << 16) | ((uint)record[3] << 8) | record[4]);
            ushort Word(int offset) => (ushort)((record[offset] << 8) | record[offset + 1]);

            await AddAsync(player, true, record[0], count, Word(5), Word(7), Word(9), Word(11), ct);
        }
    }
}
