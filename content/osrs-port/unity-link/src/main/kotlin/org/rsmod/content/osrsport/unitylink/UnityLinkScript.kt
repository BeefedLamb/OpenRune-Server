package org.rsmod.content.osrsport.unitylink

import com.github.michaelbull.logging.InlineLogger
import dev.openrune.ServerCacheManager
import dev.openrune.rscm.RSCM
import dev.openrune.rscm.RSCMType
import dev.openrune.types.aconverted.interf.IfButtonOp
import jakarta.inject.Inject
import kotlin.math.abs
import kotlin.math.max
import net.rsprot.protocol.game.outgoing.zone.payload.LocAddChangeV2
import net.rsprot.protocol.game.outgoing.zone.payload.LocDel
import net.rsprot.protocol.game.outgoing.zone.payload.SoundArea
import org.rsmod.api.game.process.GameLifecycle
import org.rsmod.api.player.events.FarmingPatchEvent
import org.rsmod.api.player.events.HouseLayoutEvent
import org.rsmod.api.player.events.MinigameOverlayEvent
import org.rsmod.api.player.events.PlayerHitEvents
import org.rsmod.api.player.interact.HeldInteractions
import org.rsmod.api.player.interact.LocInteractions
import org.rsmod.api.player.interact.NpcInteractions
import org.rsmod.api.player.interact.ObjInteractions
import org.rsmod.api.player.interact.WornInteractions
import org.rsmod.api.player.output.mes
import org.rsmod.api.player.protect.ProtectedAccessLauncher
import org.rsmod.api.player.protect.clearPendingAction
import org.rsmod.api.player.stat.baseHitpointsLvl
import org.rsmod.api.player.stat.stat
import org.rsmod.api.player.stat.statBase
import org.rsmod.api.player.stat.hitpoints
import org.rsmod.api.player.lefthand
import org.rsmod.api.player.righthand
import org.rsmod.api.player.vars.enumVarp
import org.rsmod.api.player.vars.intVarp
import org.rsmod.api.registry.loc.LocRegistry
import org.rsmod.api.registry.zone.ZoneUpdateMap
import org.rsmod.api.repo.npc.NpcRepository
import org.rsmod.api.repo.obj.ObjRepository
import org.rsmod.api.script.onCommand
import org.rsmod.api.script.onEvent
import org.rsmod.api.specials.SpecialAttack
import org.rsmod.api.specials.SpecialAttackRegistry
import org.rsmod.api.specials.SpecialAttackType
import org.rsmod.events.EventBus
import org.rsmod.game.cheat.CheatCommandMap
import org.rsmod.game.entity.NpcList
import org.rsmod.game.entity.PlayerList
import org.rsmod.game.entity.Player
import org.rsmod.game.interact.InteractionLoc
import org.rsmod.game.interact.InteractionLocOp
import org.rsmod.game.interact.InteractionNpcOp
import org.rsmod.game.interact.InteractionOp
import org.rsmod.game.loc.BoundLocInfo
import org.rsmod.game.loc.LocInfo
import org.rsmod.game.map.collision.isZoneValid
import org.rsmod.game.hit.HitType
import org.rsmod.game.interact.HeldOp
import org.rsmod.game.inv.Inventory
import org.rsmod.game.movement.MoveSpeed
import org.rsmod.game.seq.EntitySeq
import org.rsmod.map.CoordGrid
import org.rsmod.map.zone.ZoneKey
import org.rsmod.plugin.scripts.PluginScript
import org.rsmod.plugin.scripts.ScriptContext
import org.rsmod.routefinder.collision.CollisionFlagMap

