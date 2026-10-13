package org.rsmod.content.osrsport.unitylink

import org.rsmod.events.UnboundEvent
import org.rsmod.game.entity.Player

/** Public chat said by a headless player (a bot); the Unity link relays it to connected clients. */
data class HeadlessChat(val player: Player, val text: String) : UnboundEvent

/** Public chat typed by a Unity player; bots listen for it so they can answer. */
data class LinkPlayerSaid(val player: Player, val text: String) : UnboundEvent
