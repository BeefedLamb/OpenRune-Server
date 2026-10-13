package org.rsmod.content.osrsport.unitylink

import com.github.michaelbull.logging.InlineLogger
import jakarta.inject.Inject
import kotlin.math.abs
import kotlin.math.max
import org.rsmod.api.account.AccountManager
import org.rsmod.api.account.loader.request.AccountLoadAuth
import org.rsmod.api.account.loader.request.AccountLoadResponse
import org.rsmod.api.account.loader.request.isNewAccount
import org.rsmod.api.game.process.GameLifecycle
import org.rsmod.api.player.output.mes
import org.rsmod.api.player.vars.boolVarBit
import org.rsmod.api.realm.Realm
import org.rsmod.api.registry.account.AccountRegistry
import org.rsmod.api.registry.player.PlayerRegistry
import org.rsmod.api.registry.player.PlayerRegistryResult
import org.rsmod.api.script.onCommand
import org.rsmod.api.script.onEvent
import org.rsmod.events.EventBus
import org.rsmod.game.entity.Player
import org.rsmod.game.entity.player.SessionStateEvent
import org.rsmod.game.entity.util.PathingEntityCommon
import org.rsmod.game.map.collision.isZoneValid
import org.rsmod.map.CoordGrid
import org.rsmod.plugin.scripts.PluginScript
import org.rsmod.plugin.scripts.ScriptContext
import org.rsmod.routefinder.collision.CollisionFlagMap

/**
 * Lets the Unity client play as a normal server-side player without the OSRS game protocol.
 * The player has no rsprot session (it keeps the default no-op client), so OSRS clients such as
 * RuneLite cannot see it; `::where` shows linked players instead.
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
) : PluginScript() {
    private val server = UnityLinkServer(LINK_PORT)
    private val connections = LinkedHashSet<LinkConnection>()

    override fun ScriptContext.startup() {
        onEvent<GameLifecycle.Startup> {
            try {
                server.start()
            } catch (e: Exception) {
                logger.error(e) { "Unity link could not open port $LINK_PORT" }
            }
        }
        onEvent<GameLifecycle.Shutdown> { server.stop() }
        onEvent<GameLifecycle.StartCycle> { drainInbox() }
        onEvent<GameLifecycle.LateCycle> { reportPositions() }

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
        val player = createPlayer(response)
        accountRegistry.queueLogin(player, response) { p, _ -> finishLogin(connection, p) }
    }

    private fun createPlayer(response: AccountLoadResponse.Ok): Player {
        val player = Player()
        for (transform in response.transforms) {
            transform.apply(player)
        }
        player.ui.setWindowStatus(width = 765, height = 503, resizable = false)
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
        val c = player.coords
        connection.lastSentX = c.x
        connection.lastSentZ = c.z
        connection.lastSentLevel = c.level
        connection.send("WELCOME ${c.x} ${c.z} ${c.level}")
        logger.info { "Unity link: ${player.username} logged in at $c (slot $slot)" }
    }

    private fun move(connection: LinkConnection, x: Int, z: Int, level: Int) {
        val player = connection.player ?: return
        if (player.loggingOut || level !in 0..3) {
            return
        }
        val current = player.coords
        if (current.x == x && current.z == z && current.level == level) {
            return
        }
        val dest = CoordGrid(x, z, level)
        if (!collision.isZoneValid(dest)) {
            return
        }
        val distance = max(abs(current.x - x), abs(current.z - z))
        if (distance > MAX_STEP_TILES && current.level == level) {
            logger.debug { "Unity link: ${player.username} jumped $distance tiles to $dest" }
        }
        PathingEntityCommon.telejump(player, collision, dest)
    }

    private fun reportPositions() {
        for (connection in connections) {
            val player = connection.player ?: continue
            val c = player.coords
            if (c.x != connection.lastSentX || c.z != connection.lastSentZ || c.level != connection.lastSentLevel) {
                connection.lastSentX = c.x
                connection.lastSentZ = c.z
                connection.lastSentLevel = c.level
                connection.send("TILE ${c.x} ${c.z} ${c.level}")
            }
        }
    }

    private companion object {
        private val logger = InlineLogger()

        const val LINK_PORT = 43610

        /** Accounts made through the link are local-only; this marks them and is never checked. */
        const val LINK_PASSWORD_HASH = "unity-link-local"

        const val MAX_STEP_TILES = 4

        private var Player.newAccount by boolVarBit("varbit.new_player_account")
    }
}
