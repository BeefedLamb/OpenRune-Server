package org.rsmod.content.osrsport.unitylink

import com.github.michaelbull.logging.InlineLogger
import java.io.BufferedReader
import java.io.InputStreamReader
import java.io.OutputStreamWriter
import java.io.PrintWriter
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketException
import java.nio.charset.StandardCharsets
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.atomic.AtomicBoolean
import org.rsmod.game.entity.Player
import org.rsmod.map.CoordGrid

/**
 * Line-based TCP listener for the Unity client. Socket threads only parse lines into [inbox];
 * everything that touches game state happens on the game thread when the script drains it.
 *
 * See [UnityLinkScript] for the message formats.
 */
class UnityLinkServer(private val port: Int) {
    val inbox: ConcurrentLinkedQueue<LinkCommand> = ConcurrentLinkedQueue()

    private val running = AtomicBoolean(false)
    private var serverSocket: ServerSocket? = null

    fun start() {
        if (!running.compareAndSet(false, true)) {
            return
        }
        val socket = ServerSocket(port, 8, InetAddress.getLoopbackAddress())
        serverSocket = socket
        val acceptor =
            Thread({ acceptLoop(socket) }, "unity-link-accept").apply { isDaemon = true }
        acceptor.start()
        logger.info { "Unity link listening on 127.0.0.1:$port" }
    }

    fun stop() {
        running.set(false)
        try {
            serverSocket?.close()
        } catch (_: Exception) {
        }
    }

    private fun acceptLoop(socket: ServerSocket) {
        while (running.get()) {
            val client =
                try {
                    socket.accept()
                } catch (_: SocketException) {
                    break
                }
            client.tcpNoDelay = true
            val connection = LinkConnection(client)
            Thread({ readLoop(connection) }, "unity-link-client").apply { isDaemon = true }.start()
        }
    }

    private fun readLoop(connection: LinkConnection) {
        try {
            val reader =
                BufferedReader(InputStreamReader(connection.socket.getInputStream(), StandardCharsets.UTF_8))
            while (running.get()) {
                val line = reader.readLine() ?: break
                val command = parse(connection, line.trim()) ?: continue
                inbox.add(command)
            }
        } catch (_: Exception) {
        } finally {
            connection.close()
            inbox.add(LinkCommand.Disconnected(connection))
        }
    }

