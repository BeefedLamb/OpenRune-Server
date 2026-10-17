package org.rsmod.content.skills.woodcutting.scripts

import org.rsmod.api.player.protect.ProtectedAccess
import org.rsmod.api.script.onOpLoc1
import org.rsmod.plugin.scripts.PluginScript
import org.rsmod.plugin.scripts.ScriptContext

/**
 * Woodcutting Guild redwood rope ladders. The redwood trees themselves (loc.redwoodtree_l/_r) are
 * already choppable through the generic Woodcutting script via cache params (level 90, 380 XP,
 * obj.redwood_logs); these locs had no handler, so the platform was unreachable.
 *
 * Destination = same tile, one level up/down (same convention as the generic LadderScript).
 * UNVERIFIED: whether the OSRS rope ladders offset the player a tile when arriving.
 */
class RedwoodLadderScript : PluginScript() {
    override fun ScriptContext.startup() {
        onOpLoc1("loc.wcguild_redwood_ropebottom") { climb(1, "seq.human_reachforladder") }
        onOpLoc1("loc.wcguild_redwood_ropetop") { climb(-1, "seq.human_reachforladdertop") }
    }

    private suspend fun ProtectedAccess.climb(translateLevel: Int, seq: String) {
        arriveDelay()
        anim(seq)
        delay(1)
        telejump(player.coords.translateLevel(translateLevel))
    }
}