/**
 * Lets the Unity client play as a normal server-side player without the OSRS game protocol.
 *
 * Client -> server: `HELLO name`, `POS x z level`, `OPLOC locId x z level op`, `OPNPC index op`,
 * `STOP` (drop the current interaction), `SPEC` (arm the weapon special for the next attack),
 * `BLOCK 1|0` (shield block held/released), `DODGE` (one tick of invulnerability), `CHAT text`
 * (`::` prefix runs a server command), `DLGC` (click-to-continue), `DLGS n` (pick dialogue option
 * n), `OPHELD slot op` / `OPWORN slot op` (item options, op 10 = examine), `BYE`.
 * Dialogues arrive as `DLG ...` lines (see [LinkClient]); the backpack, equipment and skills as
 * `INV slot|id|count|name|op1/../op5;...`, `WORN ...` (same shape) and `STATS id|name|level|base|xp;...`.
 * Server -> client: `WELCOME x z level`, `TILE x z level`, `MSG type text`, `ME hp maxHp spec`,
 * `HIT me 0 damage` (a hit on the link player), `HIT npc index damage mine` (a player's hit on a
 * nearby npc, 0 = miss/block; mine is 1 when this link player dealt it), `NDIE index` (that hit
 * killed the npc), `NPCS n idx,id,x,z,level,hp,maxHp;...`,
 * `PLAYERS slot|name|x|z|level|hp|maxHp|anim;...` (other players, i.e. bots), `ANIM seq` (player
 * animation), `BAS key ready turn walkF walkB walkL walkR run` (base stand/walk/run seqs of the
 * link player `me` or another player's slot, on change), `NANIM index seq` (npc animation), `LOC add id x z level shape rot` /
 * `LOC del x z level shape rot`, `SND synth loops delay` (sound for the link player),
 * `SNDA synth x z level loops delay range` (area sound), `ERROR text`.
 *
 * Ground items: `GITEMS id|count|x|z|level|name;...` is the full list of ground items the link
 * player can see (own private drops/loot plus public ones) on its floor within GROUND_ZONE_RADIUS
 * zones; it is re-sent only when it changes. The client picks one up with `TAKE id x z level`,
 * which runs the obj's Take op (the normal ObjTakePlugin path) from up to one tile away.
 *
 * Bank: use a bank booth (OPLOC, op "Bank") or a banker (OPNPC, op "Bank") to open the real
 * server bank. The server then sends `BANKOPEN`, `BANKNOTE 0|1` and `BANK slot|id|count|name|ops;...`
 * (re-sent on change) until `BANKCLOSE`. The client sends `WITHDRAW slot id count` /
 * `DEPOSIT slot id count` (bank slot / backpack slot; count 1, 5, 10, any positive X, or -1 = all),
 * `DEPOSITALL`, `DEPOSITWORN`, `BANKNOTE 0|1` and `BANKCLOSE`. See [LinkBank].
 *
 * Shops: a shopkeeper's "Trade" op opens the real server shop. The server sends `SHOPOPEN`,
 * `SHOP currency;slot|id|count|name|price;...` (re-sent on change) until `SHOPCLOSE`. The client
 * sends `BUY slot id count` / `SELL slot id count` (count 1, 5, 10, 50), `SHOPVALUE 0|1 slot id`
 * and `SHOPCLOSE`. See [LinkShop].
 *
 * Spellbook: `SPELLS book;objId|name|level|type|target|levelOk|runesOk|runes;...` lists the current
 * spellbook (re-sent on change); the client casts with `CAST objId` or `CAST objId slot` (spell on a
 * backpack item). Casts replay the real spellbook interface events. See [LinkSpells].
 *
 * Minigames: `MGOVERLAY title;key|value;...` is the live status of the minigame the player is
 * working in (Wintertodt, Tempoross, Giants' Foundry, GotR), pushed by those scripts through
 * [MinigameOverlayEvent]; a bare `MGOVERLAY` hides it.
 *
 * Farming patches: `PATCH x z level variantLocId baseLocId value shape rot` (per patch, on change).
 * Player-owned house: `HOUSE level;dstX|dstZ|srcX|srcZ|rot|name;...` (bare `HOUSE` = left) and
 * `HLOCS id|x|z|level|shape|rot;...` (all house locs). See [LinkGeometry].
 *
 * Stuns/freezes: `FROZEN 1` when the link player becomes frozen (e.g. a failed pickpocket stun,
 * or a bind spell) and `FROZEN 0` when it wears off. While frozen the server ignores `POS` moves
 * and re-sends `TILE` so the client snaps back.
 *
 * Character Design (OSRS Tutorial Island style): the login name from `HELLO` becomes the display
 * name. Right after `WELCOME`, an account without a saved look gets `KITS slot aIds bIds` (x7,
 * slots hair jaw torso arms hands legs feet; comma-separated legal identkit ids for body type A
 * and B, `-` if none), `LOOK ...` (current/default look) and `CREATE displayName`. The client
 * shows its design screen and answers `APPEAR bodyType hair jaw torso arms hands legs feet
 * colHair colTorso colLegs colFeet colSkin` (13 ints; bodyType 0 = A/male, 1 = B/female; colours
 * 0..24, 0..28, 0..28, 0..5, 0..13). The server sanitises it, saves it and replies `LOOK ...` then
 * `CREATED`. Accounts that already have a saved look just get `LOOK ...` after `WELCOME`, and
 * `APPEAR` is ignored for them.
 *
 * Player bodies: `PLOOK key bodyType colHair colTorso colLegs colFeet colSkin s0 .. s11` gives the
 * resolved 12-slot appearance of the link player (key `me`) or of another player (key = its
 * `PLAYERS` slot), sent before/with `PLAYERS` and re-sent only when it changes. Slot values: -1
 * nothing, 256 + identkit id, 512 + item id. See [LinkAppearance].
 *
 * The Unity client owns the player's position: the server never walks the link player, it only
 * teleports it where Unity says it stands. Interactions therefore work by range checks, not by
 * walking to the target.
 */
