using MuServer.GameServer.Net;
using MuServer.GameServer.World;

namespace MuServer.GameServer.Protocol;

// The Dark Raven of a Dark Lord (CDarkSpirit): the commands the client gives it, the places where the game tells it
// something happened, and the blows it lands on monsters and players.
public sealed partial class ClientProtocolHandler
{
    /// <summary>The Dark Ravens of the players on this server.</summary>
    public DarkSpiritManager Spirits { get; private set; } = null!;

    private void CreateSpirits()
    {
        Spirits = new DarkSpiritManager(_players, _monsters, _itemBalance, Rng)
        {
            IsSafeZone = (map, x, y) => _maps.GetMap(map)?.CheckAttr(x, y, 1) ?? false,
            CanAttackPlayer = CanAttackPlayer,
            InDuel = InDuelWith,
            OnMonsterKilled = (master, monster, damage, ct) => OnMonsterDeathAsync(master, monster, damage, 0, ct),
            OnPlayerHit = CheckSelfDefenseAsync,
            OnTargetDamaged = ApplyHelperWearAsync,
            OnPlayerKilled = (master, target, ct) => KillPlayerAsync(target, master.Index, 0, ct),
        };
    }

    /// <summary>Every tick for every player in the world: the raven of a Dark Lord decides what to do (CDarkSpirit::MainProc).
    /// The blows queued 600 ms ago land once, for all of them, from the ticker.</summary>
    public Task OnPlayerTickAsync(PlayerObject player, CancellationToken ct) => Spirits.TickAsync(player, ct);

    public Task LandSpiritBlowsAsync(CancellationToken ct) => Spirits.LandDueBlowsAsync(ct);

    /// <summary>The pet a player wears on the helper slot wears down with the blow they took (CAttack::DamageSprite); when
    /// one point goes the stats are worked out again, and when the pet is gone the new look is sent to everybody.</summary>
    public async Task ApplyHelperWearAsync(PlayerObject target, int damage, CancellationToken ct)
    {
        var outcome = await _pets.WearHelperAsync(target, damage, ct);

        if (outcome == PetManager.HelperWearOutcome.None)
        {
            return;
        }

        if (outcome.HasFlag(PetManager.HelperWearOutcome.Vanished))
        {
            target.RebuildCharSet();
        }

        target.RecalcCombatStats(_itemBalance, _characterBalance);

        if (!outcome.HasFlag(PetManager.HelperWearOutcome.Vanished))
        {
            return;
        }

        await target.Session.SendAsync(ItemPacketBuilder.ItemEquipmentSend(target), ct);
        var refresh = WorldPacketBuilder.ViewportPlayerAppear(new[] { target });

        foreach (var other in _players.All)
        {
            if (other.Index != target.Index && other.VisibleTo.Contains(target.Index))
            {
                await other.Session.SendAsync(refresh, ct);
            }
        }
    }

    /// <summary>Port of CGPetItemCommandRecv (Protocol.cpp:780), C1:A7: the order the player gives the raven.</summary>
    private async Task OnPetCommandAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered)
        {
            return;
        }

        var recv = PetCommandRecv.Parse(p);

        if (recv.Type != 0)
        {
            return;
        }

        await Spirits.ChangeCommandAsync(player, recv.Command, recv.Index, ct);
    }

    /// <summary>The raven's numbers for the character window (GetPreviewAttackDamage, DarkSpirit.cpp:984): its range with a
    /// scepter's rise, its speed and its success rate; zeros without a working raven.</summary>
    private DarkSpiritStats SpiritPreview(PlayerObject player)
    {
        var stats = Spirits.StatsFor(player);

        if (stats == null)
        {
            return default;
        }

        var (min, max) = DarkSpiritMath.ScepterRise(player, _itemBalance, stats.Value.DamageMin, stats.Value.DamageMax);
        return stats.Value with { DamageMin = min, DamageMax = max };
    }
}
