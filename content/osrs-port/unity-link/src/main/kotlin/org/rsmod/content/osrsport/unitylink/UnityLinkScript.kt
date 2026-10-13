package org.rsmod.content.osrsport.unitylink

import com.github.michaelbull.logging.InlineLogger
import dev.openrune.ServerCacheManager
import dev.or2.central.account.Rights
import dev.openrune.rscm.RSCM
import dev.openrune.rscm.RSCM.asRSCM
import dev.openrune.rscm.RSCMType
import jakarta.inject.Inject
import kotlin.math.abs
import kotlin.math.max
import net.rsprot.protocol.game.outgoing.zone.payload.LocAddChangeV2
import net.rsprot.protocol.game.outgoing.zone.payload.LocDel
import org.rsmod.api.account.AccountManager
import org.rsmod.api.account.loader.request.AccountLoadAuth
import org.rsmod.api.account.loader.request.AccountLoadResponse
import org.rsmod.api.account.loader.request.isNewAccount
import org.rsmod.api.game.process.GameLifecycle
import org.rsmod.api.player.events.PlayerHitEvents
import org.rsmod.api.player.input.ResumePauseButtonInput
import org.rsmod.api.player.interact.LocInteractions
import org.rsmod.api.player.interact.NpcInteractions
import org.rsmod.api.player.output.mes
import org.rsmod.api.player.protect.clearPendingAction
import org.rsmod.api.player.stat.baseHitpointsLvl
import org.rsmod.api.player.stat.hitpoints
import org.rsmod.api.player.lefthand
import org.rsmod.api.player.righthand
import org.rsmod.api.player.vars.boolVarBit
import org.rsmod.api.player.vars.enumVarp
import org.rsmod.api.player.vars.intVarp
import org.rsmod.api.realm.Realm
import org.rsmod.api.registry.account.AccountRegistry
import org.rsmod.api.registry.loc.LocRegistry
import org.rsmod.api.registry.player.PlayerRegistry
import org.rsmod.api.registry.player.PlayerRegistryResult
import org.rsmod.api.registry.zone.ZoneUpdateMap
import org.rsmod.api.repo.npc.NpcRepository
import org.rsmod.api.script.onCommand
import org.rsmod.api.script.onEvent
import org.rsmod.api.specials.SpecialAttack
import org.rsmod.api.specials.SpecialAttackRegistry
import org.rsmod.api.specials.SpecialAttackType
import org.rsmod.events.EventBus
import org.rsmod.game.cheat.CheatCommandMap
import org.rsmod.game.entity.NpcList
import org.rsmod.game.entity.Player
import org.rsmod.game.entity.player.SessionStateEvent
import org.rsmod.game.interact.InteractionLoc
import org.rsmod.game.interact.InteractionLocOp
import org.rsmod.game.interact.InteractionNpcOp
import org.rsmod.game.interact.InteractionOp
import org.rsmod.game.loc.BoundLocInfo
import org.rsmod.game.loc.LocInfo
import org.rsmod.game.map.collision.isZoneValid
import org.rsmod.game.hit.HitType
import org.rsmod.game.movement.MoveSpeed
import org.rsmod.game.seq.EntitySeq
import org.rsmod.game.ui.Component
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
 * n), `BYE`. Dialogues themselves arrive as `DLG ...` lines, see [LinkClient].
 * Server -> client: `WELCOME x z level`, `TILE x z level`, `MSG type text`, `ME hp maxHp spec`,
 * `HIT me|npc index damage`, `NPCS n idx,id,x,z,level,hp,maxHp;...`, `ANIM seq` (player
 * animation), `NANIM index seq` (npc animation), `LOC add id x z level shape rot` /
 * `LOC del x z level shape rot`, `ERROR text`.
 *
 * The Unity client owns the player's position: the server never walks the link player, it only
 * teleports it where Unity says it stands. Interactions therefore work by range checks, not by
 * walking to the target.
 */
