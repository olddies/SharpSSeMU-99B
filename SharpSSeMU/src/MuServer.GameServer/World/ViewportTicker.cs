using MuServer.GameServer.Config;
using MuServer.GameServer.Protocol;
using MuServer.Shared.Localization;
using MuServer.Shared.Logging;

namespace MuServer.GameServer.World;

/// <summary> Simplified port of the periodic cycle gObjViewportProc()/CViewport (User.cpp/Viewport.cpp):
/// instead of replicating the internal VpPlayer[]/VpPlayer2[] mechanism per object, a full O(n²) sweep is done
/// every tick over all online players (acceptable for this phase: without monsters/NPCs, the number of objects
/// is that of connected players) and mutual visibility is decided directly by map+range. The result on the wire
/// is the same: appear (0x12) and disappear (0x14) packets exactly when appropriate. A player takes part in
/// this sweep from entering the world, because RegenOk starts as false (=0 in the original, see
/// PlayerObject.RegenOk) -- PMSG_CHARACTER_MOVE_VIEWPORT_ENABLE (0xF3:0x12) is only relevant after a real
/// teleport/gate (which sets RegenOk to true/1), a mechanism this build does not have plugged in yet (see the
/// doc-comment of PlayerObject.RegenOk). </summary>
public sealed class ViewportTicker
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);

    private readonly PlayerRegistry _players;
    private readonly MapRegistry _maps;
    private readonly MonsterRegistry? _monsters;
    private readonly PartyRegistry? _parties;
    private readonly ClientProtocolHandler? _protocolHandler;
    private readonly CharacterBalanceConfig? _characterBalance;
    private readonly GroundItemRegistry? _groundItems;
    private readonly GateTable? _gates;
    private readonly SkillInfoTable? _skills;
    private readonly SkillDamageTable? _skillDamage;
    private int _tickCount;

    /// <summary>Every how many ticks (200ms each) PMSG_PARTY_LIFE_SEND is sent -- ~2s, within the reasonable
    /// range for a periodic broadcast of party life bars (the original does not fix an exact period documented
    /// in the research brief, it only confirms that it is periodic).</summary>
    private const int PartyLifeTickInterval = 10;

    /// <summary>Port of CharacterAutoRecuperation (ObjectManager.cpp:1729-1758): the original runs every 1s but
    /// only regenerates mana every 3rd tick (~3s) -- here the base tick is 200ms, so 15 ticks ≈ 3s.</summary>
    private const int ManaRegenTickInterval = 15;

    public ViewportTicker(
        PlayerRegistry players, MapRegistry maps, MonsterRegistry? monsters = null,
        PartyRegistry? parties = null, ClientProtocolHandler? protocolHandler = null,
        CharacterBalanceConfig? characterBalance = null, GroundItemRegistry? groundItems = null,
        GateTable? gates = null, SkillInfoTable? skills = null, SkillDamageTable? skillDamage = null)
    {
        _players = players;
        _maps = maps;
        _monsters = monsters;
        _parties = parties;
        _protocolHandler = protocolHandler;
        _characterBalance = characterBalance;
        _groundItems = groundItems;
        _gates = gates;
        _skills = skills;
        _skillDamage = skillDamage;
    }

    public void Start(CancellationToken ct) => _ = RunAsync(ct);

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await TickAsync(ct);
            }
            catch (Exception ex)
            {
                Log.Add(LogColor.Red, "[Viewport] Error in tick: {0}", ex.Message);
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await RespawnDeadMonsters(ct);
        await SweepExpiredGroundItemsAsync(ct);

        _tickCount++;

        if (_tickCount % PartyLifeTickInterval == 0)
        {
            await TickPartyLifeAsync(ct);
        }

        if (_tickCount % ManaRegenTickInterval == 0)
        {
            await TickManaRegenAsync(ct);
        }

        await TickAutoSaveAsync(ct);
        await TickMonsterAIAsync(ct);
        await RespawnDyingPlayersAsync(ct);

        var all = _players.All.Where(p => p.WorldEntered).ToList();

        foreach (var observer in all)
        {
            if (observer.RegenOk)
            {
                continue; // has not sent 0xF3:0x12 yet -- it does not process its own visible list
            }

            int view = _maps.GetViewRange(observer.Map);
            var currentlyVisible = new HashSet<int>();
            var newlyVisible = new List<PlayerObject>();

            foreach (var target in all)
            {
                if (target.Index == observer.Index)
                {
                    continue;
                }

                bool inRange = target.Map == observer.Map
                    && Math.Abs(target.X - observer.X) <= view
                    && Math.Abs(target.Y - observer.Y) <= view;

                if (!inRange)
                {
                    continue;
                }

                currentlyVisible.Add(target.Index);

                if (!observer.VisibleTo.Contains(target.Index))
                {
                    newlyVisible.Add(target);
                }
            }

            var noLongerVisible = observer.VisibleTo.Where(idx => !currentlyVisible.Contains(idx)).ToList();

            if (newlyVisible.Count > 0)
            {
                await observer.Session.SendAsync(WorldPacketBuilder.ViewportPlayerAppear(newlyVisible), ct);
            }

            if (noLongerVisible.Count > 0)
            {
                await observer.Session.SendAsync(WorldPacketBuilder.ViewportDestroy(noLongerVisible), ct);
            }

            observer.VisibleTo.Clear();

            foreach (var idx in currentlyVisible)
            {
                observer.VisibleTo.Add(idx);
            }

            await TickMonstersForObserverAsync(observer, view, ct);
            await TickGroundItemsForObserverAsync(observer, view, ct);
        }
    }

    /// <summary>Port of the "items" portion of CMap::StateSetDestroy (Map.cpp:433-474): frees any ground item
    /// whose lifetime expired and notifies whoever had it in view (0xC2:0x21, <see
    /// cref="WorldPacketBuilder.ViewportItemDestroy"/>). It does not distinguish by map when sending the notice
    /// -- each observer only has in <see cref="PlayerObject.VisibleGroundItems"/> the indices of THEIR own map,
    /// so an index that does not belong to them is simply not in their set and generates no noise.</summary>
    private async Task SweepExpiredGroundItemsAsync(CancellationToken ct)
    {
        if (_groundItems == null)
        {
            return;
        }

        var expired = _groundItems.SweepExpired().ToList();

        if (expired.Count == 0)
        {
            return;
        }

        foreach (var player in _players.All)
        {
            if (!player.WorldEntered)
            {
                continue;
            }

            var toRemove = expired.Where(g => g.Map == player.Map && player.VisibleGroundItems.Contains(g.Index)).Select(g => g.Index).ToList();

            if (toRemove.Count == 0)
            {
                continue;
            }

            foreach (var idx in toRemove)
            {
                player.VisibleGroundItems.Remove(idx);
            }

            await player.Session.SendAsync(WorldPacketBuilder.ViewportItemDestroy(toRemove), ct);
        }
    }

    /// <summary>Simplified port of CViewport::CreateViewportItem/DestroyViewportItem
    /// (Viewport.cpp:376-417,499-525) -- the same O(n)-per-observer sweep pattern as <see
    /// cref="TickMonstersForObserverAsync"/>, applied to <see cref="GroundItemRegistry"/> instead of the
    /// monster registry.</summary>
    private async Task TickGroundItemsForObserverAsync(PlayerObject observer, int view, CancellationToken ct)
    {
        if (_groundItems == null)
        {
            return;
        }

        var currentlyVisible = new HashSet<int>();
        var newlyVisible = new List<GroundItem>();

        foreach (var ground in _groundItems.AllOnMap(observer.Map))
        {
            bool inRange = Math.Abs(ground.X - observer.X) <= view && Math.Abs(ground.Y - observer.Y) <= view;

            if (!inRange)
            {
                continue;
            }

            currentlyVisible.Add(ground.Index);

            if (!observer.VisibleGroundItems.Contains(ground.Index))
            {
                newlyVisible.Add(ground);
            }
        }

        var noLongerVisible = observer.VisibleGroundItems.Where(idx => !currentlyVisible.Contains(idx)).ToList();

        if (newlyVisible.Count > 0)
        {
            await observer.Session.SendAsync(WorldPacketBuilder.ViewportItemAppear(newlyVisible), ct);

            foreach (var g in newlyVisible)
            {
                g.JustDropped = false; // the "just fallen" bit is only sent the first time
            }
        }

        if (noLongerVisible.Count > 0)
        {
            await observer.Session.SendAsync(WorldPacketBuilder.ViewportItemDestroy(noLongerVisible), ct);
        }

        observer.VisibleGroundItems.Clear();

        foreach (var idx in currentlyVisible)
        {
            observer.VisibleGroundItems.Add(idx);
        }
    }

    /// <summary> Port of the respawn part of ObjectSetStateCreate/ObjectSetStateProc
    /// (ObjectManager.cpp:94-99,197-246): once MaxRegenMillis+1000ms have passed since death, the monster
    /// revives at its original spawn point (gObjMonsterRegen). Before reviving, gObjMonsterRegen
    /// (Monster.cpp:369) calls gObjClearViewport -- only AT THAT MOMENT is the corpse removed from the view of
    /// whoever saw it, not on dying (see the comment of ClientProtocolHandler. OnMonsterDeathAsync). The same
    /// order is ported here: first ViewportDestroy to those who still had it visible, then the respawn. The
    /// appearance packet (0x13) is naturally sent by the normal sweep below, because it will no longer be in
    /// anyone's VisibleMonsters. </summary>
    private async Task RespawnDeadMonsters(CancellationToken ct)
    {
        if (_monsters == null)
        {
            return;
        }

        var now = DateTime.UtcNow;

        foreach (var monster in _monsters.All)
        {
            if (monster.IsDead && monster.DiedAt != null
                && (now - monster.DiedAt.Value).TotalMilliseconds > monster.MaxRegenMillis + 1000)
            {
                foreach (var viewerIndex in monster.VisibleTo)
                {
                    if (_players.TryGet(viewerIndex, out var viewer))
                    {
                        viewer.VisibleMonsters.Remove(monster.Index);
                        await viewer.Session.SendAsync(WorldPacketBuilder.ViewportDestroy(new[] { monster.Index }), ct);
                    }
                }

                monster.VisibleTo.Clear();

                _monsters.Respawn(monster, _maps);
            }
        }
    }

    /// <summary> Simplified port of the same mechanism as above but for monsters (Viewport.cpp: the original
    /// function walks ALL objects, players and monsters alike; here it is split into two passes because they
    /// are two different C# collections/types). It updates <see cref="PlayerObject.VisibleMonsters"/> and also
    /// <see cref="Monster.VisibleTo"/> (used by ClientProtocolHandler to direct damage/death packets only to
    /// whoever really sees the monster, equivalent to the original's MsgSendV2/VpPlayer2[]). </summary>
    private async Task TickMonstersForObserverAsync(PlayerObject observer, int view, CancellationToken ct)
    {
        if (_monsters == null)
        {
            return;
        }

        var currentlyVisible = new HashSet<int>();
        var newlyVisible = new List<Monster>();

        foreach (var monster in _monsters.All)
        {
            if (monster.Map != observer.Map)
            {
                continue;
            }

            bool inRange = Math.Abs(monster.X - observer.X) <= view && Math.Abs(monster.Y - observer.Y) <= view;

            if (!inRange)
            {
                monster.VisibleTo.Remove(observer.Index);
                continue;
            }

            currentlyVisible.Add(monster.Index);
            monster.VisibleTo.Add(observer.Index);

            // A corpse (IsDead but not respawned yet, see RespawnDeadMonsters) stays in currentlyVisible so
            // that this pass does not treat it as "just became invisible" -- but it is never announced as just
            // appeared, because its death was already announced with the 0x17 packet.
            if (!observer.VisibleMonsters.Contains(monster.Index) && !monster.IsDead)
            {
                newlyVisible.Add(monster);
            }
        }

        var noLongerVisible = observer.VisibleMonsters.Where(idx => !currentlyVisible.Contains(idx)).ToList();

        if (newlyVisible.Count > 0)
        {
            await observer.Session.SendAsync(WorldPacketBuilder.ViewportMonsterAppear(newlyVisible), ct);
        }

        if (noLongerVisible.Count > 0)
        {
            await observer.Session.SendAsync(WorldPacketBuilder.ViewportDestroy(noLongerVisible), ct);
        }

        observer.VisibleMonsters.Clear();

        foreach (var idx in currentlyVisible)
        {
            observer.VisibleMonsters.Add(idx);
        }
    }

    /// <summary> Simplified port of CObjectManager::CharacterAutoRecuperation (ObjectManager.cpp:1729-1758),
    /// mana branch: <c>Mana += (MaxMana*MPRecoveryRate[class])/100</c>, capped at MaxMana, every <see
    /// cref="ManaRegenTickInterval"/> ticks (~3s, see the constant's comment). Documented simplifications:
    /// without the "+3pp if it has not attacked in the last 5s" bonus (MPAutoRecuperationTime is not tracked),
    /// without item/active effect bonuses (MPRecoveryRate/EffectOption.AddMPRecoveryRate, not ported), and
    /// WITHOUT life regeneration (HPRecoveryRate is 0 for the 5 classes in the real shipped .dat, so the
    /// original does not regenerate life by this mechanism either -- omitting it does not change the default
    /// behaviour). BP is regenerated with the same formula/constants (BPRecoveryRate). </summary>
    private async Task TickManaRegenAsync(CancellationToken ct)
    {
        if (_characterBalance == null)
        {
            return;
        }

        foreach (var player in _players.All)
        {
            if (!player.WorldEntered)
            {
                continue;
            }

            int cls = Math.Clamp((int)player.Class, 0, 4);
            bool changed = false;

            if (player.Mana < player.MaxMana)
            {
                int rate = _characterBalance.MpRecoveryRate[cls];
                uint gain = (uint)Math.Max(((int)player.MaxMana * rate) / 100, 0);
                player.Mana = Math.Min(player.Mana + gain, player.MaxMana);
                changed = true;
            }

            if (player.BP < player.MaxBP)
            {
                int rate = _characterBalance.BpRecoveryRate[cls];
                uint gain = (uint)Math.Max(((int)player.MaxBP * rate) / 100, 0);
                player.BP = Math.Min(player.BP + gain, player.MaxBP);
                changed = true;
            }

            if (changed)
            {
                await player.Session.SendAsync(ManaPacketBuilder.ManaSend(0xFF, (int)player.Mana, (int)player.BP), ct);
            }
        }
    }

    /// <summary> Port of User.cpp:2535-2541 (branch <c>GetTickCount()-lpObj->AutoSaveTime &gt; 600000</c>):
    /// unconditional autosave every 10 minutes for EVERY connected player, whether or not they fought -- this
    /// is the real mechanism that guarantees position/progress is persisted in any session (walking without
    /// fighting, staying in town, etc.), unlike the 60s-throttled save of <see
    /// cref="ClientProtocolHandler.ApplyExperienceGainAsync"/>, which only triggers on level-ups. See
    /// PlayerObject.AutoSaveTime (the same sentinel DateTime.MinValue = "never saved" that makes the first
    /// check after entering the world able to trigger a save, like the original with AutoSaveTime at 0).
    /// </summary>
    private async Task TickAutoSaveAsync(CancellationToken ct)
    {
        if (_protocolHandler == null)
        {
            return;
        }

        var now = DateTime.UtcNow;

        foreach (var player in _players.All)
        {
            if (!player.WorldEntered)
            {
                continue;
            }

            if ((now - player.AutoSaveTime).TotalMilliseconds > 600000)
            {
                player.AutoSaveTime = now;
                await _protocolHandler.SaveCharacterAsync(player, ct);
            }
        }
    }

    /// <summary>Port of the periodic part of GCPartyLifeSend (called from User.cpp:3488) -- sends the life/mana
    /// bars of each party with more than one member every <see cref="PartyLifeTickInterval"/> ticks.</summary>
    private async Task TickPartyLifeAsync(CancellationToken ct)
    {
        if (_parties == null || _protocolHandler == null)
        {
            return;
        }

        foreach (var group in _parties.All)
        {
            if (group.MemberIndices.Count > 1)
            {
                await _protocolHandler.BroadcastPartyLifeAsync(group, ct);
            }
        }
    }

    private async Task TickMonsterAIAsync(CancellationToken ct)
    {
        if (_monsters == null || _protocolHandler == null) return;

        var allMonsters = _monsters.All.Where(m => m.Live && m.ShopNumber == null).ToList();
        var allPlayers = _players.All.Where(p => p.WorldEntered && p.Life > 0).ToList();

        if (allMonsters.Count == 0 || allPlayers.Count == 0) return;

        var now = DateTime.UtcNow;

        foreach (var monster in allMonsters)
        {
            var map = _maps.GetMap(monster.Map);
            if (map != null && map.IsSafeZone(monster.X, monster.Y))
            {
                // Monstruos en zona segura no atacan ni persiguen
                monster.TargetIndex = -1;
                continue;
            }

            // 1. Check whether the existing target is still valid
            PlayerObject? target = null;
            if (monster.TargetIndex != -1)
            {
                if (!_players.TryGet(monster.TargetIndex, out target)
                    || !target.WorldEntered
                    || target.Life == 0
                    || target.Map != monster.Map
                    || !monster.VisibleTo.Contains(target.Index) // Prevent invisible damage outside the Viewport
                    || (map != null && map.IsSafeZone(target.X, target.Y)))
                {
                    monster.TargetIndex = -1;
                    target = null;
                }
                else
                {
                    // Check whether the target moved more than 10 tiles away
                    double distToTarget = Math.Sqrt(Math.Pow(monster.X - target.X, 2) + Math.Pow(monster.Y - target.Y, 2));
                    if (distToTarget > 10.0)
                    {
                        monster.TargetIndex = -1;
                        target = null;
                    }
                }
            }

            // 2. If it has no target, look for the CLOSEST player in its Viewport
            if (monster.TargetIndex == -1)
            {
                // A monster can ONLY see players who are seeing it in their viewport (VisibleTo)
                PlayerObject? closest = null;
                double closestDist = double.MaxValue;

                foreach (var obsIndex in monster.VisibleTo)
                {
                    if (_players.TryGet(obsIndex, out var p) && p.WorldEntered && p.Life > 0 && p.Map == monster.Map)
                    {
                        if (map != null && map.IsSafeZone(p.X, p.Y)) continue; // Players in town are protected

                        double dist = Math.Sqrt(Math.Pow(monster.X - p.X, 2) + Math.Pow(monster.Y - p.Y, 2));
                        int maxDetectRange = monster.ViewRange > 0 ? Math.Min(monster.ViewRange, 5) : 4;

                        if (dist <= maxDetectRange && dist < closestDist)
                        {
                            closestDist = dist;
                            closest = p;
                        }
                    }
                }

                if (closest != null)
                {
                    monster.TargetIndex = closest.Index;
                    target = closest;

                    // On detecting a player for the first time, give a reaction grace time (1000ms)
                    monster.LastAttackTime = now;
                    monster.LastMoveTime = now;
                    continue; // Wait for the next tick to react
                }
            }

            if (target == null)
            {
                // Autonomous movement (passive roaming of the monster in its spawn area)
                if (now >= monster.LastMoveTime.AddSeconds(3 + Random.Shared.Next(0, 3)))
                {
                    monster.LastMoveTime = now;

                    // One one-tile step in a random direction, accepted only if it does not take the monster
                    // more than Dis from WHERE IT APPEARED. It is the port of gObjMonsterMoveCheck
                    // (Monster.cpp:430-462): Euclidean distance against StartX/StartY, with the Dis of its
                    // spawn row. Before, this measured against SpawnEntry.X/Y and on top of that clipped the
                    // radius to 3. In area spawns (Type 1) SpawnEntry.X/Y is the corner of the SHARED
                    // rectangle, so the 85 monsters of the Lorencia area all ended up squeezed into a 7x7
                    // square on that corner, walking in circles and going in and out of the viewport all the
                    // time.
                    int moveRange = Math.Max(1, monster.SpawnEntry.Dis);

                    int newX = monster.X + Random.Shared.Next(-1, 2);
                    int newY = monster.Y + Random.Shared.Next(-1, 2);

                    double distFromStart = Math.Sqrt(
                        Math.Pow(newX - monster.StartX, 2) + Math.Pow(newY - monster.StartY, 2));

                    if ((newX != monster.X || newY != monster.Y) && distFromStart <= moveRange)
                    {
                        if (map == null || !map.IsBlocked(newX, newY))
                        {
                            byte oldX = monster.X;
                            byte oldY = monster.Y;
                            monster.X = (byte)newX;
                            monster.Y = (byte)newY;

                            int signX = Math.Sign(newX - oldX);
                            int signY = Math.Sign(newY - oldY);
                            monster.Dir = GetDir8(signX, signY, monster.Dir);

                            var movePacket = WorldPacketBuilder.MoveSend(monster.Index, monster.X, monster.Y, monster.Dir);
                            foreach (var obsIndex in monster.VisibleTo)
                            {
                                if (_players.TryGet(obsIndex, out var obsPlayer) && obsPlayer.WorldEntered)
                                {
                                    await obsPlayer.Session.SendAsync(movePacket, ct);
                                }
                            }
                        }
                    }
                }
                continue;
            }

            // 3. Compute the real Euclidean distance
            double eucDist = Math.Sqrt(Math.Pow(monster.X - target.X, 2) + Math.Pow(monster.Y - target.Y, 2));
            double attackRange = monster.AttackRange > 0 ? monster.AttackRange : 1.5;

            int attackCooldownMs = monster.AttackSpeed > 0 ? monster.AttackSpeed : 1500;

            if (eucDist <= attackRange)
            {
                // Rango de Ataque alcanzado: verificar Cooldown de velocidad de ataque del monstruo
                if (now < monster.LastAttackTime.AddMilliseconds(attackCooldownMs)) continue;

                monster.LastAttackTime = now;

                // Turn the monster's direction towards the player
                int signX = Math.Sign(target.X - monster.X);
                int signY = Math.Sign(target.Y - monster.Y);
                monster.Dir = GetDir8(signX, signY, monster.Dir);

                var (isSpell, skillId) = GetMonsterAttackSkill(monster.MonsterClass, monster.AttackRange, monster.AttackType, monster.MonsterSkill);

                // ---- Step 1: miss/dodge (port of CAttack::MissCheck, Attack.cpp:987-1031) ---- Same
                // calculation as OnAttackAsync (see ClientProtocolHandler.cs), but here the attacker is the
                // monster and the defender the player.
                int attackSuccess = Math.Max(monster.AttackSuccessRate, 0);
                int defenseSuccess = Math.Max(target.DefenseSuccessRate, 0);
                bool graze = false;

                if (attackSuccess < defenseSuccess)
                {
                    if (Random.Shared.Next(100) >= 5)
                    {
                        await target.Session.SendAsync(CombatPacketBuilder.DamageSend(target.Index, 0, 0, true, target.Life), ct);
                        continue;
                    }

                    graze = true;
                }
                else
                {
                    int denom = attackSuccess == 0 ? 1 : attackSuccess;

                    if (Random.Shared.Next(denom) < defenseSuccess)
                    {
                        await target.Session.SendAsync(CombatPacketBuilder.DamageSend(target.Index, 0, 0, true, target.Life), ct);
                        continue;
                    }
                }

                // ---- Step 2: target defense (CAttack::GetTargetDefense, Attack.cpp:1117-1154) ---- Defense is
                // halved when the target is an OBJECT_USER -- here the target is always a player, so the half
                // always applies (not only for spells).
                int targetDefense = Math.Max((int)target.Defense * 50 / 100, 0);

                // ---- Step 3: raw damage ---- Melee: CAttack::GetAttackDamage, OBJECT_MONSTER branch
                // (Attack.cpp:1156-1307) -- uses the monster's PhysiDamageMin/Max directly, without
                // critical/excellent (those rolls only exist in the player-attacker branch). Spell:
                // CAttack::GetAttackDamageWizard (Attack.cpp:1309-1384) -- monsters never have
                // MagicDamageMin/Max set (gObjSetMonster, Monster.cpp:206-365, does not touch it), so the magic
                // damage comes purely and exclusively from the skill itself (DamageMin/DamageMax of Skill.txt,
                // see SkillInfo.cs), like the term "lpObj->MagicDamageMin + lpSkill->m_DamageMin" with the
                // monster's part at zero.
                int damage;
                var skillInfo = isSpell ? _skills?.Get(skillId) : null;

                if (skillInfo != null)
                {
                    int spellRange = Math.Max(skillInfo.DamageMax - skillInfo.DamageMin, 1);
                    damage = skillInfo.DamageMin + Random.Shared.Next(spellRange);
                }
                else
                {
                    int attackMin = monster.DamageMin > 0 ? monster.DamageMin : 5;
                    int attackMax = monster.DamageMax >= attackMin ? monster.DamageMax : attackMin + 5;
                    int range = Math.Max(attackMax - attackMin, 1);
                    damage = attackMin + Random.Shared.Next(range);
                }

                if (graze)
                {
                    damage = (damage * 30) / 100;
                }

                damage -= targetDefense;
                damage = Math.Max(damage, 0);

                // ---- Step 3: damage floor by the ATTACKER's level (Attack.cpp:318-322) ----
                int minDamage = Math.Max(monster.Level / 10, 1);

                if (damage < minDamage)
                {
                    damage = minDamage + Random.Shared.Next(minDamage);
                }

                // ---- Step 4: optional per-skill multiplier (SkillDamage.txt -- no-op with the real data),
                // only applies on the spell side, like GetAttackDamageWizard ----
                if (skillInfo != null && _skillDamage != null)
                {
                    damage = _skillDamage.Apply(skillInfo.Index, damage);
                }

                target.Life = (uint)Math.Max(0, (long)target.Life - damage);

                await target.Session.SendAsync(CombatPacketBuilder.DamageSend(target.Index, damage, 0, false, target.Life), ct);

                if (isSpell)
                {
                    var skillPacket = CombatPacketBuilder.SkillAttackSend(monster.Index, skillId, target.Index, true);
                    foreach (var obsIndex in monster.VisibleTo)
                    {
                        if (_players.TryGet(obsIndex, out var obsPlayer) && obsPlayer.WorldEntered)
                        {
                            await obsPlayer.Session.SendEncryptedAsync(skillPacket, ct);
                        }
                    }
                }
                else
                {
                    var actionPacket = CombatPacketBuilder.ActionSend(monster.Index, monster.Dir, 0, target.Index);
                    foreach (var obsIndex in monster.VisibleTo)
                    {
                        if (_players.TryGet(obsIndex, out var obsPlayer) && obsPlayer.WorldEntered)
                        {
                            await obsPlayer.Session.SendAsync(actionPacket, ct);
                        }
                    }
                }

                if (target.Life == 0 && !target.IsDying)
                {
                    target.IsDying = true;
                    target.DiedAt = now;
                    monster.TargetIndex = -1;

                    // 1. Enviar aviso de muerte y actualizar barra de HP a 0 en la UI del cliente (0x26)
                    await target.Session.SendAsync(ChatPacketBuilder.NoticeSend(Loc.T("You died.")), ct);
                    await target.Session.SendAsync(LifePacketBuilder.LifeSend(0xFF, 0), ct);

                    // 2. Transmitir el paquete de MUERTE REAL de MU 0.99B (0x17 PMSG_USER_DIE_SEND)
                    var userDiePacket = CombatPacketBuilder.UserDieSend(target.Index, 0, monster.Index);
                    await target.Session.SendAsync(userDiePacket, ct);

                    foreach (var obsIndex in target.VisibleTo)
                    {
                        if (_players.TryGet(obsIndex, out var obsPlayer) && obsPlayer.WorldEntered)
                        {
                            await obsPlayer.Session.SendAsync(userDiePacket, ct);
                        }
                    }
                }
            }
            else if (eucDist <= 7.0)
            {
                // Chase: move the monster 1 tile towards the player
                if (now < monster.LastMoveTime.AddMilliseconds(1000)) continue;

                monster.LastMoveTime = now;

                int signX = Math.Sign(target.X - monster.X);
                int signY = Math.Sign(target.Y - monster.Y);

                byte dir = GetDir8(signX, signY, monster.Dir);
                monster.Dir = dir;

                byte stepX = (byte)(monster.X + signX);
                byte stepY = (byte)(monster.Y + signY);

                if (map != null && (map.IsSafeZone(stepX, stepY) || map.IsBlocked(stepX, stepY)))
                {
                    continue; // It does not enter the town nor cross obstacles
                }

                monster.X = stepX;
                monster.Y = stepY;

                foreach (var obsIndex in monster.VisibleTo)
                {
                    if (_players.TryGet(obsIndex, out var obsPlayer) && obsPlayer.WorldEntered)
                    {
                        await obsPlayer.Session.SendAsync(WorldPacketBuilder.ViewportMonsterMove(monster.Index, stepX, stepY, monster.Dir), ct);
                    }
                }
            }
            else
            {
                // Lost the trail (more than 7 tiles): drop the target
                monster.TargetIndex = -1;
            }
        }
    }

    private static int GetRespawnGateForMap(int mapNumber) => mapNumber switch
    {
        0 => 17, // Lorencia
        1 => 17, // Dungeon -> Lorencia
        2 => 22, // Devias
        3 => 27, // Noria
        4 => 42, // LostTower
        6 => 115, // Arena
        7 => 49, // Atlans
        8 => 57, // Tarkan
        _ => 17  // Default Lorencia
    };

    private async Task RespawnDyingPlayersAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var dyingPlayers = _players.All.Where(p => p.WorldEntered && p.IsDying).ToList();

        foreach (var player in dyingPlayers)
        {
            if (now >= player.DiedAt?.AddMilliseconds(1000))
            {
                player.IsDying = false;
                player.DiedAt = null;

                player.Life = player.MaxLife;
                player.Mana = player.MaxMana;
                player.BP = player.MaxBP;

                int gateIndex = GetRespawnGateForMap(player.Map);
                var gate = _gates?.Get(gateIndex);

                if (gate != null)
                {
                    player.Map = (byte)gate.Map;
                    int rx = (gate.EndX > gate.StartX) ? Random.Shared.Next(gate.StartX, gate.EndX + 1) : gate.StartX;
                    int ry = (gate.EndY > gate.StartY) ? Random.Shared.Next(gate.StartY, gate.EndY + 1) : gate.StartY;
                    player.X = (byte)rx;
                    player.Y = (byte)ry;
                    player.TX = (byte)rx;
                    player.TY = (byte)ry;
                    player.OldX = (byte)rx;
                    player.OldY = (byte)ry;
                }
                else
                {
                    player.Map = 0;
                    player.X = 122;
                    player.Y = 125;
                    player.TX = 122;
                    player.TY = 125;
                    player.OldX = 122;
                    player.OldY = 125;
                }

                player.VisibleMonsters.Clear();

                // 1. Send PMSG_CHARACTER_REGEN_SEND (C3:F3:04) ENCRYPTED to the player themselves so that main.exe runs the full respawn
                await player.Session.SendEncryptedAsync(WorldPacketBuilder.CharacterRegenSend(player), ct);

                // 2. Notify the appearance on the map to other nearby players in the respawn zone
                var appearPacket = WorldPacketBuilder.ViewportPlayerAppear(new[] { player });
                foreach (var other in _players.All)
                {
                    if (other.Index != player.Index && other.WorldEntered && other.Map == player.Map)
                    {
                        await other.Session.SendAsync(appearPacket, ct);
                    }
                }
            }
        }
    }

    private static (bool IsSpell, byte SkillId) GetMonsterAttackSkill(int monsterClass, int attackRange, int attackType, int monsterSkill)
    {
        if (monsterClass is 66 or 73 or 77 or 89 or 95 or 112 or 118 or 124 or 130 or 143)
        {
            byte skill = (monsterClass is 66 or 73 or 38)
                ? (byte)Random.Shared.Next(0, 2)
                : (byte)1;
            return (true, skill);
        }

        if (monsterSkill > 0)
        {
            return (true, (byte)monsterSkill);
        }

        if (attackType > 0 && attackType < 100)
        {
            return (true, (byte)attackType);
        }

        if (attackRange > 1)
        {
            return (true, 0);
        }

        if (monsterClass is 144 or 174 or 182 or 190 or 260 or 268 or 145 or 146 or 147 or 148 or 160 or 175 or 176 or 177 or 178 or 180 or 183 or 184 or 185 or 186 or 188 or 191 or 192 or 193 or 194 or 196 or 261 or 262 or 263 or 264 or 266 or 269 or 270 or 271 or 272 or 274)
        {
            return Random.Shared.Next(2) == 0 ? (true, (byte)0) : (false, (byte)0);
        }

        if (monsterClass is 149 or 179 or 187 or 195 or 265 or 273)
        {
            return (true, (byte)Random.Shared.Next(0, 2));
        }

        if (monsterClass is 161 or 181 or 189 or 197 or 267 or 275)
        {
            return (true, (byte)Random.Shared.Next(1, 7));
        }

        if (monsterClass is 163 or 165 or 167 or 169 or 171 or 173)
        {
            return (true, 0);
        }

        return (false, 0);
    }

    private static byte GetDir8(int signX, int signY, byte defaultDir) => (signX, signY) switch
    {
        (0, -1) => 0,
        (1, -1) => 1,
        (1, 0) => 2,
        (1, 1) => 3,
        (0, 1) => 4,
        (-1, 1) => 5,
        (-1, 0) => 6,
        (-1, -1) => 7,
        _ => defaultDir
    };
}
