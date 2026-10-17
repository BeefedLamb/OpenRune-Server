package org.rsmod.content.osrsport.unitylink

import dev.openrune.ServerCacheManager
import dev.openrune.rscm.RSCM.asRSCM
import org.rsmod.api.config.refs.params
import org.rsmod.api.player.righthand
import org.rsmod.game.entity.Player
import org.rsmod.game.type.getInvObj

/**
 * A player's base animation set (stand / walk / run sequences) for the Unity client, resolved
 * exactly like the OSRS appearance block (RspCycle): explicit bas, else transmog npc, else the
 * wielded weapon's bas_* params, else bas.human_default.
 *
 * Line: `BAS key ready turn walkF walkB walkL walkR run` (seq ids, -1 = none); key as in `PLOOK`.
 */
object LinkBas {
    fun line(key: String, player: Player): String {
        val s = resolve(player)
        return "BAS $key ${s.joinToString(" ")}"
    }

    private fun resolve(player: Player): IntArray {
        val bas = player.appearance.bas
        if (bas != null) {
            return intArrayOf(bas.readyAnim, bas.turnOnSpot, bas.walkForward, bas.walkBack,
                bas.walkLeft, bas.walkRight, bas.running)
        }
        val transmog = player.transmog
        if (transmog != null) {
            return intArrayOf(transmog.standAnim, transmog.rotateBackAnim, transmog.walkAnim, transmog.walkAnim,
                transmog.walkLeftAnim, transmog.walkRightAnim, transmog.runSequence)
        }
        val weapon = player.righthand
        if (weapon != null) {
            val type = getInvObj(weapon)
            return intArrayOf(
                type.param(params.bas_readyanim).id, type.param(params.bas_turnonspot).id,
                type.param(params.bas_walk_f).id, type.param(params.bas_walk_b).id,
                type.param(params.bas_walk_l).id, type.param(params.bas_walk_r).id,
                type.param(params.bas_running).id,
            )
        }
        val d = ServerCacheManager.getBas("bas.human_default".asRSCM()) ?: return IntArray(7) { -1 }
        return intArrayOf(d.readyAnim, d.turnOnSpot, d.walkForward, d.walkBack, d.walkLeft, d.walkRight, d.running)
    }
}
