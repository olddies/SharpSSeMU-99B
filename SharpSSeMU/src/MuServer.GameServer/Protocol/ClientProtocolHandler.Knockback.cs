using MuServer.GameServer.World;
using MuServer.Shared.Logging;

namespace MuServer.GameServer.Protocol;

/// <summary>The blows that push: Lightning and the Dark Knight's basic skills move the target a tile 150 ms after the hit, the Dark Lord's
/// Earthquake throws it three (ApplySkillEffect, Attack.cpp:1076-1096; gObjBackSpring and gObjBackSpring2, User.cpp:2594-2736).</summary>
public sealed partial class ClientProtocolHandler
{
    /// <summary>How long after the hit the push comes (gObjAddMsgSendDelay(..., 150)).</summary>
    private const int PushDelayMs = 150;

    /// <summary>The creatures a push does not move: the Castle Gate, the Statue of Saint, the Archangel's statue and Kundun.</summary>
    private static bool Unmovable(Monster monster) => monster.MonsterClass is >= 131 and <= 134 or 275;

    /// <summary>What a skill that hits does to the target's position, once it is known the skill took effect: Lightning and the Dark Knight's
    /// skills push it one tile in 150 ms; the Earthquake throws it three tiles at once.</summary>
    private void StartKnockback(int attackerIndex, IEffectCarrier target, SkillInfo skill, CancellationToken ct)
    {
        if (Knockback.PushesOnHit(skill.Index))
        {
            int dir = _players.TryGet(attackerIndex, out var player) ? player.Dir : _monsters.TryGet(attackerIndex, out var monster) ? monster.Dir : 0;
            _ = PushAfterDelayAsync(target, dir, ct);
        }
        else if (Knockback.ThrowsOnHit(skill.Index) && _players.TryGet(attackerIndex, out var thrower))
        {
            _ = ThrowAsync(target, thrower.X, thrower.Y, ct);
        }
    }

    private async Task PushAfterDelayAsync(IEffectCarrier target, int attackerDir, CancellationToken ct)
    {
        try
        {
            await Task.Delay(PushDelayMs, ct);

            if (!CanBeMoved(target))
            {
                return;
            }

            var map = _maps.GetMap(target.Map);
            var spot = Knockback.Push(map, PositionOf(target).X, PositionOf(target).Y, attackerDir, n => Rng.Next(n));

            if (spot != null)
            {
                await MoveByForceAsync(target, (byte)spot.Value.X, (byte)spot.Value.Y, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Add(LogColor.Red, "[Knockback] The push failed: {0}", ex.Message);
        }
    }

    private async Task ThrowAsync(IEffectCarrier target, byte fromX, byte fromY, CancellationToken ct)
    {
        try
        {
            if (!CanBeMoved(target))
            {
                return;
            }

            var (x, y) = PositionOf(target);
            bool strong = target is PlayerObject { Class: 1 or 3 or 4 }; // the Dark Knight, the Magic Gladiator and the Dark Lord keep going
            var (nx, ny) = Knockback.Throw(_maps.GetMap(target.Map), x, y, fromX, fromY, 3, strong);

            if (nx != x || ny != y)
            {
                await MoveByForceAsync(target, (byte)nx, (byte)ny, ct);
            }
        }
        catch (Exception ex)
        {
            Log.Add(LogColor.Red, "[Knockback] The throw failed: {0}", ex.Message);
        }
    }

    private static (int X, int Y) PositionOf(IEffectCarrier carrier) => carrier switch
    {
        PlayerObject player => (player.X, player.Y),
        Monster monster => (monster.X, monster.Y),
        _ => (0, 0),
    };

    private static bool CanBeMoved(IEffectCarrier carrier) => carrier switch
    {
        PlayerObject player => player.WorldEntered && !player.IsDying && player.Life > 0,
        Monster monster => monster.Live && !Unmovable(monster) && monster.ShopNumber == null,
        _ => false,
    };

    /// <summary>gObjSetPosition (User.cpp:2862): the creature is at the new tile, and it and everybody who sees it are told (PMSG_POSITION_SEND).</summary>
    private async Task MoveByForceAsync(IEffectCarrier target, byte x, byte y, CancellationToken ct)
    {
        var packet = SkillPacketBuilder.PositionSend(target.Index, x, y);

        switch (target)
        {
            case PlayerObject player:
                player.X = x;
                player.Y = y;
                player.TX = x;
                player.TY = y;
                player.OldX = x;
                player.OldY = y;
                await player.Session.SendAsync(packet, ct);

                foreach (var other in _players.All)
                {
                    if (other.Index != player.Index && other.WorldEntered && other.VisibleTo.Contains(player.Index))
                    {
                        await other.Session.SendAsync(packet, ct);
                    }
                }

                break;

            case Monster monster:
                monster.X = x;
                monster.Y = y;
                monster.TX = x;
                monster.TY = y;

                foreach (int viewerIndex in monster.VisibleTo.ToList())
                {
                    if (_players.TryGet(viewerIndex, out var viewer) && viewer.WorldEntered)
                    {
                        await viewer.Session.SendAsync(packet, ct);
                    }
                }

                break;
        }
    }
}