class UnityLinkScript
@Inject
constructor(
    private val headlessLogin: HeadlessLogin,
    private val eventBus: EventBus,
    private val collision: CollisionFlagMap,
    private val locRegistry: LocRegistry,
    private val locInteractions: LocInteractions,
    private val npcInteractions: NpcInteractions,
    private val npcList: NpcList,
    private val playerList: PlayerList,
    private val npcRepo: NpcRepository,
    private val zoneUpdates: ZoneUpdateMap,
    private val specialsReg: SpecialAttackRegistry,
    private val cheats: CheatCommandMap,
    private val heldInteractions: HeldInteractions,
    private val wornInteractions: WornInteractions,
    private val protectedAccess: ProtectedAccessLauncher,
    private val objRepo: ObjRepository,
    private val objInteractions: ObjInteractions,
    private val locTInteractions: org.rsmod.api.player.interact.LocTInteractions,
    private val spellRegistry: org.rsmod.api.spells.MagicSpellRegistry,
    private val runeManager: org.rsmod.api.combat.manager.MagicRuneManager,
) : PluginScript() {
    private val server = UnityLinkServer(LINK_PORT)
    private val connections = LinkedHashSet<LinkConnection>()
    private val bank = LinkBank(eventBus, protectedAccess)
    private val shop = LinkShop(eventBus, protectedAccess)
    private val geometry = LinkGeometry(locRegistry)
    private val spells by lazy { LinkSpells(eventBus, protectedAccess, spellRegistry, runeManager) }
    private var currentTick = 0

    override fun ScriptContext.startup() {
        onEvent<GameLifecycle.Startup> {
            try {
                server.start()
            } catch (e: Exception) {
                logger.error(e) { "Unity link could not open port $LINK_PORT" }
            }
        }
        onEvent<GameLifecycle.Shutdown> { server.stop() }
        onEvent<GameLifecycle.StartCycle> {
            currentTick++
            drainInbox()
        }
        onEvent<GameLifecycle.LateCycle> { syncClients() }
        onEvent<PlayerHitEvents.Impact> {
            connectionOf(player)?.send("HIT me 0 ${hit.damage}")
        }
        onEvent<LinkNpcHit> { npcHit(this) }
        onEvent<MinigameOverlayEvent> {
            val connection = connectionOf(player) ?: return@onEvent
            val line = overlayLine(this)
            if (line != connection.lastOverlayLine) {
                connection.lastOverlayLine = line
                connection.send(line)
            }
        }
        onEvent<FarmingPatchEvent> { connectionOf(player)?.let { geometry.patches(it, this) } }
        onEvent<HouseLayoutEvent> { connectionOf(player)?.let { geometry.house(it, this) } }
        onEvent<HeadlessChat> {
            for (connection in connections) {
                connection.send("MSG 2 ${player.displayName}: $text")
            }
        }
        onEvent<PlayerHitEvents.Modify> {
            val connection = connectionOf(player) ?: return@onEvent
            if (currentTick < connection.dodgeUntilTick) {
                hit.damage = 0
            } else if (connection.blocking && player.lefthand != null && hit.type != HitType.Magic) {
                hit.damage = hit.damage * (100 - BLOCK_REDUCTION_PERCENT) / 100
            }
        }

        onCommand("where") {
            desc = "Lists players connected from the Unity client and their tiles."
            cheat {
                val linked = connections.mapNotNull { it.player }
                if (linked.isEmpty()) {
                    player.mes("No Unity players connected.")
                } else {
                    for (p in linked) {
                        val c = p.coords
                        player.mes("${p.displayName}: ${c.x}, ${c.z}, level ${c.level}")
                    }
                }
            }
        }
    }

    override fun ScriptContext.shutdown() {
        server.stop()
    }

    /** Tells every link player who can see the npc about the hit, and about the kill. */
    private fun npcHit(event: LinkNpcHit) {
        val npc = event.npc
        val dead = npc.hitpoints <= 0
        for (connection in connections) {
            val player = connection.player ?: continue
            if (player.coords.level != npc.coords.level || chebyshev(player.coords, npc.coords) > PLAYER_VIEW_RANGE) {
                continue
            }
            val mine = if (event.source === player) 1 else 0
            connection.send("HIT npc ${npc.slotId} ${event.damage} $mine")
            if (dead) {
                connection.send("NDIE ${npc.slotId}")
            }
        }
    }

    /** `MGOVERLAY title;key|value;...`, or a bare `MGOVERLAY` to hide the minigame HUD. */
    private fun overlayLine(event: MinigameOverlayEvent): String {
        if (event.title.isEmpty()) return "MGOVERLAY"
        fun clean(s: String) = s.replace('|', '/').replace(';', ',').replace('\n', ' ')
        val sb = StringBuilder("MGOVERLAY ").append(clean(event.title))
        for ((k, v) in event.values) sb.append(';').append(clean(k)).append('|').append(clean(v))
        return sb.toString()
    }

    private fun connectionOf(player: Player): LinkConnection? =
        connections.firstOrNull { it.player === player }

    private fun drainInbox() {
        while (true) {
            val command = server.inbox.poll() ?: break
            try {
                handle(command)
            } catch (e: Exception) {
                logger.error(e) { "Unity link command failed: $command" }
            }
        }
    }

    private fun handle(command: LinkCommand) {
        val connection = command.connection
        when (command) {
            is LinkCommand.Hello -> beginLogin(connection, command.name)
            is LinkCommand.Position -> move(connection, command.x, command.z, command.level)
            is LinkCommand.OpLoc -> opLoc(connection, command)
            is LinkCommand.OpLocU -> opLocU(connection, command)
            is LinkCommand.OpNpc -> opNpc(connection, command)
            is LinkCommand.Appear -> appear(connection, command.values)
            is LinkCommand.Stop -> stop(connection)
            is LinkCommand.Chat -> chat(connection, command.text)
            is LinkCommand.Special -> armSpecial(connection)
            is LinkCommand.Block -> connection.blocking = command.blocking
            is LinkCommand.Dodge -> connection.dodgeUntilTick = currentTick + DODGE_TICKS
            is LinkCommand.OpItem -> opItem(connection, command)
            is LinkCommand.Take -> take(connection, command)
            is LinkCommand.BankItem -> bankItem(connection, command)
            is LinkCommand.BankDepositAll -> connection.player?.let { bank.depositAll(connection, it, command.worn) }
            is LinkCommand.BankNote -> connection.player?.let { bank.setNoteMode(connection, it, command.note) }
            is LinkCommand.BankClose -> connection.player?.let { bank.close(it) }
            is LinkCommand.ShopItem -> connection.player?.let {
                if (command.sell) shop.sell(connection, it, command.slot, command.id, command.count)
                else shop.buy(connection, it, command.slot, command.id, command.count)
            }
            is LinkCommand.ShopValue -> connection.player?.let { shop.value(connection, it, command.side, command.slot, command.id) }
            is LinkCommand.ShopClose -> connection.player?.let { shop.close(it) }
            is LinkCommand.Cast -> connection.player?.let { spells.cast(connection, it, command.spellObj, command.slot) }
            is LinkCommand.DialogueContinue -> resumeDialogue(connection, -1)
            is LinkCommand.DialogueSelect -> resumeDialogue(connection, command.option)
            is LinkCommand.Bye -> {
                connection.player?.forceDisconnect = true
                connection.close()
            }
            is LinkCommand.Disconnected -> {
                connections.remove(connection)
                connection.player?.clientDisconnected?.set(true)
            }
        }
    }

    // ------------------------------------------------------------------ login

    private fun beginLogin(connection: LinkConnection, name: String) {
        if (connection.player != null || connection.loggingIn) {
            return
        }
        connection.loggingIn = true
        connections += connection
        val queued =
            headlessLogin.login(
                name = name,
                client = LinkClient(connection),
                admin = true,
                failed = { reason ->
                    connection.loggingIn = false
                    connection.send("ERROR $reason")
                },
                loggedIn = { player -> finishLogin(connection, player) },
            )
        if (!queued) {
            connection.loggingIn = false
            connection.send("ERROR login service busy, try again")
        }
    }

    /** Runs on the game thread once the player is registered in the world. */
    private fun finishLogin(connection: LinkConnection, player: Player) {
        connection.loggingIn = false
        if (connection.isClosed) {
            player.forceDisconnect = true
            return
        }
        connection.player = player
        connection.clientTile = player.coords
        connection.lastSentTile = player.coords
        val c = player.coords
        connection.send("WELCOME ${c.x} ${c.z} ${c.level}")
        connection.send("ME ${player.hitpoints} ${player.baseHitpointsLvl} ${player.specialEnergy}")
        if (CharacterDesign.restore(player)) {
            connection.send(CharacterDesign.lookLine(player))
        } else {
            CharacterDesign.kitLines().forEach(connection::send)
            connection.send(CharacterDesign.lookLine(player))
            connection.send("CREATE ${player.displayName}")
        }
        logger.info { "Unity link: ${player.username} logged in at $c (slot ${player.slotId})" }
    }

    /**
     * Character Design: only accepted while the account has no saved look (a new account, or one
     * from before looks were saved). Afterwards the look is fixed and APPEAR is ignored.
     */
    private fun appear(connection: LinkConnection, values: List<Int>) {
        val player = connection.player ?: return
        if (CharacterDesign.hasDesigned(player)) {
            connection.send("MSG 0 Your appearance is already set.")
            return
        }
        val look = CharacterDesign.sanitise(player, values) ?: return
        CharacterDesign.apply(player, look)
        CharacterDesign.save(player, look)
        connection.send(CharacterDesign.lookLine(player))
        connection.send("CREATED")
        logger.info { "Unity link: ${player.username} designed character $look" }
    }

    // ------------------------------------------------------------------ movement

    private fun move(connection: LinkConnection, x: Int, z: Int, level: Int) {
        val player = connection.player ?: return
        if (player.loggingOut || level !in 0..3) {
            return
        }
        val dest = CoordGrid(x, z, level)
        if (player.isFrozen && dest != player.coords) {
            // Stunned/bound: refuse the move and snap the client back to where the server holds it.
            connection.clientTile = player.coords
            connection.lastSentTile = null
            return
        }
        if (player.isDelayed && dest != player.coords) {
            // Mid-script (agility obstacle, climb, ...): the server owns the position until the
            // delay ends. Refuse the move and make Unity snap to the server tile.
            connection.clientTile = player.coords
            forceMove(connection, player.coords)
            return
        }
        connection.clientTile = dest
        if (player.coords == dest || !collision.isZoneValid(dest)) {
            return
        }
        val interaction = player.interaction
        val keepInteraction =
            interaction != null &&
                (interaction !is InteractionLoc || chebyshev(dest, interaction.target.coords) <= 2)
        place(player, dest, keepInteraction)
    }

    /** Moves the player instantly, like a telejump, but optionally keeps its interaction. */
    private fun place(player: Player, dest: CoordGrid, keepInteraction: Boolean) {
        val start = player.coords
        player.coords = dest
        player.lastProcessedCoord = CoordGrid.NULL
        player.abortRoute()
        if (!keepInteraction) {
            player.clearInteraction()
        }
        if (collision.isZoneValid(start)) {
            player.removeBlockWalkCollision(collision, start)
        }
        player.addBlockWalkCollision(collision, dest)
        player.pendingTelejump = true
        player.moveSpeed = MoveSpeed.Stationary
    }

    // ------------------------------------------------------------------ interactions

    private fun opLoc(connection: LinkConnection, command: LinkCommand.OpLoc) {
        val player = connection.player ?: return
        if (player.isDelayed) {
            return
        }
        val op = interactionOp(command.op) ?: return
        val coords = CoordGrid(command.x, command.z, command.level)
        val loc = locRegistry.findType(coords, command.locId)
        if (loc == null) {
            connection.send("MSG 0 Nothing interesting happens.")
            return
        }
        val type = ServerCacheManager.getObject(command.locId) ?: return
        val bound = BoundLocInfo(loc, type)
        if (!locInteractions.hasOp(bound, type, player.vars, op)) {
            return
        }
        if (distanceToLoc(player.coords, bound) > OP_RANGE) {
            connection.send("MSG 0 You need to get closer.")
            return
        }
        val interaction =
            InteractionLocOp(
                target = bound,
                op = op,
                hasOpTrigger = locInteractions.hasOpTrigger(player, bound, op, type),
                hasApTrigger = locInteractions.hasApTrigger(player, bound, op, type),
            )
        player.clearPendingAction(eventBus)
        player.resetFaceEntity()
        player.faceLoc(loc, type.width, type.length)
        player.interaction = interaction
    }

    /** Use the item in backpack slot [LinkCommand.OpLocU.slot] on a loc (seeds on patches, etc.). */
    private fun opLocU(connection: LinkConnection, command: LinkCommand.OpLocU) {
        val player = connection.player ?: return
        if (player.isDelayed) {
            return
        }
        val obj = player.inv[command.slot] ?: return
        val objType = ServerCacheManager.getItem(obj.id) ?: return
        val coords = CoordGrid(command.x, command.z, command.level)
        val loc = locRegistry.findType(coords, command.locId)
        if (loc == null) {
            connection.send("MSG 0 Nothing interesting happens.")
            return
        }
        val type = ServerCacheManager.getObject(command.locId) ?: return
        val bound = BoundLocInfo(loc, type)
        if (distanceToLoc(player.coords, bound) > OP_RANGE) {
            connection.send("MSG 0 You need to get closer.")
            return
        }
        val component =
            ServerCacheManager.fromComponent("component.inventory:items")
        val interaction =
            org.rsmod.game.interact.InteractionLocT(
                objType = objType,
                component = component,
                comsub = command.slot,
                target = bound,
                hasOpTrigger = locTInteractions.hasOpTrigger(player, bound, type, objType, component, command.slot),
                hasApTrigger = locTInteractions.hasApTrigger(player, bound, type, objType, component, command.slot),
            )
        player.clearPendingAction(eventBus)
        player.resetFaceEntity()
        player.faceLoc(loc, type.width, type.length)
        player.interaction = interaction
    }

    private fun opNpc(connection: LinkConnection, command: LinkCommand.OpNpc) {
        val player = connection.player ?: return
        if (player.isDelayed) {
            return
        }
        val op = interactionOp(command.op) ?: return
        val npc = npcList[command.index] ?: return
        if (npc.isDelayed || !npcInteractions.hasOp(npc, player.vars, op)) {
            return
        }
        if (chebyshev(player.coords, npc.coords) > OP_RANGE + npc.size) {
            connection.send("MSG 0 You need to get closer.")
            return
        }
        val interaction =
            InteractionNpcOp(
                target = npc,
                op = op,
                hasOpTrigger = npcInteractions.hasOpTrigger(player, npc, op),
                hasApTrigger = npcInteractions.hasApTrigger(player, npc, op),
            )
        player.clearPendingAction(eventBus)
        player.faceNpc(npc)
        player.interaction = interaction
    }

    private fun chat(connection: LinkConnection, text: String) {
        val player = connection.player ?: return
        if (text.isEmpty()) {
            return
        }
        if (text.startsWith("::")) {
            val split = text.substring(2).trim().lowercase().split(' ').filter { it.isNotEmpty() }
            if (split.isEmpty()) {
                return
            }
            val ran = cheats.execute(player, split[0], split.drop(1))
            if (!ran) {
                connection.send("MSG 0 Unknown command: ${split[0]}")
            }
            return
        }
        for (other in connections) {
            other.send("MSG 2 ${player.displayName}: $text")
        }
        eventBus.publish(LinkPlayerSaid(player, text))
    }

    private fun opItem(connection: LinkConnection, command: LinkCommand.OpItem) {
        val player = connection.player ?: return
        val inventory = if (command.worn) player.worn else player.inv
        if (command.slot !in 0 until inventory.size || inventory[command.slot] == null) {
            return
        }
        if (command.op == EXAMINE_OP) {
            if (command.worn) wornInteractions.examine(player, inventory, command.slot)
            else heldInteractions.examine(player, inventory, command.slot)
            return
        }
        if (player.isAccessProtected) {
            connection.send("MSG 0 You can't do that right now.")
            return
        }
        if (command.worn) {
            val op = interfaceButtonOp(command.op) ?: return
            protectedAccess.launch(player) {
                clearPendingAction()
                wornInteractions.interact(this, inventory, command.slot, op)
            }
        } else {
            val op = heldOp(command.op) ?: return
            protectedAccess.launch(player) {
                clearPendingAction()
                heldInteractions.interact(this, inventory, command.slot, op)
            }
        }
    }

    /** Picks up a ground item through its normal "Take" op (op 3), like clicking it in OSRS. */
    private fun take(connection: LinkConnection, command: LinkCommand.Take) {
        val player = connection.player ?: return
        if (player.isDelayed || player.isAccessProtected) {
            return
        }
        val coords = CoordGrid(command.x, command.z, command.level)
        val obj = objRepo.findAll(coords).firstOrNull { it.type == command.id && it.isVisibleTo(player) }
        if (obj == null) {
            connection.send("MSG 0 Too late - it's gone!")
            return
        }
        if (player.coords.level != coords.level || chebyshev(player.coords, coords) > OP_RANGE) {
            connection.send("MSG 0 You need to get closer.")
            return
        }
        val trigger = objInteractions.opTrigger(obj, InteractionOp.Op3)
        if (trigger == null) {
            connection.send("MSG 0 Nothing interesting happens.")
            return
        }
        player.clearPendingAction(eventBus)
        player.clearInteraction()
        protectedAccess.launch(player) { eventBus.publish(this, trigger) }
    }

    private fun bankItem(connection: LinkConnection, command: LinkCommand.BankItem) {
        val player = connection.player ?: return
        if (command.deposit) bank.deposit(connection, player, command.slot, command.id, command.count)
        else bank.withdraw(connection, player, command.slot, command.id, command.count)
    }

    private fun heldOp(op: Int): HeldOp? =
        when (op) {
            1 -> HeldOp.Op1
            2 -> HeldOp.Op2
            3 -> HeldOp.Op3
            4 -> HeldOp.Op4
            5 -> HeldOp.Op5
            else -> null
        }

    private fun interfaceButtonOp(op: Int): IfButtonOp? =
        when (op) {
            1 -> IfButtonOp.Op1
            2 -> IfButtonOp.Op2
            3 -> IfButtonOp.Op3
            4 -> IfButtonOp.Op4
            5 -> IfButtonOp.Op5
            else -> null
        }

    private fun resumeDialogue(connection: LinkConnection, option: Int) {
        val player = connection.player ?: return
        HeadlessDialogue.resume(player, option)
    }

    private fun stop(connection: LinkConnection) {
        val player = connection.player ?: return
        if (player.isDelayed) {
            return
        }
        player.clearInteraction()
        player.resetFaceEntity()
    }

    /** Mirrors the special attack orb: arms a combat special so the next attack uses it. */
    private fun armSpecial(connection: LinkConnection) {
        val player = connection.player ?: return
        val weapon = player.righthand
        if (weapon == null || specialsReg[weapon] !is SpecialAttack.Combat) {
            player.specialType = SpecialAttackType.None
            connection.send("MSG 0 This weapon does not have a special attack.")
            return
        }
        player.specialType = SpecialAttackType.Weapon
    }

    private fun interactionOp(op: Int): InteractionOp? =
        when (op) {
            1 -> InteractionOp.Op1
            2 -> InteractionOp.Op2
            3 -> InteractionOp.Op3
            4 -> InteractionOp.Op4
            5 -> InteractionOp.Op5
            else -> null
        }

    private fun distanceToLoc(from: CoordGrid, loc: BoundLocInfo): Int {
        val w = loc.adjustedWidth
        val l = loc.adjustedLength
        val dx = max(loc.x - from.x, max(0, from.x - (loc.x + w - 1)))
        val dz = max(loc.z - from.z, max(0, from.z - (loc.z + l - 1)))
        return max(max(dx, 0), max(dz, 0))
    }

    // ------------------------------------------------------------------ per-tick sync

    /** Seq id for the link protocol: [EntitySeq.ZERO] is the engine's "reset animation" (-1). */
    private fun linkSeqId(seq: EntitySeq): Int = if (seq == EntitySeq.ZERO) -1 else seq.id

    private fun syncClients() {
        for (connection in connections) {
            val player = connection.player ?: continue
            if (player.loggingOut) {
                continue
            }
            pin(connection, player)
            val c = player.coords
            if (c != connection.lastSentTile) {
                connection.lastSentTile = c
                connection.send("TILE ${c.x} ${c.z} ${c.level}")
            }
            val hp = player.hitpoints
            val maxHp = player.baseHitpointsLvl
            val spec = player.specialEnergy
            if (hp != connection.lastHp || maxHp != connection.lastMaxHp || spec != connection.lastSpecEnergy) {
                connection.lastHp = hp
                connection.lastMaxHp = maxHp
                connection.lastSpecEnergy = spec
                connection.send("ME $hp $maxHp $spec")
            }
            if (player.pendingSequence != EntitySeq.NULL) {
                connection.send("ANIM ${linkSeqId(player.pendingSequence)}")
            }
            val selfLook = LinkAppearance.line("me", player)
            if (selfLook != connection.lastSelfLook) {
                connection.lastSelfLook = selfLook
                connection.send(selfLook)
            }
            val selfBas = LinkBas.line("me", player)
            if (selfBas != connection.lastSelfBas) {
                connection.lastSelfBas = selfBas
                connection.send(selfBas)
            }
            sendNpcs(connection, player)
            sendPlayers(connection, player)
            sendLocs(connection, player)
            sendInventories(connection, player)
            sendGroundItems(connection, player)
            bank.sync(connection, player, ::inventoryLine)
            shop.sync(connection, player)
            spells.sync(connection, player, currentTick)
            geometry.sync(connection, player)
            val frozen = player.isFrozen
            if (frozen != connection.frozen) {
                connection.frozen = frozen
                connection.send("FROZEN ${if (frozen) 1 else 0}")
            }
        }
    }

    /** Other players (bots) near the link player: `PLAYERS slot|name|x|z|level|hp|maxHp|anim;...`. */
    private fun sendPlayers(connection: LinkConnection, player: Player) {
        val me = player.coords
        val sb = StringBuilder("PLAYERS ")
        for (other in playerList) {
            if (other === player || other.coords.level != me.level || chebyshev(other.coords, me) > PLAYER_VIEW_RANGE) {
                continue
            }
            val c = other.coords
            val look = LinkAppearance.line(other.slotId.toString(), other)
            if (connection.lastLooks[other.slotId] != look) {
                connection.lastLooks[other.slotId] = look
                connection.send(look)
            }
            val bas = LinkBas.line(other.slotId.toString(), other)
            if (connection.lastBas[other.slotId] != bas) {
                connection.lastBas[other.slotId] = bas
                connection.send(bas)
            }
            val anim = if (other.pendingSequence != EntitySeq.NULL) linkSeqId(other.pendingSequence) else -1
            sb.append(other.slotId).append('|').append(other.displayName.replace('|', ' ')).append('|')
                .append(c.x).append('|').append(c.z).append('|').append(c.level).append('|')
                .append(other.hitpoints).append('|').append(other.baseHitpointsLvl).append('|').append(anim).append(';')
        }
        val line = sb.toString()
        if (line != connection.lastPlayersLine) {
            connection.lastPlayersLine = line
            connection.send(line)
        }
    }

    private fun sendInventories(connection: LinkConnection, player: Player) {
        val inv = inventoryLine("INV", player.inv)
        if (inv != connection.lastInvLine) {
            connection.lastInvLine = inv
            connection.send(inv)
        }
        val worn = inventoryLine("WORN", player.worn)
        if (worn != connection.lastWornLine) {
            connection.lastWornLine = worn
            connection.send(worn)
        }
        val stats = StringBuilder("STATS ")
        for (stat in ServerCacheManager.getStats().values) {
            if (stat.unreleased) {
                continue
            }
            val name = RSCM.getReverseMapping(RSCMType.STAT, stat.id)
            stats.append(stat.id).append('|').append(stat.displayName.ifEmpty { name.substringAfter('.') })
                .append('|').append(player.stat(name))
                .append('|').append(player.statBase(name))
                .append('|').append(player.statMap.getXP(name)).append(';')
        }
        val statsLine = stats.toString()
        if (statsLine != connection.lastStatsLine) {
            connection.lastStatsLine = statsLine
            connection.send(statsLine)
        }
    }

    /** Ground items this player can see on its floor nearby (loot, drops, item spawns). */
    private fun sendGroundItems(connection: LinkConnection, player: Player) {
        val me = player.coords
        val centre = ZoneKey.from(me)
        val sb = StringBuilder("GITEMS ")
        var shown = 0
        loop@ for (dx in -GROUND_ZONE_RADIUS..GROUND_ZONE_RADIUS) {
            for (dz in -GROUND_ZONE_RADIUS..GROUND_ZONE_RADIUS) {
                for (obj in objRepo.findAll(centre.translate(dx, dz))) {
                    if (obj.level != me.level || !obj.isVisibleTo(player)) continue
                    val name = (ServerCacheManager.getItem(obj.type)?.name ?: "null")
                        .replace('|', '/').replace(';', ',')
                    sb.append(obj.type).append('|').append(obj.count).append('|').append(obj.x).append('|')
                        .append(obj.z).append('|').append(obj.level).append('|').append(name).append(';')
                    if (++shown >= MAX_GROUND_ITEMS) break@loop
                }
            }
        }
        val line = sb.toString()
        if (line != connection.lastGroundLine) {
            connection.lastGroundLine = line
            connection.send(line)
        }
    }

    private fun inventoryLine(prefix: String, inventory: Inventory): String {
        val sb = StringBuilder(prefix).append(' ')
        for (slot in 0 until inventory.size) {
            val obj = inventory[slot] ?: continue
            val type = ServerCacheManager.getItem(obj.id)
            val name = (type?.name ?: "null").replace('|', '/').replace(';', ',')
            val ops =
                (type?.interfaceOptions ?: emptyList()).take(5)
                    .joinToString("/") { (it ?: "").replace('/', '-').replace('|', '-').replace(';', ',') }
            sb.append(slot).append('|').append(obj.id).append('|').append(obj.count).append('|')
                .append(name).append('|').append(ops).append(';')
        }
        return sb.toString()
    }

    /**
     * Unity owns the position. If the server walked the player (an interaction re-route), put it
     * back; if the server teleported it (ladder, death, script), let Unity follow instead.
     */
    private fun pin(connection: LinkConnection, player: Player) {
        val client = connection.clientTile ?: return
        val c = player.coords
        if (c == client) {
            return
        }
        if (player.isDelayed) {
            // Server-driven movement (agility force-moves, scripted steps): Unity follows, even
            // for short moves that would otherwise be pinned back to the client tile.
            connection.clientTile = c
            forceMove(connection, c)
            return
        }
        val teleported = c.level != client.level || chebyshev(c, client) > PIN_RANGE
        if (teleported) {
            connection.clientTile = c
        } else if (collision.isZoneValid(client)) {
            place(player, client, keepInteraction = true)
        }
    }

    /** Tells Unity to move the local player to [tile] unconditionally (FORCEMOVE in OsrsServerLink). */
    private fun forceMove(connection: LinkConnection, tile: CoordGrid) {
        connection.lastSentTile = tile
        connection.send("FORCEMOVE ${tile.x} ${tile.z} ${tile.level}")
    }

    /**
     * Zones entering view get every spawned loc change (opened doors, tree stumps, ...); zones
     * already in view get this tick's changes from the shared zone update map.
     */
    private fun sendLocs(connection: LinkConnection, player: Player) {
        val centre = ZoneKey.from(player.coords)
        val visible = HashSet<Int>()
        for (dx in -LOC_ZONE_RADIUS..LOC_ZONE_RADIUS) {
            for (dz in -LOC_ZONE_RADIUS..LOC_ZONE_RADIUS) {
                val zone = centre.translate(dx, dz)
                visible += zone.packed
                if (connection.locZones.add(zone.packed)) {
                    for (loc in locRegistry.findAllSpawned(zone)) {
                        connection.send(locLine(loc))
                    }
                    continue
                }
                val updates = zoneUpdates[zone] ?: continue
                val base = zone.toCoords()
                for (prot in updates) {
                    when (prot) {
                        is LocAddChangeV2 ->
                            connection.send(
                                "LOC add ${prot.id} ${base.x + prot.xInZone} " +
                                    "${base.z + prot.zInZone} ${base.level} ${prot.shape} " +
                                    "${prot.rotation}"
                            )
                        is LocDel ->
                            connection.send(
                                "LOC del ${base.x + prot.xInZone} ${base.z + prot.zInZone} " +
                                    "${base.level} ${prot.shape} ${prot.rotation}"
                            )
                        is SoundArea ->
                            connection.send(
                                "SNDA ${prot.id} ${base.x + prot.xInZone} ${base.z + prot.zInZone} " +
                                    "${base.level} ${prot.loops} ${prot.delay} ${prot.range}"
                            )
                    }
                }
            }
        }
        connection.locZones.retainAll(visible)
    }

    private fun locLine(loc: LocInfo): String {
        val c = loc.coords
        return if (loc.id == LocRegistry.DELETED_LOC_ID) {
            "LOC del ${c.x} ${c.z} ${c.level} ${loc.shapeId} ${loc.angleId}"
        } else {
            "LOC add ${loc.id} ${c.x} ${c.z} ${c.level} ${loc.shapeId} ${loc.angleId}"
        }
    }

    private fun sendNpcs(connection: LinkConnection, player: Player) {
        val me = player.coords
        val nearby =
            npcRepo
                .findAll(ZoneKey.from(me), NPC_ZONE_RADIUS)
                .filter { it.isVisible && it.coords.level == me.level }
                .sortedBy { chebyshev(it.coords, me) }
                .take(MAX_NPCS)
        val sb = StringBuilder("NPCS ")
        for (npc in nearby) {
            val c = npc.coords
            sb.append(npc.slotId).append(',').append(npc.visType.id).append(',')
                .append(c.x).append(',').append(c.z).append(',').append(c.level).append(',')
                .append(npc.hitpoints).append(',').append(npc.visType.hitpoints).append(';')
            if (npc.pendingSequence != EntitySeq.NULL) {
                connection.send("NANIM ${npc.slotId} ${linkSeqId(npc.pendingSequence)}")
            }
        }
        val line = sb.toString()
        if (line != connection.lastNpcLine) {
            connection.lastNpcLine = line
            connection.send(line)
        }
    }

    private companion object {
        private val logger = InlineLogger()

        const val LINK_PORT = 43610

        /** Tiles a player may be from a target's footprint to interact with it. */
        const val OP_RANGE = 1

        /** Server moves further than this are treated as teleports Unity should follow. */
        const val PIN_RANGE = 3

        const val NPC_ZONE_RADIUS = 2

        const val PLAYER_VIEW_RANGE = 24

        const val EXAMINE_OP = 10

        val PAUSE_COMPONENTS =
            mapOf(
                "interface.chat_left" to "component.chat_left:continue",
                "interface.chat_right" to "component.chat_right:continue",
                "interface.messagebox" to "component.messagebox:continue",
                "interface.objectbox" to "component.objectbox:universe",
                "interface.objectbox_double" to "component.objectbox_double:pausebutton",
                "interface.levelup_display" to "component.levelup_display:continue",
                "interface.chatmenu" to "component.chatmenu:options",
            )

        const val LOC_ZONE_RADIUS = 3

        /** Damage taken off melee and ranged hits while holding a shield block. */
        const val BLOCK_REDUCTION_PERCENT = 50

        /** Hits landing within this many ticks of a dodge roll do no damage. */
        const val DODGE_TICKS = 1

        const val MAX_NPCS = 200

        const val GROUND_ZONE_RADIUS = 2

        const val MAX_GROUND_ITEMS = 300

        private var Player.specialType by enumVarp<SpecialAttackType>("varp.sa_attack")
        private val Player.specialEnergy by intVarp("varp.sa_energy")

        private fun chebyshev(a: CoordGrid, b: CoordGrid): Int = max(abs(a.x - b.x), abs(a.z - b.z))
    }
}