    private fun parse(connection: LinkConnection, line: String): LinkCommand? {
        if (line.isEmpty()) {
            return null
        }
        val parts = line.split(' ')
        return when (parts[0].uppercase()) {
            "HELLO" -> {
                val name = parts.drop(1).joinToString(" ").trim()
                if (name.isEmpty() || name.length > 12) {
                    connection.send("ERROR name must be 1-12 characters")
                    null
                } else {
                    LinkCommand.Hello(connection, name)
                }
            }
            "POS" -> {
                val x = parts.getOrNull(1)?.toIntOrNull()
                val z = parts.getOrNull(2)?.toIntOrNull()
                val level = parts.getOrNull(3)?.toIntOrNull() ?: 0
                if (x == null || z == null) null else LinkCommand.Position(connection, x, z, level)
            }
            "OPLOC" -> {
                val v = parts.drop(1).map { it.toIntOrNull() }
                if (v.size < 5 || v.any { it == null }) null
                else LinkCommand.OpLoc(connection, v[0]!!, v[1]!!, v[2]!!, v[3]!!, v[4]!!)
            }
            "OPLOCU" -> {
                // Use an inventory item on a loc: OPLOCU locId x z level invSlot
                val v = parts.drop(1).map { it.toIntOrNull() }
                if (v.size < 5 || v.any { it == null }) null
                else LinkCommand.OpLocU(connection, v[0]!!, v[1]!!, v[2]!!, v[3]!!, v[4]!!)
            }
            "OPNPC" -> {
                val idx = parts.getOrNull(1)?.toIntOrNull()
                val op = parts.getOrNull(2)?.toIntOrNull()
                if (idx == null || op == null) null else LinkCommand.OpNpc(connection, idx, op)
            }
            "CHAT" -> LinkCommand.Chat(connection, line.substring(4).trim())
            "DODGE" -> LinkCommand.Dodge(connection)
            "OPHELD", "OPWORN" -> {
                val slot = parts.getOrNull(1)?.toIntOrNull()
                val op = parts.getOrNull(2)?.toIntOrNull()
                if (slot == null || op == null) null
                else LinkCommand.OpItem(connection, worn = parts[0].uppercase() == "OPWORN", slot = slot, op = op)
            }
            "TAKE" -> {
                val v = parts.drop(1).map { it.toIntOrNull() }
                if (v.size < 4 || v.any { it == null }) null
                else LinkCommand.Take(connection, v[0]!!, v[1]!!, v[2]!!, v[3]!!)
            }
            "WITHDRAW", "DEPOSIT" -> {
                val v = parts.drop(1).map { it.toIntOrNull() }
                if (v.size < 3 || v.any { it == null }) null
                else LinkCommand.BankItem(connection, parts[0].uppercase() == "DEPOSIT", v[0]!!, v[1]!!, v[2]!!)
            }
            "DEPOSITALL" -> LinkCommand.BankDepositAll(connection, worn = false)
            "DEPOSITWORN" -> LinkCommand.BankDepositAll(connection, worn = true)
            "BANKNOTE" -> LinkCommand.BankNote(connection, parts.getOrNull(1) == "1")
            "BANKCLOSE" -> LinkCommand.BankClose(connection)
            "BUY", "SELL" -> {
                val v = parts.drop(1).map { it.toIntOrNull() }
                if (v.size < 3 || v.any { it == null }) null
                else LinkCommand.ShopItem(connection, parts[0].uppercase() == "SELL", v[0]!!, v[1]!!, v[2]!!)
            }
            "SHOPVALUE" -> {
                val v = parts.drop(1).map { it.toIntOrNull() }
                if (v.size < 3 || v.any { it == null }) null
                else LinkCommand.ShopValue(connection, v[0] == 1, v[1]!!, v[2]!!)
            }
            "SHOPCLOSE" -> LinkCommand.ShopClose(connection)
            "CAST" -> {
                // CAST spellObjId [backpackSlot]
                val spell = parts.getOrNull(1)?.toIntOrNull()
                if (spell == null) null else LinkCommand.Cast(connection, spell, parts.getOrNull(2)?.toIntOrNull())
            }
            "DLGC" -> LinkCommand.DialogueContinue(connection)
            "DLGS" -> {
                val option = parts.getOrNull(1)?.toIntOrNull()
                if (option == null) null else LinkCommand.DialogueSelect(connection, option)
            }
            "APPEAR" -> {
                val v = parts.drop(1).map { it.toIntOrNull() }
                if (v.size != CharacterDesign.FIELD_COUNT || v.any { it == null }) null
                else LinkCommand.Appear(connection, v.map { it!! })
            }
            "STOP" -> LinkCommand.Stop(connection)
            "SPEC" -> LinkCommand.Special(connection)
            "BLOCK" -> LinkCommand.Block(connection, parts.getOrNull(1) == "1")
            "BYE" -> LinkCommand.Bye(connection)
            else -> null
        }
    }

    private companion object {
        private val logger = InlineLogger()
    }
}

class LinkConnection(val socket: Socket) {
    @Volatile var player: Player? = null
    @Volatile var loggingIn: Boolean = false
    /** Where Unity last said the player stands (game thread only). */
    var clientTile: CoordGrid? = null
    var lastSentTile: CoordGrid? = null
    var lastHp: Int = -1
    var lastMaxHp: Int = -1
    var lastNpcLine: String = ""
    val locZones: HashSet<Int> = HashSet()
    var blocking: Boolean = false
    var dodgeUntilTick: Int = 0
    var lastInvLine: String = ""
    var lastPlayersLine: String = ""
    var lastWornLine: String = ""
    var lastStatsLine: String = ""
    var lastGroundLine: String = ""
    var lastSpecEnergy: Int = -1
    var lastPrayer: Int = -1
    var lastMaxPrayer: Int = -1
    var lastRunEnergy: Int = -1
    var bankOpen: Boolean = false
    var lastBankLine: String = ""
    var lastBankNote: Int = -1
    var shopOpen: Boolean = false
    var lastShopLine: String = ""
    /** Last `SPELLS` line sent (spellbook bridge, see [LinkSpells]). */
    var lastSpellsLine: String = ""
    /** Last `MGOVERLAY` line sent (minigame HUD). */
    var lastOverlayLine: String = ""
    /** Last `PATCH` line sent per patch tile (packed coords). */
    val patchLines: HashMap<Long, String> = HashMap()
    /** Rooms of the POH the player is in (from HouseLayoutEvent), or null. */
    var houseRooms: List<org.rsmod.api.player.events.HouseRoomView>? = null
    var lastHouseLine: String = ""
    var lastHouseLocsLine: String = ""
    /** Whether the link player was frozen/stunned last tick (`FROZEN 0|1`). */
    var frozen: Boolean = false
    /** Last `PLOOK` sent for the link player itself. */
    var lastSelfLook: String = ""
    /** Last `PLOOK` sent per other player slot. */
    val lastLooks: HashMap<Int, String> = HashMap()
    var lastSelfBas: String = ""
    val lastBas: HashMap<Int, String> = HashMap()

