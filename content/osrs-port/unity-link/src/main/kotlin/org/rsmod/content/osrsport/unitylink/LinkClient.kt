package org.rsmod.content.osrsport.unitylink

import dev.openrune.rscm.RSCM
import dev.openrune.rscm.RSCMType
import dev.openrune.rscm.RSCM.asRSCM
import net.rsprot.protocol.game.outgoing.interfaces.IfCloseSub
import net.rsprot.protocol.game.outgoing.interfaces.IfOpenSub
import net.rsprot.protocol.game.outgoing.interfaces.IfSetNpcHead
import net.rsprot.protocol.game.outgoing.interfaces.IfSetText
import net.rsprot.protocol.game.outgoing.misc.player.RunClientScript
import net.rsprot.protocol.game.outgoing.misc.player.MessageGame
import net.rsprot.protocol.game.outgoing.sound.SynthSound
import org.rsmod.game.client.Client
import org.rsmod.game.entity.Player
import org.rsmod.game.ui.Component

/**
 * The link player's "network client". The server writes rsprot messages to it as it would to a
 * real client; the few the Unity client cares about are forwarded as text lines.
 *
 * Chatbox dialogues are rebuilt from the interface packets the dialogue helpers send and emitted
 * once per tick as `DLG kind|title|text|continue|npcId|option1|option2...` (`DLG close` when the
 * chat modal closes). Kinds: `npc`, `player`, `mes`, `obj`, `menu`, `levelup`, `other`.
 */
class LinkClient(private val connection: LinkConnection) : Client<Any, Any> {
    private var dialogueKind: String? = null
    private var dialogueTitle = ""
    private var dialogueText = ""
    private var dialogueContinue = ""
    private var dialogueNpc = -1
    private var dialogueOptions: List<String> = emptyList()
    private var dialogueDirty = false
    private var dialogueClosed = false

    override fun write(message: Any) {
        when (message) {
            is MessageGame -> connection.send("MSG ${message.type} ${message.message.replace('\n', ' ')}")
            is IfOpenSub -> onOpenSub(message)
            is IfCloseSub -> onCloseSub(message)
            is IfSetText -> onSetText(message)
            is IfSetNpcHead -> {
                dialogueNpc = message.npc
                dialogueDirty = true
            }
            is RunClientScript -> onClientScript(message)
            is SynthSound -> connection.send("SND ${message.id} ${message.loops} ${message.delay}")
        }
    }

    private fun onOpenSub(message: IfOpenSub) {
        if (Component(message.destinationInterfaceId, message.destinationComponentId) != CHAT_MODAL) {
            return
        }
        val name = RSCM.getReverseMapping(RSCMType.INTERFACE, message.interfaceId)
        dialogueKind = DIALOGUE_KINDS[name] ?: "other"
        dialogueTitle = ""
        dialogueText = ""
        dialogueContinue = ""
        dialogueNpc = -1
        dialogueOptions = emptyList()
        dialogueClosed = false
        dialogueDirty = true
    }

    private fun onCloseSub(message: IfCloseSub) {
        if (Component(message.interfaceId, message.componentId) == CHAT_MODAL) {
            dialogueKind = null
            dialogueClosed = true
            dialogueDirty = true
        }
    }

    private fun onSetText(message: IfSetText) {
        if (dialogueKind == null) {
            return
        }
        val name = RSCM.getReverseMapping(RSCMType.COMPONENT, Component(message.interfaceId, message.componentId).packed)
        when (name.substringAfter(':', "")) {
            "name", "text1" -> dialogueTitle = message.text
            "text", "text2" -> dialogueText = message.text
            "continue", "pausebutton" -> dialogueContinue = message.text
            else -> return
        }
        dialogueDirty = true
    }

    private fun onClientScript(message: RunClientScript) {
        if (message.id != CHATBOX_MULTI_INIT || message.values.size < 2) {
            return
        }
        dialogueKind = "menu"
        dialogueTitle = message.values[0].toString()
        dialogueOptions = message.values[1].toString().split('|').filter { it.isNotEmpty() }
        dialogueClosed = false
        dialogueDirty = true
    }

    override fun read(player: Player) {}

    override fun flush() {
        if (!dialogueDirty) {
            return
        }
        dialogueDirty = false
        val kind = dialogueKind
        if (kind == null) {
            if (dialogueClosed) {
                connection.send("DLG close")
                dialogueClosed = false
            }
            return
        }
        val parts =
            listOf(kind, dialogueTitle, dialogueText, dialogueContinue, dialogueNpc.toString()) + dialogueOptions
        connection.send("DLG " + parts.joinToString("|") { it.replace('|', '/').replace('\n', ' ') })
    }

    override fun flushHighPriority() {}

    override fun close() {}

    override fun unregister(service: Any, player: Player) {}

    private companion object {
        const val CHATBOX_MULTI_INIT = 58

        val CHAT_MODAL: Component by lazy {
            Component("component.chatbox:chatmodal".asRSCM(RSCMType.COMPONENT))
        }

        val DIALOGUE_KINDS =
            mapOf(
                "interface.chat_left" to "npc",
                "interface.chat_right" to "player",
                "interface.messagebox" to "mes",
                "interface.objectbox" to "obj",
                "interface.objectbox_double" to "obj",
                "interface.chatmenu" to "menu",
                "interface.levelup_display" to "levelup",
            )
    }
}
