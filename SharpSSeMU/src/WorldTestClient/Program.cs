using WorldTestClient;

// End-to-end test of Phase 2 of the GameServer: two simulated "clients" (with the real encryption keys) that
// log in, select a character, enter the world, see each other appear through the viewport, and confirm that
// one's movement propagates to the other.

string host = args[0];
int port = int.Parse(args[1]);
string serial = args[2];
string version = args[3];
string enc1 = args[4];
string dec2 = args[5];
string account1 = args[6];
string password1 = args[7];
string char1 = args[8];
string account2 = args[9];
string password2 = args[10];
string char2 = args[11];

// Optional: NEW account (without characters) to test the real character creation flow via GameServer (C1:F3:01)
// -- the bug reported by the user testing with the real client (main.exe): the account arrives with 0
// characters, the client sends F3:01 on confirming name/class, and until now the GameServer did not handle it
// at all (only the DataServer side existed, used only by the direct seeding of these same test scripts). It
// only runs if these 3 extra args are passed.
string? account3 = args.Length > 14 ? args[12] : null;
string? password3 = args.Length > 14 ? args[13] : null;
string? newCharName = args.Length > 14 ? args[14] : null;

// Optional: enables the Devil Square section (Phase 6) at the end of the test -- it uses a DEDICATED
// account/character (not Hero1/Hero2) with a very high Strength seeded via SQL, because the real monsters of
// Devil Square 1 (Skeleton Archer/Cyclops, HP 850-1100, Defense 35-45) are unbeatable with this phase's
// placeholder damage formula using the low Strength of a newly created character -- raising Hero1's Strength
// instead would break the timing that the rest of the shared flow (Phase 4/5) already assumes (it one-shots --
// 1 hit -- the "hit it again, it should not be dead yet" tests). The ticket ("Devil's Invitation",
// GET_ITEM(14,19)) is seeded in slot 13 of this character. The server should already have received 'ds
// forcestart' via console before this process starts.
bool dsEnabled = args.Length > 17;
string? account4 = dsEnabled ? args[15] : null;
string? password4 = dsEnabled ? args[16] : null;
string? char4 = dsEnabled ? args[17] : null;

// Optional: enables the skills/mana section after Phase 4's melee combat block -- it reuses client A (Hero1)
// and the already seeded test monster, so no dedicated account is needed; it is enough to pass this flag as arg
// 12 (position deliberately BEFORE account3/account4 so as not to interfere with those other optional blocks,
// which are triggered by args length, not by content).
bool skillEnabled = args.Length > 12 && args[12] == "skills";

// Optional: enables the shops/NPC/action/stat point section (Task #32) -- the same gating criterion by value of
// args[12] as skillEnabled (mutually exclusive, see comment above). It uses client A and assumes a DEDICATED
// test environment (gameserver_shop_e2e_test.py) with no combat monsters loaded, so the only "monster" (the
// shop NPC) has a deterministic index 0.
bool shopEnabled = args.Length > 12 && args[12] == "shop";

// Optional (only with shopEnabled): name of a SECOND character seeded in the SAME account as char1 (account1)
// -- direct regression of the bug reported by the user ("I only see the first one I created" on the selection
// screen), see the alsoExpectName block in LoginAndEnterAsync.
string? secondCharName1 = shopEnabled && args.Length > 13 ? args[13] : null;

// Optional: enables the ground items (drop/pickup) section -- the same gating criterion by value of args[12] as
// shopEnabled/skillEnabled (mutually exclusive). DEDICATED environment (gameserver_grounditem_e2e_test.py) with
// 2 test monsters deliberately deterministic, seeded AFTER Phase 4's shared monster (index 0, a real "Spider"
// from MonsterList.txt) -- so they end up at index 1 = "always drops an item" (ItemRate=1) and index 2 =
// "always drops money" (MoneyRate=1), NOT at 0/1 (index 0 is already taken by the shared combat block above).
bool groundItemEnabled = args.Length > 12 && args[12] == "grounditem";

int failures = 0;

void Check(string name, bool ok, string extra = "")
{
    Console.WriteLine($"[{(ok ? "OK" : "FALLO")}] {name} {extra}");
    if (!ok) failures++;
}

// Casts an attack skill repeating the attempt until it HITS (or the attempts run out). A cast's hit reuses the
// same AttackSuccessRate/DefenseSuccessRate as melee (see the doc- comment of OnSkillAttackAsync in
// ClientProtocolHandler.cs, point 6), so a single attempt without retry makes the test intermittent by design
// (with Hero1/Kris+1 vs Bull Fighter the chance of failing an individual cast is around 17%). Mana is deducted
// on ALL attempts, hit or not (the original UseAttackSkill does it that way, not only on the one that finally
// hits) -- that is why we return the number of attempts, so the caller can compute the total expected
// deduction.
async Task<(FakeMuClient.DecodedPacket ManaPkt, FakeMuClient.DecodedPacket DmgPkt, FakeMuClient.DecodedPacket SkillPkt, int Attempts)>
    CastSkillUntilHitAsync(FakeMuClient client, byte skill, int targetIndex, int maxAttempts, CancellationToken ct)
{
    for (int attempt = 1; attempt <= maxAttempts; attempt++)
    {
        await client.SendSkillAttackAsync(skill, targetIndex);
        var manaPkt = await client.WaitForAsync(0x27, TimeSpan.FromSeconds(5));
        var dmgPkt = await client.WaitForAsync(0xD9, TimeSpan.FromSeconds(5));

        // PMSG_DAMAGE_SEND: bit 0x80 of byte [3] (high half of the target index) is the missFlag.
        bool missed = (dmgPkt.Full[3] & 0x80) != 0;

        if (!missed)
        {
            var skillPkt = await client.WaitForAsync(0x19, TimeSpan.FromSeconds(5));
            return (manaPkt, dmgPkt, skillPkt, attempt);
        }
    }

    throw new TimeoutException(
        $"[{client.Name}] El skill {skill} falló {maxAttempts} veces seguidas contra el objetivo " +
        "(estadísticamente insignificante) -- revisar AttackSuccessRate/DefenseSuccessRate.");
}

