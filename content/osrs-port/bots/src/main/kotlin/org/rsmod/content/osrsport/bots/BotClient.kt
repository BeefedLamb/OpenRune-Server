package org.rsmod.content.osrsport.bots

import net.rsprot.protocol.game.outgoing.misc.player.MessageGame
import org.rsmod.game.client.Client
import org.rsmod.game.entity.Player

/** A bot's "network client": drops everything except game messages, which `::botinfo` shows. */
class BotClient : Client<Any, Any> {
    @Volatile var lastMessage: String = ""

    override fun write(message: Any) {
        if (message is MessageGame) {
            lastMessage = message.message
        }
    }

    override fun read(player: Player) {}

    override fun flush() {}

    override fun flushHighPriority() {}

    override fun close() {}

    override fun unregister(service: Any, player: Player) {}
}
