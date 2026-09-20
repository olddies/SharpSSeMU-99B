using MuServer.GameServer.Config;
using MuServer.GameServer.Protocol;
using MuServer.Shared.Localization;
using MuServer.Shared.Logging;

namespace MuServer.GameServer.World;

/// <summary>
/// Puerto simplificado del ciclo periódico gObjViewportProc()/CViewport (User.cpp/Viewport.cpp):
/// en vez de replicar el mecanismo interno de VpPlayer[]/VpPlayer2[] por objeto, se hace un barrido
/// completo O(n²) cada tick sobre todos los jugadores online (aceptable para esta fase: sin
/// monstruos/NPCs, la cantidad de objetos es la de jugadores conectados) y se decide visibilidad
/// mutua directamente por mapa+rango. El resultado en el cable es el mismo: paquetes de aparecer
/// (0x12) y desaparecer (0x14) exactamente cuando corresponde.
///
/// Un jugador participa de este barrido desde que entra al mundo, porque RegenOk arranca en false
/// (=0 en el original, ver PlayerObject.RegenOk) -- PMSG_CHARACTER_MOVE_VIEWPORT_ENABLE (0xF3:0x12)
/// solo es relevante después de un teleport/gate real (que pone RegenOk en true/1), mecanismo que
/// este build todavía no tiene enchufado (ver doc-comment de PlayerObject.RegenOk).
/// </summary>
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

    /// <summary>Cada cuántos ticks (200ms cada uno) se manda PMSG_PARTY_LIFE_SEND -- ~2s, dentro del
    /// rango razonable para un broadcast periódico de barras de vida de grupo (el original no fija
    /// un período exacto documentado en el brief de investigación, solo confirma que es periódico).</summary>
    private const int PartyLifeTickInterval = 10;

    /// <summary>Puerto de CharacterAutoRecuperation (ObjectManager.cpp:1729-1758): el original corre
    /// cada 1s pero solo regenera maná cada 3er tick (~3s) -- acá el tick base es 200ms, así que 15
    /// ticks ≈ 3s.</summary>
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
                continue; // todavía no mandó 0xF3:0x12 -- no procesa su propia lista de visibles
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

    /// <summary>Puerto de la porción "items" de CMap::StateSetDestroy (Map.cpp:433-474): libera
    /// cualquier item de piso cuyo tiempo de vida venció y avisa a quien lo tuviera a la vista
    /// (0xC2:0x21, <see cref="WorldPacketBuilder.ViewportItemDestroy"/>). No distingue por mapa al
    /// mandar el aviso -- cada observador solo tiene en <see cref="PlayerObject.VisibleGroundItems"/>
    /// los índices de SU propio mapa, así que un índice que no le pertenece simplemente no está en su
    /// set y no genera ruido.</summary>
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

    /// <summary>Puerto simplificado de CViewport::CreateViewportItem/DestroyViewportItem
    /// (Viewport.cpp:376-417,499-525) -- mismo patrón de barrido O(n) por observador que
    /// <see cref="TickMonstersForObserverAsync"/>, aplicado a <see cref="GroundItemRegistry"/> en vez
    /// del registro de monstruos.</summary>
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
                g.JustDropped = false; // el bit de "recién caído" solo se manda la primera vez
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

    /// <summary>
    /// Puerto de la parte de respawn de ObjectSetStateCreate/ObjectSetStateProc
    /// (ObjectManager.cpp:94-99,197-246): pasado MaxRegenMillis+1000ms desde la muerte, el monstruo
    /// revive en su punto de spawn original (gObjMonsterRegen). Antes de revivir, gObjMonsterRegen
    /// (Monster.cpp:369) llama gObjClearViewport -- recién en ESE momento el cadáver se saca de la
    /// vista de quien lo veía, no al morir (ver el comentario de ClientProtocolHandler.
    /// OnMonsterDeathAsync). Acá se porta ese mismo orden: primero ViewportDestroy a los que todavía
    /// lo tenían visible, después el respawn. El paquete de aparición (0x13) lo manda naturalmente el
    /// barrido normal de abajo, porque ya no va a estar en VisibleMonsters de nadie.
    /// </summary>
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

    /// <summary>
    /// Puerto simplificado del mismo mecanismo de arriba pero para monstruos (Viewport.cpp: la
    /// función original recorre TODOS los objetos, jugadores y monstruos por igual; acá se separa
    /// en dos pasadas porque son dos colecciones/tipos C# distintos). Actualiza
    /// <see cref="PlayerObject.VisibleMonsters"/> y también <see cref="Monster.VisibleTo"/> (usado
    /// por ClientProtocolHandler para dirigir los paquetes de daño/muerte solo a quien realmente ve
    /// al monstruo, equivalente a MsgSendV2/VpPlayer2[] del original).
    /// </summary>
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

            // Un cadáver (IsDead pero todavía no respawneó, ver RespawnDeadMonsters) sigue en
            // currentlyVisible para que esta pasada no lo trate como "recién invisible" -- pero nunca
            // se anuncia como recién aparecido, porque ya se avisó su muerte con el paquete 0x17.
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

    /// <summary>
    /// Puerto simplificado de CObjectManager::CharacterAutoRecuperation (ObjectManager.cpp:1729-1758),
    /// rama de maná: <c>Mana += (MaxMana*MPRecoveryRate[clase])/100</c>, tope en MaxMana, cada
    /// <see cref="ManaRegenTickInterval"/> ticks (~3s, ver comentario de la constante). Simplificaciones
    /// documentadas: sin el bono de "+3pp si no atacó en los últimos 5s" (MPAutoRecuperationTime no se
    /// trackea), sin bonos de item/efecto activo (MPRecoveryRate/EffectOption.AddMPRecoveryRate, no
    /// portados), y SIN regeneración de vida (HPRecoveryRate vale 0 para las 5 clases en el .dat real
    /// shippeado, así que el original tampoco regenera vida por este mecanismo -- omitirla no cambia
    /// el comportamiento default). BP se regenera con la misma fórmula/constantes (BPRecoveryRate).
    /// </summary>
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

    /// <summary>
    /// Puerto de User.cpp:2535-2541 (rama <c>GetTickCount()-lpObj->AutoSaveTime &gt; 600000</c>):
    /// autoguardado incondicional cada 10 minutos para TODO jugador conectado, sin importar si
    /// combatió o no -- éste es el mecanismo real que garantiza que la posición/progreso se persista
    /// en cualquier sesión (caminar sin pelear, quedarse en el pueblo, etc.), a diferencia del
    /// guardado throttled a 60s de <see cref="ClientProtocolHandler.ApplyExperienceGainAsync"/>, que
    /// solo dispara con subidas de nivel. Ver PlayerObject.AutoSaveTime (mismo sentinel
    /// DateTime.MinValue = "nunca guardado" que hace que el primer chequeo tras entrar al mundo ya
    /// pueda disparar un guardado, igual que el original con AutoSaveTime en 0).
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

    /// <summary>Puerto de la parte periódica de GCPartyLifeSend (llamada desde User.cpp:3488) --
    /// manda las barras de vida/maná de cada grupo con más de un miembro cada
    /// <see cref="PartyLifeTickInterval"/> ticks.</summary>
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

            // 1. Validar si el objetivo existente sigue siendo válido
            PlayerObject? target = null;
            if (monster.TargetIndex != -1)
            {
                if (!_players.TryGet(monster.TargetIndex, out target)
                    || !target.WorldEntered
                    || target.Life == 0
                    || target.Map != monster.Map
                    || !monster.VisibleTo.Contains(target.Index) // Prevenir daño invisible fuera del Viewport
                    || (map != null && map.IsSafeZone(target.X, target.Y)))
                {
                    monster.TargetIndex = -1;
                    target = null;
                }
                else
                {
                    // Validar si el objetivo se alejó a más de 10 casillas
                    double distToTarget = Math.Sqrt(Math.Pow(monster.X - target.X, 2) + Math.Pow(monster.Y - target.Y, 2));
                    if (distToTarget > 10.0)
                    {
                        monster.TargetIndex = -1;
                        target = null;
                    }
                }
            }

            // 2. Si no tiene objetivo, buscar el jugador MÁS CERCANO en su Viewport
            if (monster.TargetIndex == -1)
            {
                // Un monstruo SOLO puede ver jugadores que lo estén viendo en su viewport (VisibleTo)
                PlayerObject? closest = null;
                double closestDist = double.MaxValue;

                foreach (var obsIndex in monster.VisibleTo)
                {
                    if (_players.TryGet(obsIndex, out var p) && p.WorldEntered && p.Life > 0 && p.Map == monster.Map)
                    {
                        if (map != null && map.IsSafeZone(p.X, p.Y)) continue; // Jugadores en ciudad están protegidos

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

                    // Al detectar por primera vez un jugador, dar un tiempo de gracia de reacción (1000ms)
                    monster.LastAttackTime = now;
                    monster.LastMoveTime = now;
                    continue; // Espera al próximo tick para reaccionar
                }
            }

            if (target == null)
            {
                // Movimiento Autónomo (Roaming pasivo del monstruo en su área de spawn)
                if (now >= monster.LastMoveTime.AddSeconds(3 + Random.Shared.Next(0, 3)))
                {
                    monster.LastMoveTime = now;

                    // Un paso de una casilla en una dirección al azar, aceptado sólo si no aleja al
                    // monstruo más de Dis de DONDE APARECIÓ. Es el puerto de gObjMonsterMoveCheck
                    // (Monster.cpp:430-462): distancia euclídea contra StartX/StartY, con el Dis de
                    // su fila de spawn.
                    //
                    // Antes esto medía contra SpawnEntry.X/Y y encima recortaba el radio a 3. En los
                    // spawns de área (Type 1) SpawnEntry.X/Y es la esquina del rectángulo COMPARTIDO,
                    // así que los 85 monstruos del área de Lorencia terminaban todos apretados en un
                    // cuadrado de 7x7 sobre esa esquina, caminando en círculos y entrando y saliendo
                    // del viewport todo el tiempo.
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

            // 3. Calcular distancia euclídea real
            double eucDist = Math.Sqrt(Math.Pow(monster.X - target.X, 2) + Math.Pow(monster.Y - target.Y, 2));
            double attackRange = monster.AttackRange > 0 ? monster.AttackRange : 1.5;

            int attackCooldownMs = monster.AttackSpeed > 0 ? monster.AttackSpeed : 1500;

            if (eucDist <= attackRange)
            {
                // Rango de Ataque alcanzado: verificar Cooldown de velocidad de ataque del monstruo
                if (now < monster.LastAttackTime.AddMilliseconds(attackCooldownMs)) continue;

                monster.LastAttackTime = now;

                // Girar la dirección del monstruo hacia el jugador
                int signX = Math.Sign(target.X - monster.X);
                int signY = Math.Sign(target.Y - monster.Y);
                monster.Dir = GetDir8(signX, signY, monster.Dir);

                var (isSpell, skillId) = GetMonsterAttackSkill(monster.MonsterClass, monster.AttackRange, monster.AttackType, monster.MonsterSkill);

                // ---- Paso 1: miss/dodge (puerto de CAttack::MissCheck, Attack.cpp:987-1031) ----
                // Mismo cálculo que OnAttackAsync (ver ClientProtocolHandler.cs), pero acá el atacante
                // es el monstruo y el defensor el jugador.
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

                // ---- Paso 2: defensa del objetivo (CAttack::GetTargetDefense, Attack.cpp:1117-1154) ----
                // La defensa se reduce a la mitad cuando el objetivo es OBJECT_USER -- acá el objetivo
                // siempre es un jugador, así que la mitad se aplica siempre (no solo para hechizos).
                int targetDefense = Math.Max((int)target.Defense * 50 / 100, 0);

                // ---- Paso 3: daño crudo ----
                // Melee: CAttack::GetAttackDamage, rama OBJECT_MONSTER (Attack.cpp:1156-1307) -- usa
                // PhysiDamageMin/Max del monstruo directamente, sin crítico/excelente (esos rolls solo
                // existen en la rama de atacante jugador).
                // Hechizo: CAttack::GetAttackDamageWizard (Attack.cpp:1309-1384) -- los monstruos nunca
                // tienen MagicDamageMin/Max seteado (gObjSetMonster, Monster.cpp:206-365, no lo toca),
                // así que el daño mágico sale pura y exclusivamente del propio skill
                // (DamageMin/DamageMax de Skill.txt, ver SkillInfo.cs), igual que el término
                // "lpObj->MagicDamageMin + lpSkill->m_DamageMin" con la parte del monstruo en cero.
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

                // ---- Paso 3: piso de daño según el nivel del ATACANTE (Attack.cpp:318-322) ----
                int minDamage = Math.Max(monster.Level / 10, 1);

                if (damage < minDamage)
                {
                    damage = minDamage + Random.Shared.Next(minDamage);
                }

                // ---- Paso 4: multiplicador opcional por skill (SkillDamage.txt -- no-op con los datos
                // reales), solo aplica del lado hechizo, igual que GetAttackDamageWizard ----
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
                // Persecución: Mover el monstruo 1 casilla en dirección al jugador
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
                    continue; // No entra a la ciudad ni atraviesa obstáculos
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
                // Perdió el rastro (más de 7 casillas): Soltar objetivo
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

                // 1. Enviar PMSG_CHARACTER_REGEN_SEND (C3:F3:04) CIFRADO al propio jugador para que main.exe ejecute el respawn completo
                await player.Session.SendEncryptedAsync(WorldPacketBuilder.CharacterRegenSend(player), ct);

                // 2. Notificar aparición en el mapa a otros jugadores cercanos en la zona de respawn
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