class UnityLinkScript
@Inject
constructor(
    private val accountManager: AccountManager,
    private val accountRegistry: AccountRegistry,
    private val playerRegistry: PlayerRegistry,
    private val eventBus: EventBus,
    private val realm: Realm,
    private val collision: CollisionFlagMap,
    private val locRegistry: LocRegistry,
    private val locInteractions: LocInteractions,
    private val npcInteractions: NpcInteractions,
    private val npcList: NpcList,
    private val npcRepo: NpcRepository,
    private val zoneUpdates: ZoneUpdateMap,
    private val specialsReg: SpecialAttackRegistry,
    private val cheats: CheatCommandMap,
) : PluginScript() {
    private val server = UnityLinkServer(LINK_PORT)
    private val connections = LinkedHashSet<LinkConnection>()
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
            is LinkCommand.OpNpc -> opNpc(connection, command)
            is LinkCommand.Stop -> stop(connection)
            is LinkCommand.Chat -> chat(connection, command.text)
            is LinkCommand.Special -> armSpecial(connection)
            is LinkCommand.Block -> connection.blocking = command.blocking
            is LinkCommand.Dodge -> connection.dodgeUntilTick = currentTick + DODGE_TICKS
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
            accountManager.loadOrCreate(AccountLoadAuth.UnknownDevice, name, { LINK_PASSWORD_HASH }) {
                response ->
                onAccountLoaded(connection, response)
            }
        if (!queued) {
            connection.loggingIn = false
            connection.send("ERROR login service busy, try again")
        }
    }

    /** Runs on the account loader thread, like the normal login hook. */
    private fun onAccountLoaded(connection: LinkConnection, response: AccountLoadResponse) {
        if (response !is AccountLoadResponse.Ok) {
            connection.loggingIn = false
            connection.send("ERROR could not load account: ${response::class.simpleName}")
            return
        }
        val player = createPlayer(connection, response)
        accountRegistry.queueLogin(player, response) { p, _ -> finishLogin(connection, p) }
    }

    private fun createPlayer(connection: LinkConnection, response: AccountLoadResponse.Ok): Player {
        val player = Player(client = LinkClient(connection))
        for (transform in response.transforms) {
            transform.apply(player)
        }
        player.ui.setWindowStatus(width = 765, height = 503, resizable = false)
        player.modLevel = Rights.ADMINISTRATOR
        player.newAccount = response.isNewAccount()
        player.worldType = response.worldType

        val config = realm.config
        if (player.newAccount || response.firstVisitToWorldType) {
            player.coords = config.spawnCoord
            player.xpRate = config.baseXpRate
            if (response.firstVisitToWorldType) {
                player.newAccount = true
            }
            if (config.autoAssignDisplayNames && player.displayName.isBlank()) {
                player.displayName = player.username.replaceFirstChar { it.titlecase() }
            }
        }
        return player
    }

    /** Runs on the game thread, from the account registry's login queue. */
    private fun finishLogin(connection: LinkConnection, player: Player) {
        connection.loggingIn = false
        if (connection.isClosed) {
            return
        }
        if (playerRegistry.isOnline(player.userId)) {
            connection.send("ERROR that account is already logged in")
            return
        }
        val slot = playerRegistry.nextFreeSlot()
        if (slot == null) {
            connection.send("ERROR server is full")
            return
        }
        eventBus.publish(SessionStateEvent.PrepareLogin(player))
        player.slotId = slot
        val result = playerRegistry.add(player)
        if (result != PlayerRegistryResult.Add.Success) {
            connection.send("ERROR could not register player: $result")
            logger.warn { "Unity link failed to register player: $result ($player)" }
            return
        }
        eventBus.publish(SessionStateEvent.Login(player))
        eventBus.publish(SessionStateEvent.EngineLogin(player))
        connection.player = player
        connection.clientTile = player.coords
        connection.lastSentTile = player.coords
        val c = player.coords
        connection.send("WELCOME ${c.x} ${c.z} ${c.level}")
        connection.send("ME ${player.hitpoints} ${player.baseHitpointsLvl} ${player.specialEnergy}")
        logger.info { "Unity link: ${player.username} logged in at $c (slot $slot)" }
    }

    // ------------------------------------------------------------------ movement

    private fun move(connection: LinkConnection, x: Int, z: Int, level: Int) {
        val player = connection.player ?: return
        if (player.loggingOut || level !in 0..3) {
            return
        }
        val dest = CoordGrid(x, z, level)
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
    }

    /** Plays the chatbox modal's pause button, like a click on "Click here to continue". */
    private fun resumeDialogue(connection: LinkConnection, option: Int) {
        val player = connection.player ?: return
        val interfaceId = player.ui.getModalOrNull(CHAT_MODAL)?.packed ?: return
        val name = RSCM.getReverseMapping(RSCMType.INTERFACE, interfaceId)
        val pauseComponent = PAUSE_COMPONENTS[name] ?: return
        val sub = if (name == "interface.chatmenu") option else -1
        player.ui.queueClose(Component(CHAT_MODAL.asRSCM(RSCMType.COMPONENT)))
        player.resumeActiveCoroutine(ResumePauseButtonInput(pauseComponent, sub))
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
                connection.send("ANIM ${player.pendingSequence.id}")
            }
            sendNpcs(connection, player)
            sendLocs(connection, player)
        }
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
        val teleported = c.level != client.level || chebyshev(c, client) > PIN_RANGE
        if (teleported) {
            connection.clientTile = c
        } else if (collision.isZoneValid(client)) {
            place(player, client, keepInteraction = true)
        }
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
                connection.send("NANIM ${npc.slotId} ${npc.pendingSequence.id}")
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

        /** Accounts made through the link are local-only; this marks them and is never checked. */
        const val LINK_PASSWORD_HASH = "unity-link-local"

        /** Tiles a player may be from a target's footprint to interact with it. */
        const val OP_RANGE = 1

        /** Server moves further than this are treated as teleports Unity should follow. */
        const val PIN_RANGE = 3

        const val NPC_ZONE_RADIUS = 2

        const val CHAT_MODAL = "component.chatbox:chatmodal"

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

        private var Player.newAccount by boolVarBit("varbit.new_player_account")
        private var Player.specialType by enumVarp<SpecialAttackType>("varp.sa_attack")
        private val Player.specialEnergy by intVarp("varp.sa_energy")

        private fun chebyshev(a: CoordGrid, b: CoordGrid): Int = max(abs(a.x - b.x), abs(a.z - b.z))
    }
}