async Task<(FakeMuClient Client, byte X, byte Y)> LoginAndEnterAsync(string label, string account, string password, string charName, CancellationToken ct, string? alsoExpectName = null)
{
    var c = new FakeMuClient(label, serial, version, enc1, dec2);
    await c.ConnectAsync(host, port, ct);

    var connectPkt = await c.WaitForAsync(0xF1, TimeSpan.FromSeconds(5), 0x00);
    Console.WriteLine($"[{label}] ConnectClientSend recibido ({connectPkt.Full.Length} bytes)");

    await c.SendLoginAsync(account, password);
    var loginResult = await c.WaitForAsync(0xF1, TimeSpan.FromSeconds(5), 0x01);
    byte result = loginResult.Full[4];
    Check($"[{label}] Login '{account}'", result == 1, $"(resultado={result})");

    // Real port: the client asks for the character list (0xF3:0x00) BEFORE choosing one -- without this step
    // the real client stays stuck on the selection screen (bug found only when testing with the real main.exe;
    // WorldTestClient did not exercise it because it sent 0xF3:0x03 directly).
    await c.SendCharacterListRequestAsync();
    var listPkt = await c.WaitForAsync(0xF3, TimeSpan.FromSeconds(5), 0x00);
    // C1:F3:00 -- [4]=ClassCode [5]=MoveCnt [6]=count. Without ExtWarehouse (that byte does not exist in this
    // build's real struct, Protocol.h of the correct source tree -- see the doc-comment of
    // ClientPacketBuilder.CharacterListSend).
    byte charCount = listPkt.Full[6];
    bool foundInList = false;
    int? alsoFoundAtSlot = null;

    for (int i = 0; i < charCount; i++)
    {
        // slot(1)+Name[10]+pad(1, alignment of Level to WORD, see ClientPackets.CharacterListSend)+
        // Level(2)+CtlCode(1)+CharSet[13] = 28 bytes/entry. Without GuildStatus (it does not exist in this
        // build's real struct).
        int off = 7 + (i * 28);
        string listedName = System.Text.Encoding.ASCII.GetString(listPkt.Full, off + 1, 10).TrimEnd('\0');

        if (string.Equals(listedName, charName, StringComparison.OrdinalIgnoreCase))
        {
            foundInList = true;
        }

        if (alsoExpectName != null && string.Equals(listedName, alsoExpectName, StringComparison.OrdinalIgnoreCase))
        {
            alsoFoundAtSlot = i;
        }
    }

    Check($"[{label}] CHARACTER_LIST_SEND incluye '{charName}'", foundInList, $"(count={charCount})");

    // Direct regression of the bug reported by the user ("I only see the first one I created"): if
    // alsoExpectName is set, the account has 2+ seeded characters and this check confirms that the SECOND one
    // (slot != 0, offset != the first) is also read correctly -- the real bug was the missing alignment padding
    // byte (see ClientPacketBuilder.CharacterListSend), not the CharSet size nor an invented
    // ExtWarehouse/GuildStatus.
    if (alsoExpectName != null)
    {
        Check($"[{label}] CHARACTER_LIST_SEND también incluye el 2do personaje '{alsoExpectName}' (offset correcto)",
            alsoFoundAtSlot != null, $"(count={charCount}, slot={alsoFoundAtSlot})");
    }

    await c.SendCharacterSelectAsync(charName);
    var infoPkt = await c.WaitForAsync(0xF3, TimeSpan.FromSeconds(5), 0x03);
    byte x = infoPkt.Full[4], y = infoPkt.Full[5], map = infoPkt.Full[6];
    Console.WriteLine($"[{label}] CHARACTER_INFO_SEND recibido y descifrado OK -> Map={map} X={x} Y={y}");

    var newInfoPkt = await c.WaitForAsync(0xF3, TimeSpan.FromSeconds(5), 0xE0);
    Console.WriteLine($"[{label}] NEW_CHARACTER_INFO_SEND recibido ({newInfoPkt.Full.Length} bytes)");

    var itemListPkt = await c.WaitForAsync(0xF3, TimeSpan.FromSeconds(5), 0x10);
    byte itemCount = itemListPkt.Full[5]; // C2:F3:10 -- payload empieza en offset 5 (type+size2+head+subh)
    Console.WriteLine($"[{label}] ITEM_LIST_SEND recibido ({itemListPkt.Full.Length} bytes, {itemCount} item(s))");

    await c.SendMoveViewportEnableAsync();

    return (c, x, y);
}

// Phase 4 (~12s respawn) + Phase 5 (party/exp) + Phase 6 (Devil Square: up to ~3 min waiting for the window
// forced by console + real STAND (1 min) + START (1 min) of the test bracket, see the test's DevilSquare.dat)
// add steps.
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(dsEnabled ? 300 : 90));

