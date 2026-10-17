package org.rsmod.content.osrsport.unitylink

import org.rsmod.api.player.events.FarmingPatchEvent
import org.rsmod.api.player.events.HouseLayoutEvent
import org.rsmod.api.registry.loc.LocRegistry
import org.rsmod.game.entity.Player
import org.rsmod.map.CoordGrid
import org.rsmod.map.zone.ZoneKey

/**
 * Geometry bridges: farming patch variants and the player-owned house instance.
 *
 * `PATCH x z level variantLocId baseLocId value shape rot` - the farming patch whose base multiloc
 * [baseLocId] has its south-west tile at x,z,level currently shows [variantLocId] (crop + growth
 * stage; -1 = nothing visible). value is the transmit-varbit value. Ops must still target the
 * base id (`OPLOC baseLocId x z level op` / `OPLOCU`). Sent per patch only when it changes.
 *
 * `HOUSE level;dstX|dstZ|srcX|srcZ|rot|name;...` - the POH the player is in: each room copies the
 * 8x8 template chunk at srcX,srcZ (plane 0, exported map square) to dstX,dstZ rotated rot clockwise
 * quarter-turns (tile x,z -> z,7-x for rot 1). Bare `HOUSE` = left the house (clear it).
 * `HLOCS id|x|z|level|shape|rot;...` - every loc inside the house rooms (walls, hotspots, built
 * furniture), already rotated/positioned by the server; re-sent whole when it changes.
 */
internal class LinkGeometry(private val locRegistry: LocRegistry) {

    fun patches(connection: LinkConnection, event: FarmingPatchEvent) {
        for (p in event.patches) {
            val key = CoordGrid(p.x, p.z, p.level).packed.toLong()
            val line = "PATCH ${p.x} ${p.z} ${p.level} ${p.variantLocId} ${p.baseLocId} ${p.value} ${p.shape} ${p.angle}"
            if (connection.patchLines[key] != line) {
                connection.patchLines[key] = line
                connection.send(line)
            }
        }
    }

    fun house(connection: LinkConnection, event: HouseLayoutEvent) {
        connection.houseRooms = event.rooms.takeIf { it.isNotEmpty() }
        val line =
            if (event.rooms.isEmpty()) "HOUSE"
            else buildString {
                append("HOUSE ").append(event.rooms.first().level)
                for (r in event.rooms) {
                    append(';').append(r.dstX).append('|').append(r.dstZ).append('|').append(r.srcX).append('|')
                        .append(r.srcZ).append('|').append(r.rotation).append('|').append(r.name.replace('|', '/').replace(';', ','))
                }
            }
        connection.lastHouseLocsLine = ""
        connection.lastHouseLine = line
        connection.send(line)
    }

    /** Per tick: re-sends `HLOCS` on change; clears the house if the player teleported out. */
    fun sync(connection: LinkConnection, player: Player) {
        val rooms = connection.houseRooms ?: return
        val c = player.coords
        val inside = rooms.any { c.x >= it.dstX - HOUSE_MARGIN && c.x < it.dstX + 8 + HOUSE_MARGIN &&
            c.z >= it.dstZ - HOUSE_MARGIN && c.z < it.dstZ + 8 + HOUSE_MARGIN }
        if (!inside) {
            connection.houseRooms = null
            connection.lastHouseLine = "HOUSE"
            connection.lastHouseLocsLine = ""
            connection.send("HOUSE")
            return
        }
        val sb = StringBuilder("HLOCS ")
        for (r in rooms) {
            val zone = ZoneKey.from(CoordGrid(r.dstX, r.dstZ, r.level))
            for (loc in locRegistry.findAll(zone)) {
                if (loc.id == LocRegistry.DELETED_LOC_ID) continue
                val lc = loc.coords
                sb.append(loc.id).append('|').append(lc.x).append('|').append(lc.z).append('|').append(lc.level)
                    .append('|').append(loc.shapeId).append('|').append(loc.angleId).append(';')
            }
        }
        val line = sb.toString()
        if (line != connection.lastHouseLocsLine) {
            connection.lastHouseLocsLine = line
            connection.send(line)
        }
    }

    private companion object {
        const val HOUSE_MARGIN = 16
    }
}
