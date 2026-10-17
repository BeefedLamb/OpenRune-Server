package org.rsmod.api.player.events

import org.rsmod.events.UnboundEvent
import org.rsmod.game.entity.Player

/**
 * The player-owned house instance [player] is in. Published by the Construction script on enter,
 * room build/removal (instance rebuilt) and leave. An empty [rooms] list means "not in a house".
 */
public data class HouseLayoutEvent(
    public val player: Player,
    public val rooms: List<HouseRoomView>,
) : UnboundEvent

/**
 * One room: the 8x8 template chunk whose south-west tile is ([srcX], [srcZ]) (level 0) is copied
 * to the instance chunk at ([dstX], [dstZ], [level]) rotated [rotation] clockwise quarter-turns
 * (engine RegionRotations convention: tile (x, z) -> (z, 7 - x) for rotation 1).
 */
public data class HouseRoomView(
    public val dstX: Int,
    public val dstZ: Int,
    public val level: Int,
    public val srcX: Int,
    public val srcZ: Int,
    public val rotation: Int,
    public val name: String,
)
