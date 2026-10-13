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

/**
 * Line-based TCP listener for the Unity client. Socket threads only parse lines into [inbox];
 * everything that touches game state happens on the game thread when the script drains it.
 *
 * Client -> server: `HELLO <name>`, `POS <x> <z> <level>`, `BYE`.
 * Server -> client: `WELCOME <x> <z> <level>`, `TILE <x> <z> <level>`, `ERROR <text>`.
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
    var lastSentX: Int = Int.MIN_VALUE
    var lastSentZ: Int = Int.MIN_VALUE
    var lastSentLevel: Int = Int.MIN_VALUE

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

    data class Bye(override val connection: LinkConnection) : LinkCommand()

    data class Disconnected(override val connection: LinkConnection) : LinkCommand()
}