try
{
    var (clientA, ax, ay) = await LoginAndEnterAsync("A", account1, password1, char1, cts.Token, secondCharName1);
    var (clientB, _, _) = await LoginAndEnterAsync("B", account2, password2, char2, cts.Token);

    Console.WriteLine("\n=== Esperando que el ViewportTicker los haga verse mutuamente (0x12) ===");

    var appearInA = await clientA.WaitForAsync(0x12, TimeSpan.FromSeconds(5));
    Check("[A] Recibió aparición de otro jugador (0x12)", true, $"({appearInA.Full.Length} bytes)");

    var appearInB = await clientB.WaitForAsync(0x12, TimeSpan.FromSeconds(5));
    Check("[B] Recibió aparición de otro jugador (0x12)", true, $"({appearInB.Full.Length} bytes)");

    // Layout regression: PMSG_VIEWPORT_PLAYER is sizeof()=34 in MSVC, not 32 -- it carries 1 padding byte
    // before the WORD count (offset 17) and 1 tail byte (offset 33). Verified with a static_assert by
    // tools/protogen/verify_layout.cpp. The STRIDE is checked and not the total length so as not to depend on
    // how many players the packet carries: C2 header (4) + count (1) + N*34.
    const int viewportPlayerStride = 34;
    int appearBody = appearInA.Full.Length - 5;
    Check("[A] PMSG_VIEWPORT_PLAYER respeta el stride real de 34 bytes",
        appearBody > 0 && appearBody % viewportPlayerStride == 0,
        $"(cuerpo={appearBody}, {appearBody / (double)viewportPlayerStride:0.##} entradas)");

    Console.WriteLine("\n=== A se mueve, verificando que B reciba el 0xD7 ===");

    // Moving one tile to the side of where A entered (real position read from CHARACTER_INFO_SEND, instead of a
    // hardcoded absolute coordinate -- this same Program.cs is shared between the Phase 2/3 tests (characters
    // in a safe zone, for viewport/movement) and Phase 4 (characters outside a safe zone, required to be able
    // to attack), which start at different positions).
    byte moveX = (byte)(ax + 1);
    await clientA.SendMoveAsync(moveX, ay, 3);

    var moveSelf = await clientA.WaitForAsync(0xD7, TimeSpan.FromSeconds(5));
    Check("[A] Recibió su propio MOVE_SEND (eco)", true, $"({moveSelf.Full.Length} bytes)");

    var moveInB = await clientB.WaitForAsync(0xD7, TimeSpan.FromSeconds(5));
    Check("[B] Recibió el MOVE_SEND de A (propagación de movimiento)", true, $"({moveInB.Full.Length} bytes)");

    Console.WriteLine("\n=== A se mueve de nuevo con el path TRUNCADO a 1 byte (como manda el cliente real) ===");

    // Regression of the bug reported in production: MoveRecv.Parse assumed a fixed 8 path bytes and threw
    // ArgumentOutOfRangeException with the real client, which sends fewer bytes than the C++ struct suggests
    // (path[8] is a "ceiling" size, not what actually travels in the packet). WorldTestClient never exposed it
    // because SendMoveAsync builds a full 8-byte path -- this step uses SendMoveShortPathAsync on purpose to
    // reproduce it.
    byte moveX2 = (byte)(moveX + 1);
    await clientA.SendMoveShortPathAsync(moveX2, ay, 3);

    var moveShortSelf = await clientA.WaitForAsync(0xD7, TimeSpan.FromSeconds(5));
    Check("[A] Path truncado (1 byte) no crashea el servidor -- recibió su propio MOVE_SEND", true, $"({moveShortSelf.Full.Length} bytes)");

    Console.WriteLine("\n=== A equipa el arma sembrada (slot 12 -> slot 0, WEAPON1) ===");

    // The data seed (orchestrate_phase3.py) put a real sword (GET_ITEM(0,0)) in slot 12 (first "backpack" slot,
    // right after the 12 equipment ones) of Hero1's inventory.
    await clientA.SendItemMoveAsync(12, 0);

    var moveResult = await clientA.WaitForAsync(0x24, TimeSpan.FromSeconds(5));
    // Logical C1:24 tras reconstruir en el cliente: [0]=C1 [1]=size [2]=head(0x24) [3]=result
    // [4]=slot [5..9]=ItemInfo[0..4] (MAX_ITEM_INFO=5, ver Item.ToWireBytes).
    byte result = moveResult.Full[3];
    byte movedIndex = moveResult.Full[5]; // ItemInfo[0] = Index & 0xFF
    // FIXED: result is not a boolean -- it is TargetFlag (0 for Inventory) on success, 0xFF on failure (exact
    // port of MoveItemToInventoryFromInventory, ItemManager.cpp:1920-1978; see the doc-comment of
    // ClientProtocolHandler.OnItemMoveAsync). Before, result==1 was (incorrectly) expected.
    Check("[A] ITEM_MOVE_SEND resultado OK", result == 0, $"(result={result}, index={movedIndex})");

    var equipSelf = await clientA.WaitForAsync(0xF3, TimeSpan.FromSeconds(5), 0x13);
    // Logical C1:F3:13: [0]=C1 [1]=size [2]=head [3]=subh [4..5]=index [6..18]=CharSet[0..12].
    byte charSetWeapon1 = equipSelf.Full[7]; // CharSet[1] = arma mano izquierda
    Check("[A] ITEM_EQUIPMENT_SEND refleja el arma equipada", charSetWeapon1 != 0xFF, $"(CharSet[1]={charSetWeapon1})");

    var appearRefreshB = await clientB.WaitForAsync(0x12, TimeSpan.FromSeconds(5));
    Check("[B] Ve el CharSet actualizado de A tras equipar (0x12 de refresco)", true, $"({appearRefreshB.Full.Length} bytes)");

    Console.WriteLine("\n=== Fase 4: A ataca al monstruo de prueba sembrado hasta matarlo ===");

    // The test monster was already within A's view range since entering the world (long before this point of
    // the test), so its appearance packet (0x13) was already sent and silently consumed by one of the earlier
    // WaitForAsync calls (which discard any packet that does not match the head they are waiting for) -- there
    // is no point waiting for ANOTHER 0x13 here. Since it is the only monster loaded in this test environment
    // and MonsterRegistry assigns indices starting at 0, its index is deterministic.
    const int monsterIndex = 0;
    Console.WriteLine($"[A] Usando el monstruo de prueba (único cargado), index={monsterIndex}");

    bool monsterDied = false;
    int hits = 0;

    for (int i = 0; i < 40 && !monsterDied; i++)
    {
        await clientA.SendAttackAsync(monsterIndex, 120, 3);
        await clientA.WaitForAsync(0xD9, TimeSpan.FromSeconds(5)); // PMSG_DAMAGE_SEND (o miss, mismo head)
        hits++;

        try
        {
            // PMSG_USER_DIE_SEND (C1:17) if it died on this hit. YOU HAVE TO LOOK AT WHICH INDEX died: the
            // server uses the same head to announce that a PLAYER died (ViewportTicker sends UserDieSend when a
            // monster kills someone), so accepting the mere arrival of the 0x17 makes the test claim "I killed
            // the monster" when in fact the character itself died -- a false positive that hid that the attack
            // never managed to do damage. Layout: [3..4]=index of the dead one, [5]=skill, [6..7]=index of the
            // killer.
            var diePkt = await clientA.WaitForAsync(0x17, TimeSpan.FromMilliseconds(300));
            int deadIndex = (diePkt.Full[3] << 8) | diePkt.Full[4];

            if (deadIndex == monsterIndex)
            {
                monsterDied = true;
            }
            else
            {
                Console.WriteLine($"[A] 0x17 recibido pero murió el índice {deadIndex}, no el monstruo {monsterIndex}");
            }
        }
        catch (TimeoutException)
        {
            // it did not die on this hit, the loop continues
        }
    }

    Check("[A] Mató al monstruo de prueba", monsterDied, $"(en {hits} golpe(s) de ataque)");

    var expPkt = await clientA.WaitForAsync(0x9C, TimeSpan.FromSeconds(5));
    Check("[A] Recibió REWARD_EXPERIENCE_SEND (0x9C) al matarlo", true, $"({expPkt.Full.Length} bytes)");

    Console.WriteLine("\n=== Fase 4: esperando el respawn del monstruo (~12s) ===");
    await Task.Delay(TimeSpan.FromSeconds(12), cts.Token);

    await clientA.SendAttackAsync(monsterIndex, 120, 3);
    var respawnDamage = await clientA.WaitForAsync(0xD9, TimeSpan.FromSeconds(5));
    Check("[A] El monstruo revivió (respondió a un nuevo ataque tras el respawn)", true, $"({respawnDamage.Full.Length} bytes)");

    if (skillEnabled)
    {
        Console.WriteLine("\n=== Fase skills: A castea 'Fire Ball' (skill 4) sobre el monstruo de prueba ===");

        // Fire Ball (SkillList.txt index 4): Damage=8, MP=3, Range=6, usable by DW/MG with no level/energy
        // requirement -- Hero1 (DW, Energy=30 from the default_class_type seed) can cast it right away. Two
        // casting rounds (each retried until it hits, see CastSkillUntilHitAsync) allow verifying the mana
        // deduction deterministically without depending on knowing the character's exact initial mana nor on
        // the first hit roll going our way.
        const byte fireBall = 4;
        const int fireBallMana = 3;
        const int maxCastAttempts = 20;

        var (manaPkt1, dmgPkt1, skillPkt1, attempts1) =
            await CastSkillUntilHitAsync(clientA, fireBall, monsterIndex, maxCastAttempts, cts.Token);

        // PMSG_MANA_SEND: [3]=type [4..5]=mana(BE) [6..7]=bp(BE).
        int mana1 = (manaPkt1.Full[4] << 8) | manaPkt1.Full[5];
        // PMSG_DAMAGE_SEND: [3]=hi(+missFlag) [4]=lo [5..6]=damage(BE, clampeado a 65000) ...
        int skillDamage1 = (dmgPkt1.Full[5] << 8) | dmgPkt1.Full[6];
        // PMSG_SKILL_ATTACK_SEND: [3]=skill [4..5]=caster(BE) [6..7]=target(BE).
        byte skillEcho1 = skillPkt1.Full[3];

        Check("[A] SKILL_ATTACK_SEND (0x19) confirma el skill casteado", skillEcho1 == fireBall, $"(skill={skillEcho1}, intentos={attempts1})");
        Check("[A] Recibió DAMAGE_SEND del casteo de Fire Ball", true, $"(damage={skillDamage1}, mana tras casteo={mana1})");

        var (manaPkt2, _, _, attempts2) =
            await CastSkillUntilHitAsync(clientA, fireBall, monsterIndex, maxCastAttempts, cts.Token);
        int mana2 = (manaPkt2.Full[4] << 8) | manaPkt2.Full[5];
        int expectedCost2 = fireBallMana * attempts2;

        Check("[A] La segunda ronda de casteo(s) descontó exactamente maná*intentos", mana2 == mana1 - expectedCost2,
            $"(mana1={mana1}, mana2={mana2}, intentos2={attempts2}, costo esperado={expectedCost2})");

        Console.WriteLine("\n=== Fase skills: esperando ~3.5s de regeneración periódica de maná ===");
        await Task.Delay(TimeSpan.FromSeconds(3.5), cts.Token);
        var manaRegenPkt = await clientA.WaitForAsync(0x27, TimeSpan.FromSeconds(5));
        int manaRegen = (manaRegenPkt.Full[4] << 8) | manaRegenPkt.Full[5];
        Check("[A] El maná se regeneró solo con el tiempo (sin castear de nuevo)", manaRegen > mana2,
            $"(mana antes de esperar={mana2}, mana tras ~3.5s={manaRegen})");
    }

    if (shopEnabled)
    {
        Console.WriteLine("\n=== Tienda (Task #32): A habla con el NPC, ve el listado, compra y vende un item ===");

        // Dedicated test environment (gameserver_shop_e2e_test.py): the combat monster (Spider, index 0, see
        // the Phase 4 block above, which always runs) is spawned first (MonsterRegistry.SpawnAll), and AFTER it
        // the shop NPC (Program.cs, a single NPC in ShopManager.txt) -- that is why the NPC has a deterministic
        // index 1, not 0.
        const int npcIndex = 1;

        await clientA.SendNpcTalkAsync(npcIndex);

        var npcTalkPkt = await clientA.WaitForAsync(0x30, TimeSpan.FromSeconds(5));
        byte npcTalkResult = npcTalkPkt.Full[3];
        Check("[A] NPC_TALK_SEND result=0 (puede hablar con el NPC de tienda)", npcTalkResult == 0, $"(result={npcTalkResult})");

        var shopListPkt = await clientA.WaitForAsync(0x31, TimeSpan.FromSeconds(5));
        // C2:31 sin sub -- header C2 son 3 bytes (type+size16), no 2 como C1: [0]=C2 [1..2]=size
        // [3]=head(0x31), payload arranca en [4]: [4]=type [5]=count [6]=slot [7..11]=ItemInfo[0..4]
        // (MAX_ITEM_INFO=5).
        byte shopType = shopListPkt.Full[4];
        byte shopCount = shopListPkt.Full[5];
        byte shopSlot = shopListPkt.Full[6];
        byte shopItemLo = shopListPkt.Full[7];
        Check("[A] SHOP_ITEM_LIST_SEND trae 1 item en el slot esperado", shopType == 0 && shopCount == 1 && shopSlot == 0,
            $"(type={shopType}, count={shopCount}, slot={shopSlot})");

        // GET_ITEM(14,13)=14*32+13=461 (MaxItemType real de este build=32) -- 461 & 0xFF = 205.
        // "Jewel of Bless" del Item.txt de prueba.
        const byte expectedItemLo = 461 & 0xFF;
        Check("[A] El item de la tienda es el esperado (GET_ITEM(14,13))", shopItemLo == expectedItemLo,
            $"(ItemInfo[0]={shopItemLo}, esperado={expectedItemLo})");

        await clientA.SendItemBuyAsync(shopSlot);

        var buyPkt = await clientA.WaitForAsync(0x32, TimeSpan.FromSeconds(5));
        byte buyResult = buyPkt.Full[3];
        byte boughtItemLo = buyPkt.Full[4];
        Check("[A] ITEM_BUY_SEND result != 0xFF (compra exitosa)", buyResult != 0xFF, $"(result={buyResult})");
        Check("[A] El item comprado coincide con el de la tienda", boughtItemLo == expectedItemLo, $"(ItemInfo[0]={boughtItemLo})");

        var moneyAfterBuyPkt = await clientA.WaitForAsync(0x22, TimeSpan.FromSeconds(5));
        // C3:22 (money, GCMoneySend) -- [3]=0xFE, [4..7]=money BIG-ENDIAN.
        Check("[A] Recibió MONEY_SEND tras comprar (marcador 0xFE)", moneyAfterBuyPkt.Full[3] == 0xFE);
        uint moneyAfterBuy = ((uint)moneyAfterBuyPkt.Full[4] << 24) | ((uint)moneyAfterBuyPkt.Full[5] << 16)
            | ((uint)moneyAfterBuyPkt.Full[6] << 8) | moneyAfterBuyPkt.Full[7];
        Console.WriteLine($"[A] Dinero tras comprar: {moneyAfterBuy}");

        byte boughtSlot = buyResult;
        await clientA.SendItemSellAsync(boughtSlot);

        var sellPkt = await clientA.WaitForAsync(0x33, TimeSpan.FromSeconds(5));
        byte sellResult = sellPkt.Full[3];
        // PMSG_ITEM_SELL_SEND: DWORD 'money' en orden natural (little-endian en x86, ver ItemManager.h:181-186).
        uint moneyAfterSell = sellPkt.Full[4] | ((uint)sellPkt.Full[5] << 8) | ((uint)sellPkt.Full[6] << 16) | ((uint)sellPkt.Full[7] << 24);
        Check("[A] ITEM_SELL_SEND result=1 (venta exitosa)", sellResult == 1, $"(result={sellResult})");
        Check("[A] El dinero aumentó tras vender", moneyAfterSell > moneyAfterBuy, $"(antes={moneyAfterBuy}, después={moneyAfterSell})");

        // The price of the Jewel of Bless comes from Data/Item/ItemValue.txt (row "14,013 * * 9000000"), not
        // from the general formula: by the formula it would give 18,700, which is what this test accepted as
        // good before the GameServer loaded that table. The sale difference is checked and not the absolute
        // balance because the shared combat block runs before and can add money from a drop.
        const uint precioVentaEsperado = 3_000_000; // 9.000.000 / 3, el mismo tercio del original
        uint cobradoAlVender = moneyAfterSell - moneyAfterBuy;
        Check($"[A] Vender el Jewel of Bless paga {precioVentaEsperado} (ItemValue.txt, no la fórmula general)",
            cobradoAlVender == precioVentaEsperado, $"(pagó {cobradoAlVender})");

        await clientA.SendNpcTalkCloseAsync();

        Console.WriteLine("\n=== Acción (0x18): A hace una pose/emote, verifica la difusión en viewport de B ===");
        await clientA.SendActionAsync(3, 16); // dir=3, action=16 (arbitrario -- el servidor no valida el rango)
        var actionPkt = await clientB.WaitForAsync(0x18, TimeSpan.FromSeconds(5));
        Check("[B] Recibió la acción de A por el viewport (0x18)", actionPkt.Full.Length >= 5, $"({actionPkt.Full.Length} bytes)");

        Console.WriteLine("\n=== Punto de stat (F3:06): A sin puntos disponibles -> ok=false pero responde ===");
        await clientA.SendLevelUpPointAsync(0);
        var statPkt = await clientA.WaitForAsync(0xF3, TimeSpan.FromSeconds(5), 0x06);
        Check("[A] Recibió LEVEL_UP_POINT_SEND (aunque sin puntos para asignar)", statPkt.Full.Length >= 4, $"({statPkt.Full.Length} bytes)");
    }

    if (groundItemEnabled)
    {
        // Dedicated test environment (gameserver_grounditem_e2e_test.py): besides Phase 4's shared combat
        // monster (index 0, above), 2 extra deterministic test monsters are seeded -- index 1 "always drops an
        // item" (ItemRate=1) and index 2 "always drops money" (MoneyRate=1) -- so as not to depend on the real
        // chance of MonsterList.txt.
        const int itemDropperIndex = 1;
        const int moneyDropperIndex = 2;

        async Task<bool> KillAsync(FakeMuClient client, int targetIndex)
        {
            bool died = false;

            for (int i = 0; i < 15 && !died; i++)
            {
                await client.SendAttackAsync(targetIndex, 120, 3);
                await client.WaitForAsync(0xD9, TimeSpan.FromSeconds(5));

                try
                {
                    await client.WaitForAsync(0x17, TimeSpan.FromMilliseconds(300));
                    died = true;
                }
                catch (TimeoutException)
                {
                }
            }

            return died;
        }

        Console.WriteLine("\n=== Items de piso: A mata al monstruo que siempre dropea item ===");
        bool itemDropperDied = await KillAsync(clientA, itemDropperIndex);
        Check("[A] Mató al monstruo 'siempre dropea item'", itemDropperDied);

        // IMPORTANT: TryDropLootAsync sends 0x20 BEFORE the experience (0x9C) -- see OnMonsterDeathAsync
        // (Log.Add -> TryDropLootAsync -> ... -> GrantExperienceAsync). Since WaitForAsync discards any packet
        // that does not match the head sought, 0x20 has to be waited for FIRST here or the 0x9C that comes
        // afterwards never shows up; the experience itself is already thoroughly tested in Phase 4, so there is
        // no need to wait for it in this block.
        var appearA = await clientA.WaitForAsync(0x20, TimeSpan.FromSeconds(5));
        // C2:20 without sub -- [4]=count [5]=indexHi(|0x80 if just fell) [6]=indexLo [7]=x [8]=y [9..13]=ItemInfo[0..4].
        byte appearCount = appearA.Full[4];
        int groundIndex = ((appearA.Full[5] & 0x7F) << 8) | appearA.Full[6];
        bool justDropped = (appearA.Full[5] & 0x80) != 0;
        byte droppedItemLo = appearA.Full[9];
        Check("[A] VIEWPORT_ITEM_SEND (0x20) trae 1 item recién caído", appearCount == 1 && justDropped,
            $"(count={appearCount}, justDropped={justDropped}, groundIndex={groundIndex}, ItemInfo[0]={droppedItemLo})");

        var appearB = await clientB.WaitForAsync(0x20, TimeSpan.FromSeconds(5));
        Check("[B] También ve aparecer el item de piso (0x20)", appearB.Full[4] == 1, $"({appearB.Full.Length} bytes)");

        Console.WriteLine("\n=== Items de piso: B intenta recogerlo (loot-lock del dueño A, debe fallar) ===");
        await clientB.SendItemGetAsync(groundIndex);
        var getFailPkt = await clientB.WaitForAsync(0x22, TimeSpan.FromSeconds(5));
        byte getFailResult = getFailPkt.Full[3];
        Check("[B] ITEM_GET_SEND result=0xFF (loot-lock del dueño A todavía vigente)", getFailResult == 0xFF, $"(result={getFailResult})");

        Console.WriteLine("\n=== Items de piso: A (el dueño) lo recoge sin problema ===");
        await clientA.SendItemGetAsync(groundIndex);
        var getOkPkt = await clientA.WaitForAsync(0x22, TimeSpan.FromSeconds(5));
        byte getOkResult = getOkPkt.Full[3];
        byte pickedItemLo = getOkPkt.Full[4]; // ItemInfo[0]
        Check("[A] ITEM_GET_SEND result != 0xFF (recogió el item)", getOkResult != 0xFF, $"(result={getOkResult})");
        Check("[A] El item recogido coincide con el que apareció en el piso", pickedItemLo == droppedItemLo,
            $"(recogido={pickedItemLo}, esperado={droppedItemLo})");

        Console.WriteLine("\n=== Items de piso: recoger el mismo índice de nuevo debe fallar (ya no está) ===");
        await clientA.SendItemGetAsync(groundIndex);
        var getAgainPkt = await clientA.WaitForAsync(0x22, TimeSpan.FromSeconds(5));
        byte getAgainResult = getAgainPkt.Full[3];
        Check("[A] Segundo intento sobre el mismo slot da 0xFF (ya no hay nada ahí)", getAgainResult == 0xFF, $"(result={getAgainResult})");

        Console.WriteLine("\n=== Items de piso: A tira el item recién recogido al piso de nuevo ===");
        await clientA.SendItemDropAsync(ax, ay, getOkResult); // getOkResult = inventory slot where the picked-up item ended up
        var dropPkt = await clientA.WaitForAsync(0x23, TimeSpan.FromSeconds(5));
        byte dropResult = dropPkt.Full[3];
        Check("[A] ITEM_DROP_SEND result=1 (tiró el item de vuelta al piso)", dropResult == 1, $"(result={dropResult})");

        var appearAfterDropA = await clientA.WaitForAsync(0x20, TimeSpan.FromSeconds(5));
        Check("[A] Ve reaparecer su propio item tirado (0x20, vía el tick de viewport)", appearAfterDropA.Full[4] == 1, $"({appearAfterDropA.Full.Length} bytes)");
        var appearAfterDropB = await clientB.WaitForAsync(0x20, TimeSpan.FromSeconds(5));
        Check("[B] También ve el item recién tirado por A (0x20)", appearAfterDropB.Full[4] == 1, $"({appearAfterDropB.Full.Length} bytes)");

        int groundIndex2 = ((appearAfterDropA.Full[5] & 0x7F) << 8) | appearAfterDropA.Full[6];
        await clientA.SendItemGetAsync(groundIndex2);
        var getAfterDropPkt = await clientA.WaitForAsync(0x22, TimeSpan.FromSeconds(5));
        Check("[A] Recogió de vuelta el item que acababa de tirar", getAfterDropPkt.Full[3] != 0xFF, $"(result={getAfterDropPkt.Full[3]})");

        var destroyB = await clientB.WaitForAsync(0x21, TimeSpan.FromSeconds(5));
        // C2:21 sin sub -- [4]=count [5..6]=index (sin bit de flag).
        Check("[B] Ve desaparecer el item del piso al tick siguiente (0x21)", destroyB.Full[4] == 1, $"({destroyB.Full.Length} bytes)");

        Console.WriteLine("\n=== Items de piso: A mata al monstruo que siempre dropea dinero ===");
        bool moneyDropperDied = await KillAsync(clientA, moneyDropperIndex);
        Check("[A] Mató al monstruo 'siempre dropea dinero'", moneyDropperDied);

        var moneyAppear = await clientA.WaitForAsync(0x20, TimeSpan.FromSeconds(5));
        int moneyGroundIndex = ((moneyAppear.Full[5] & 0x7F) << 8) | moneyAppear.Full[6];
        byte moneyItemLo = moneyAppear.Full[9]; // ItemInfo[0] = GET_ITEM(14,15) & 0xFF
        const int moneyItemIndex = 14 * 32 + 15; // GET_ITEM(14,15), ver GroundItem.DropMoney
        Check("[A] El item de piso del segundo monstruo es dinero (GET_ITEM(14,15))", moneyItemLo == (moneyItemIndex & 0xFF),
            $"(ItemInfo[0]={moneyItemLo}, esperado={moneyItemIndex & 0xFF})");

        await clientA.SendItemGetAsync(moneyGroundIndex);
        var moneyGetPkt = await clientA.WaitForAsync(0x22, TimeSpan.FromSeconds(5));
        Check("[A] Recoger el dinero del piso da result=0xFE (marcador de dinero)", moneyGetPkt.Full[3] == 0xFE, $"(result={moneyGetPkt.Full[3]})");
        uint moneyAfterPickup = ((uint)moneyGetPkt.Full[4] << 24) | ((uint)moneyGetPkt.Full[5] << 16) | ((uint)moneyGetPkt.Full[6] << 8) | moneyGetPkt.Full[7];
        Check("[A] El dinero total tras recoger es mayor a 0", moneyAfterPickup > 0, $"(dinero={moneyAfterPickup})");
    }

    Console.WriteLine("\n=== Fase 5: chat público y whisper ===");

    const string chatMsg = "hola mundo";
    await clientA.SendChatAsync(char1, chatMsg);

    var chatEcho = await clientA.WaitForAsync(0x00, TimeSpan.FromSeconds(5));
    string echoName = System.Text.Encoding.ASCII.GetString(chatEcho.Full, 3, 10).TrimEnd('\0');
    string echoMsg = System.Text.Encoding.ASCII.GetString(chatEcho.Full, 13, 60).TrimEnd('\0');
    Check("[A] Recibió el eco de su propio chat (0x00)", echoName == char1 && echoMsg == chatMsg, $"(name={echoName}, msg='{echoMsg}')");

    var chatInB = await clientB.WaitForAsync(0x00, TimeSpan.FromSeconds(5));
    string chatInBName = System.Text.Encoding.ASCII.GetString(chatInB.Full, 3, 10).TrimEnd('\0');
    string chatInBMsg = System.Text.Encoding.ASCII.GetString(chatInB.Full, 13, 60).TrimEnd('\0');
    Check("[B] Recibió el chat público de A por viewport (0x00)", chatInBName == char1 && chatInBMsg == chatMsg, $"(name={chatInBName}, msg='{chatInBMsg}')");

    const string whisperMsg = "secreto entre nosotros";
    await clientA.SendWhisperAsync(char2, whisperMsg);

    var whisperInB = await clientB.WaitForAsync(0x02, TimeSpan.FromSeconds(5));
    string whisperSource = System.Text.Encoding.ASCII.GetString(whisperInB.Full, 3, 10).TrimEnd('\0');
    string whisperInBMsg = System.Text.Encoding.ASCII.GetString(whisperInB.Full, 13, 60).TrimEnd('\0');
    Check("[B] Recibió el whisper de A (local, 0x02)", whisperSource == char1 && whisperInBMsg == whisperMsg, $"(source={whisperSource}, msg='{whisperInBMsg}')");

    Console.WriteLine("\n=== Fase 5: party (invitar/aceptar/lista/chat de grupo/reparto de exp/salir) ===");

    // Real indices assigned by the server -- extracted from the viewport appearance packets (0x12) already
    // received above: PMSG_VIEWPORT_SEND (without sub-code) has a 4-byte C2 header + count(1) + the first entry
    // starts with indexHi/indexLo.
    int bIndexSeenByA = (appearInA.Full[5] << 8) | appearInA.Full[6];
    int aIndexSeenByB = (appearInB.Full[5] << 8) | appearInB.Full[6];

    await clientA.SendPartyRequestAsync(bIndexSeenByA);
    var partyRequestInB = await clientB.WaitForAsync(0x40, TimeSpan.FromSeconds(5));
    int inviterIndexInB = (partyRequestInB.Full[3] << 8) | partyRequestInB.Full[4];
    Check("[B] Recibió la invitación de party de A (0x40)", inviterIndexInB == aIndexSeenByB, $"(inviter={inviterIndexInB})");

    await clientB.SendPartyRequestResultAsync(1, inviterIndexInB);

    var partyListInA = await clientA.WaitForAsync(0x42, TimeSpan.FromSeconds(5));
    byte partyListResultA = partyListInA.Full[3];
    byte partyListCountA = partyListInA.Full[4];
    Check("[A] Recibió PARTY_LIST_SEND tras la aceptación (2 miembros)", partyListResultA == 1 && partyListCountA == 2, $"(result={partyListResultA}, count={partyListCountA})");

    var partyListInB = await clientB.WaitForAsync(0x42, TimeSpan.FromSeconds(5));
    byte partyListCountB = partyListInB.Full[4];
    Check("[B] Recibió PARTY_LIST_SEND tras la aceptación (2 miembros)", partyListCountB == 2, $"(count={partyListCountB})");

    // Layout regression: PMSG_PARTY_LIST is sizeof()=24, not 22 -- it carries 2 padding bytes after y (offset
    // 14) to align the DWORD CurLife. C1 header (3) + result (1) + count (1) + N*24.
    const int partyMemberStride = 24;
    int partyBody = partyListInA.Full.Length - 5;
    Check("[A] PMSG_PARTY_LIST respeta el stride real de 24 bytes",
        partyBody == partyListCountA * partyMemberStride,
        $"(cuerpo={partyBody}, esperado={partyListCountA * partyMemberStride})");

    const string partyMsg = "~vamos equipo";
    await clientA.SendChatAsync(char1, partyMsg);

    var partyChatInA = await clientA.WaitForAsync(0x00, TimeSpan.FromSeconds(5));
    string partyChatInAMsg = System.Text.Encoding.ASCII.GetString(partyChatInA.Full, 13, 60).TrimEnd('\0');
    Check("[A] Recibió su propio chat de grupo (eco, incluye '~')", partyChatInAMsg == partyMsg, $"(msg='{partyChatInAMsg}')");

    var partyChatInB = await clientB.WaitForAsync(0x00, TimeSpan.FromSeconds(5));
    string partyChatInBMsg = System.Text.Encoding.ASCII.GetString(partyChatInB.Full, 13, 60).TrimEnd('\0');
    Check("[B] Recibió el chat de grupo de A (sin importar viewport)", partyChatInBMsg == partyMsg, $"(msg='{partyChatInBMsg}')");

    Console.WriteLine("\n=== Fase 5: A remata al monstruo (ya revivido) con B en el grupo -- reparto de experiencia ===");

    bool partyMonsterDied = false;

    for (int i = 0; i < 40 && !partyMonsterDied; i++)
    {
        await clientA.SendAttackAsync(monsterIndex, 120, 3);
        await clientA.WaitForAsync(0xD9, TimeSpan.FromSeconds(5));

        try
        {
            // Same care as in the phase 4 loop: the 0x17 also announces the death of a PLAYER, so it has to be
            // confirmed that the index that died is the monster's before considering it dead.
            var diePkt = await clientA.WaitForAsync(0x17, TimeSpan.FromMilliseconds(300));
            int deadIndex = (diePkt.Full[3] << 8) | diePkt.Full[4];

            if (deadIndex == monsterIndex)
            {
                partyMonsterDied = true;
            }
            else
            {
                Console.WriteLine($"[A] 0x17 recibido pero murió el índice {deadIndex}, no el monstruo {monsterIndex}");
            }
        }
        catch (TimeoutException)
        {
        }
    }

    Check("[A] Remató al monstruo con el grupo activo", partyMonsterDied);

    var expPartyA = await clientA.WaitForAsync(0x9C, TimeSpan.FromSeconds(5));
    Check("[A] Recibió su parte de experiencia de grupo (0x9C)", true, $"({expPartyA.Full.Length} bytes)");

    var expPartyB = await clientB.WaitForAsync(0x9C, TimeSpan.FromSeconds(5));
    Check("[B] Recibió su parte de experiencia de grupo aunque no atacó (0x9C, reparto por nivel en rango)", true, $"({expPartyB.Full.Length} bytes)");

    Console.WriteLine("\n=== Fase 5: B sale del grupo -- se disuelve (quedaban 2) y A recibe PARTY_DEL_MEMBER_SEND ===");

    await clientB.SendPartyDelMemberAsync(1); // B is slot 1 (A, the leader who invited, stayed in slot 0)

    var delMemberInB = await clientB.WaitForAsync(0x43, TimeSpan.FromSeconds(5));
    Check("[B] Recibió PARTY_DEL_MEMBER_SEND al salir", delMemberInB.Full.Length >= 3);

    var delMemberInA = await clientA.WaitForAsync(0x43, TimeSpan.FromSeconds(5));
    Check("[A] También recibió PARTY_DEL_MEMBER_SEND (el grupo se disolvió al quedar en 1 miembro)", delMemberInA.Full.Length >= 3);

    Console.WriteLine("\n=== Fase 5: amigos (pedir/aceptar/listar/borrar) ===");

    await clientA.SendFriendRequestAsync(char2);

    var friendRequestInB = await clientB.WaitForAsync(0xC1, TimeSpan.FromSeconds(5));
    string friendRequesterInB = System.Text.Encoding.ASCII.GetString(friendRequestInB.Full, 4, 10).TrimEnd('\0');
    Check("[B] Recibió la solicitud de amistad de A (0xC1)", friendRequesterInB == char1, $"(de='{friendRequesterInB}')");

    await clientB.SendFriendResultAsync(1, char1);

    var friendResultInB = await clientB.WaitForAsync(0xC2, TimeSpan.FromSeconds(5));
    string friendResultInBName = System.Text.Encoding.ASCII.GetString(friendResultInB.Full, 3, 10).TrimEnd('\0');
    Check("[B] Confirmó amistad con A (0xC2)", friendResultInBName == char1, $"(name='{friendResultInBName}')");

    var friendResultInA = await clientA.WaitForAsync(0xC2, TimeSpan.FromSeconds(5));
    string friendResultInAName = System.Text.Encoding.ASCII.GetString(friendResultInA.Full, 3, 10).TrimEnd('\0');
    Check("[A] Recibió confirmación simétrica de que B aceptó (0xC2)", friendResultInAName == char2, $"(name='{friendResultInAName}')");

    await clientA.SendFriendListRequestAsync();

    var friendListInA = await clientA.WaitForAsync(0xC0, TimeSpan.FromSeconds(5));
    byte friendCountA = friendListInA.Full[5];
    string friendListName = friendCountA > 0 ? System.Text.Encoding.ASCII.GetString(friendListInA.Full, 6, 10).TrimEnd('\0') : "";
    byte friendListServer = friendCountA > 0 ? friendListInA.Full[16] : (byte)0xFF;
    Check("[A] FRIEND_LIST_SEND incluye a B, online (server != 0xFF)", friendCountA == 1 && friendListName == char2 && friendListServer != 0xFF,
        $"(count={friendCountA}, name='{friendListName}', server={friendListServer})");

    await clientA.SendFriendDeleteAsync(char2);

    var friendDeleteInA = await clientA.WaitForAsync(0xC3, TimeSpan.FromSeconds(5));
    byte friendDeleteResult = friendDeleteInA.Full[3];
    Check("[A] Borró a B de la lista de amigos (0xC3)", friendDeleteResult == 1, $"(result={friendDeleteResult})");

    if (account3 != null && password3 != null && newCharName != null)
    {
        Console.WriteLine("\n=== Crear personaje vía GameServer (cuenta nueva, 0 personajes) ===");

        var clientC = new FakeMuClient("C", serial, version, enc1, dec2);
        await clientC.ConnectAsync(host, port, cts.Token);
        await clientC.WaitForAsync(0xF1, TimeSpan.FromSeconds(5), 0x00);

        await clientC.SendLoginAsync(account3, password3);
        var loginC = await clientC.WaitForAsync(0xF1, TimeSpan.FromSeconds(5), 0x01);
        Check("[C] Login de cuenta nueva", loginC.Full[4] == 1, $"(resultado={loginC.Full[4]})");

        await clientC.SendCharacterListRequestAsync();
        var emptyListC = await clientC.WaitForAsync(0xF3, TimeSpan.FromSeconds(5), 0x00);
        byte emptyCount = emptyListC.Full[6];
        Check("[C] Cuenta nueva arranca con 0 personajes", emptyCount == 0, $"(count={emptyCount})");

        await clientC.SendCharacterCreateAsync(newCharName, 0); // clase 0 = DW
        var createResult = await clientC.WaitForAsync(0xF3, TimeSpan.FromSeconds(5), 0x01);
        byte createResultCode = createResult.Full[4];
        string createdName = System.Text.Encoding.ASCII.GetString(createResult.Full, 5, 10).TrimEnd('\0');
        Check("[C] CHARACTER_CREATE_SEND result=1", createResultCode == 1, $"(result={createResultCode}, name='{createdName}')");

        await clientC.SendCharacterListRequestAsync();
        var listAfterCreate = await clientC.WaitForAsync(0xF3, TimeSpan.FromSeconds(5), 0x00);
        byte countAfterCreate = listAfterCreate.Full[6];
        Check("[C] La lista ahora incluye el personaje recién creado", countAfterCreate == 1, $"(count={countAfterCreate})");

        await clientC.SendCharacterSelectAsync(newCharName);
        var infoC = await clientC.WaitForAsync(0xF3, TimeSpan.FromSeconds(5), 0x03);
        Check("[C] Entró al mundo con el personaje recién creado (CHARACTER_INFO_SEND)", true,
            $"(Map={infoC.Full[6]} X={infoC.Full[4]} Y={infoC.Full[5]})");

        clientC.Close();
    }

    if (dsEnabled)
    {
        Console.WriteLine("\n=== Fase 6: Devil Square (entrada, spawn de etapa 0, puntaje, cierre) ===");

        var (clientD, _, _) = await LoginAndEnterAsync("D", account4!, password4!, char4!, cts.Token);

        await clientD.SendDevilSquareEnterAsync(0, 13); // bracket 0, slot 13 = ticket sembrado
        var enterResult = await clientD.WaitForAsync(0x90, TimeSpan.FromSeconds(20));
        byte enterResultCode = enterResult.Full[3];
        Check("[D] DEVIL_SQUARE_ENTER_SEND result=0 (entró)", enterResultCode == 0, $"(result={enterResultCode})");

        var teleport = await clientD.WaitForAsync(0x1C, TimeSpan.FromSeconds(5));
        // PMSG_TELEPORT_SEND real: gate(1)+map(1)+x(1)+y(1)+dir(1) -- gate es BYTE, no WORD (ver
        // WorldPacketBuilder.TeleportSend). Full[3]=gate, Full[4]=map, Full[5]=x, Full[6]=y.
        byte dsMap = teleport.Full[4];
        byte dsX = teleport.Full[5];
        byte dsY = teleport.Full[6];
        Check("[D] Recibió TELEPORT_SEND a Devil Square (mapa 9)", dsMap == 9, $"(Map={dsMap} X={dsX} Y={dsY})");

        Console.WriteLine("\n=== Fase 6: esperando STAND->START real y la aparición de un monstruo de evento (0x13) ===");
        // The entry window opens quite a while before STAND starts (see 'ds forcestart' in the test script) --
        // from here almost the whole real duration of STAND (NotifyMinutes) may remain before START only spawns
        // stage 0.
        var monsterAppear = await clientD.WaitForAsync(0x13, TimeSpan.FromSeconds(100));
        int dsMonsterIndex = ((monsterAppear.Full[5] & 0x7F) << 8) | monsterAppear.Full[6];
        byte monX = monsterAppear.Full[11];
        byte monY = monsterAppear.Full[12];
        Check("[D] Vio aparecer un monstruo de Devil Square", true, $"(index={dsMonsterIndex}, X={monX} Y={monY})");

        // Acercarse a distancia de ataque (rango 3) -- un solo salto (el protocolo acepta hasta 15
        // tiles de diferencia por paquete de movimiento, ver OnMoveAsync/withinRange).
        int dx = monX > dsX ? -1 : (monX < dsX ? 1 : 0);
        int dy = monY > dsY ? -1 : (monY < dsY ? 1 : 0);
        byte approachX = (byte)Math.Clamp(monX + (dx * 2), 0, 255);
        byte approachY = (byte)Math.Clamp(monY + (dy * 2), 0, 255);
        await clientD.SendMoveAsync(approachX, approachY, 0);
        await clientD.WaitForAsync(0xD7, TimeSpan.FromSeconds(5));
        Check("[D] Se acercó al monstruo de evento", true, $"(a X={approachX} Y={approachY})");

        bool dsMonsterDied = false;
        int dsHits = 0;

        for (int i = 0; i < 40 && !dsMonsterDied; i++)
        {
            await clientD.SendAttackAsync(dsMonsterIndex, 120, 3);
            await clientD.WaitForAsync(0xD9, TimeSpan.FromSeconds(5));
            dsHits++;

            try
            {
                await clientD.WaitForAsync(0x17, TimeSpan.FromMilliseconds(300));
                dsMonsterDied = true;
            }
            catch (TimeoutException)
            {
            }
        }

        Check("[D] Mató a un monstruo de Devil Square", dsMonsterDied, $"(en {dsHits} golpe(s))");

        Console.WriteLine("\n=== Fase 6: esperando el cierre del evento (STAND+START reales, hasta ~2 min) ===");
        var scorePkt = await clientD.WaitForAsync(0x93, TimeSpan.FromSeconds(150));
        byte finalRank = scorePkt.Full[3];
        byte entryCount = scorePkt.Full[4];
        string firstEntryName = System.Text.Encoding.ASCII.GetString(scorePkt.Full, 5, 10).TrimEnd('\0');
        uint firstEntryScore = (uint)(scorePkt.Full[15] | (scorePkt.Full[16] << 8) | (scorePkt.Full[17] << 16) | (scorePkt.Full[18] << 24));
        Check("[D] Recibió DEVIL_SQUARE_SCORE_SEND al cerrar el evento", entryCount >= 1 && firstEntryName == char4,
            $"(rank={finalRank}, count={entryCount}, name='{firstEntryName}')");
        Check("[D] Su puntaje de evento es mayor a 0 (mató al menos un monstruo)", firstEntryScore > 0, $"(score={firstEntryScore})");

        clientD.Close();
    }

    clientA.Close();
    clientB.Close();
}
catch (Exception ex)
{
    Console.WriteLine($"[FALLO] Excepción: {ex}");
    failures++;
}

Console.WriteLine($"\n=== {(failures == 0 ? "TODO OK" : $"{failures} FALLO(S)")} ===");
return failures == 0 ? 0 : 1;
