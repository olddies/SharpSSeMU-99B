using MuServer.GameServer.Net;
using MuServer.GameServer.World;
using MuServer.Shared.Logging;

namespace MuServer.GameServer.Protocol;

/// <summary>The Fairy Elf's Summon skills (30-36): the cast, and the glue between the summons and what the rest of the server does.</summary>
public sealed partial class ClientProtocolHandler
{
    private SummonManager? _summons;

    /// <summary>The monsters the Elves have called (see <see cref="SummonManager"/>).</summary>
    public SummonManager Summons => _summons ??= CreateSummons(new MonsterInfoTable());

    /// <summary>Tells the summons which monsters exist (MonsterList.txt): what they are made of when a skill calls one.</summary>
    public void UseMonsterInfo(MonsterInfoTable infos) => _summons = CreateSummons(infos);

    private SummonManager CreateSummons(MonsterInfoTable infos) => new(_players, _monsters, infos, _maps, Rng)
    {
        // What a summon kills is its master's: the experience, the loot and the reward.
        OnMonsterKilled = (master, monster, damage, ct) => OnMonsterDeathAsync(master, monster, damage, 0, ct),
    };

    /// <summary>The cast of a Summon skill (the C3:19 of CGSkillAttackRecv for skills 30-36, SkillManager.cpp:1343): a master with a summon
    /// sends it away at no cost; otherwise the monster of the skill appears beside him -- not in Icarus nor in the Chaos Castle -- and the
    /// cast is paid for. No skill echo is sent: the summon itself is the news.</summary>
    private async Task CastSummonSkillAsync(ClientSession session, PlayerObject caster, SkillAttackRecv recv, CancellationToken ct)
    {
        if (caster.IsDying || caster.Life == 0)
        {
            return;
        }

        var skill = _skills.Get(recv.Skill);

        if (skill == null || !skill.CanUse(caster.Class, caster.ChangeUp) || caster.Level < skill.RequireLevel)
        {
            return;
        }

        var map = _maps.GetMap(caster.Map);

        if ((map?.CheckAttr(caster.X, caster.Y, 1) ?? false) && !SkillUseAreas.CheckUseSkill(caster.Map, caster.X, caster.Y, skill.Index))
        {
            return;
        }

        var now = DateTime.UtcNow;

        if (caster.SkillDelay.TryGetValue(skill.Index, out var lastCast) && (now - lastCast).TotalMilliseconds < skill.Delay)
        {
            return;
        }

        if ((int)caster.Mana < skill.Mana || (int)caster.BP < skill.BP)
        {
            return;
        }

        if (caster.Map == 10 || caster.Map is >= 18 and <= 23)
        {
            return; // no summons in Icarus nor in the Chaos Castle
        }

        // Calling over a summon sends it away and costs nothing; a new one is paid for.
        if (!await Summons.CastAsync(caster, skill.Index, ct))
        {
            return;
        }

        caster.SkillDelay[skill.Index] = now;
        caster.Mana = (uint)Math.Max((int)caster.Mana - skill.Mana, 0);
        caster.BP = (uint)Math.Max((int)caster.BP - skill.BP, 0);
        await session.SendAsync(ManaPacketBuilder.ManaSend(0xFF, (int)caster.Mana, (int)caster.BP), ct);
    }
}
