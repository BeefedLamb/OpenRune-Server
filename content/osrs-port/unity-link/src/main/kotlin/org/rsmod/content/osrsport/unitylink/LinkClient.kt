package org.rsmod.content.osrsport.unitylink

import net.rsprot.protocol.game.outgoing.misc.player.MessageGame
import org.rsmod.game.client.Client
import org.rsmod.game.entity.Player

/**
 * The link player's "network client". The server writes rsprot messages to it as it would to a
 * real client; the few the Unity client cares about are forwarded as text lines.
 */
class LinkClient(private val connection: LinkConnection) : Client<Any, Any> {
    override fun write(message: Any) {
        if (message is MessageGame) {
            connection.send("MSG ${message.type} ${message.message.replace('\n', ' ')}")
        }
    }

    override fun read(player: Player) {}

    override fun flush() {}

    override fun flushHighPriority() {}

    override fun close() {}

    override fun unregister(service: Any, player: Player) {}
}