    private val writer =
        PrintWriter(OutputStreamWriter(socket.getOutputStream(), StandardCharsets.UTF_8), true)
    private val closed = AtomicBoolean(false)

    val isClosed: Boolean
        get() = closed.get()

    fun send(line: String) {
        if (closed.get()) {
            return
        }
        synchronized(writer) { writer.print(line + "\n"); writer.flush() }
    }

    fun close() {
        if (!closed.compareAndSet(false, true)) {
            return
        }
        try {
            socket.close()
        } catch (_: Exception) {
        }
    }
}

sealed class LinkCommand {
    abstract val connection: LinkConnection

    data class Hello(override val connection: LinkConnection, val name: String) : LinkCommand()

    data class Position(override val connection: LinkConnection, val x: Int, val z: Int, val level: Int) :
        LinkCommand()

    data class OpLocU(
        override val connection: LinkConnection,
        val locId: Int,
        val x: Int,
        val z: Int,
        val level: Int,
        val slot: Int,
    ) : LinkCommand()

    data class OpLoc(
        override val connection: LinkConnection,
        val locId: Int,
        val x: Int,
        val z: Int,
        val level: Int,
        val op: Int,
    ) : LinkCommand()

    data class OpNpc(override val connection: LinkConnection, val index: Int, val op: Int) : LinkCommand()

    data class Appear(override val connection: LinkConnection, val values: List<Int>) : LinkCommand()
    data class Stop(override val connection: LinkConnection) : LinkCommand()

    data class Dodge(override val connection: LinkConnection) : LinkCommand()

    data class OpItem(override val connection: LinkConnection, val worn: Boolean, val slot: Int, val op: Int) :
        LinkCommand()

    data class Take(override val connection: LinkConnection, val id: Int, val x: Int, val z: Int, val level: Int) :
        LinkCommand()

    data class BankItem(
        override val connection: LinkConnection,
        val deposit: Boolean,
        val slot: Int,
        val id: Int,
        val count: Int,
    ) : LinkCommand()

    data class BankDepositAll(override val connection: LinkConnection, val worn: Boolean) : LinkCommand()

    data class BankNote(override val connection: LinkConnection, val note: Boolean) : LinkCommand()

    data class BankClose(override val connection: LinkConnection) : LinkCommand()

    /** `BUY slot id count` (shop slot) / `SELL slot id count` (backpack slot); count 1, 5, 10 or 50. */
    data class ShopItem(
        override val connection: LinkConnection,
        val sell: Boolean,
        val slot: Int,
        val id: Int,
        val count: Int,
    ) : LinkCommand()

    /** `SHOPVALUE side slot id`: side 0 = shop stock (Value), 1 = backpack (sell value). */
    data class ShopValue(override val connection: LinkConnection, val side: Boolean, val slot: Int, val id: Int) :
        LinkCommand()

    data class ShopClose(override val connection: LinkConnection) : LinkCommand()

    data class Cast(override val connection: LinkConnection, val spellObj: Int, val slot: Int?) : LinkCommand()

    data class DialogueContinue(override val connection: LinkConnection) : LinkCommand()

    data class DialogueSelect(override val connection: LinkConnection, val option: Int) : LinkCommand()

    data class Chat(override val connection: LinkConnection, val text: String) : LinkCommand()

    data class Special(override val connection: LinkConnection) : LinkCommand()

    data class Block(override val connection: LinkConnection, val blocking: Boolean) : LinkCommand()

    data class Bye(override val connection: LinkConnection) : LinkCommand()

    data class Disconnected(override val connection: LinkConnection) : LinkCommand()
}
