package org.rsmod.content.osrsport.unitylink

import dev.openrune.rscm.RSCM
import dev.openrune.rscm.RSCM.asRSCM
import dev.openrune.rscm.RSCMType
import org.rsmod.api.player.input.ResumePauseButtonInput
import org.rsmod.game.entity.Player
import org.rsmod.game.ui.Component

/** Answers chatbox dialogues for players that have no OSRS client (Unity players and bots). */
object HeadlessDialogue {
    private const val CHAT_MODAL = "component.chatbox:chatmodal"

    private val PAUSE_COMPONENTS =
        mapOf(
            "interface.chat_left" to "component.chat_left:continue",
            "interface.chat_right" to "component.chat_right:continue",
            "interface.messagebox" to "component.messagebox:continue",
            "interface.objectbox" to "component.objectbox:universe",
            "interface.objectbox_double" to "component.objectbox_double:pausebutton",
            "interface.levelup_display" to "component.levelup_display:continue",
            "interface.chatmenu" to "component.chatmenu:options",
        )

    fun isOpen(player: Player): Boolean = player.ui.getModalOrNull(CHAT_MODAL) != null

    /**
     * Plays the chatbox modal's pause button, like a click on "Click here to continue";
     * [option] picks the line of an option menu. Returns false when no dialogue is open.
     */
    fun resume(player: Player, option: Int): Boolean {
        val interfaceId = player.ui.getModalOrNull(CHAT_MODAL)?.packed ?: return false
        val name = RSCM.getReverseMapping(RSCMType.INTERFACE, interfaceId)
        val pauseComponent = PAUSE_COMPONENTS[name] ?: return false
        val sub = if (name == "interface.chatmenu") option else -1
        player.ui.queueClose(Component(CHAT_MODAL.asRSCM(RSCMType.COMPONENT)))
        player.resumeActiveCoroutine(ResumePauseButtonInput(pauseComponent, sub))
        return true
    }
}
