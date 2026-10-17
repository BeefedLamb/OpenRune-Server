package org.rsmod.content.osrsport.unitylink

import dev.openrune.ServerCacheManager
import dev.openrune.rscm.RSCM.asRSCM
import dev.openrune.rscm.RSCMType
import dev.openrune.types.aconverted.interf.IfButtonOp
import org.rsmod.annotations.InternalApi
import org.rsmod.api.player.protect.ProtectedAccessLauncher
import org.rsmod.api.player.ui.IfModalButton
import org.rsmod.api.player.ui.ifClose
import org.rsmod.api.player.vars.boolVarBit
import org.rsmod.api.player.vars.intVarBit
import org.rsmod.events.EventBus
import org.rsmod.game.entity.Player

/**
 * Bank bridge for the Unity link. The bank itself is the server's real one: a bank booth (OPLOC
 * op "Bank") or banker (OPNPC op "Bank") opens `interface.bankmain`/`bankside` exactly as for a
 * normal client, and every deposit/withdraw is replayed as the matching bank interface button
 * ([IfModalButton]), so BankInvScript applies all its rules (stacking, capacity, notes,
 * placeholders, tabs, unbankable items) and the bank inventory saves with the account.
 *
 * Server -> client: `BANKOPEN` / `BANKCLOSE` when the bank modal opens/closes, `BANK
 * slot|id|count|name|ops;...` (same shape as `INV`) while open, re-sent on change, and
 * `BANKNOTE 0|1` (withdraw-as-note mode).
 * Client -> server: `WITHDRAW slot id count`, `DEPOSIT slot id count` (count 1, 5, 10, -1 = all,
 * or any other positive amount = "X"), `DEPOSITALL` (whole backpack), `DEPOSITWORN` (equipment),
 * `BANKNOTE 0|1`, `BANKCLOSE`.
 */
class LinkBank(private val eventBus: EventBus, private val protectedAccess: ProtectedAccessLauncher) {
    private var Player.lastQtyInput by intVarBit("varbit.bank_requestedquantity")
    private var Player.withdrawNotes by boolVarBit("varbit.bank_withdrawnotes")

    private val Player.bank
        get() = invMap.getOrPut("inv.bank")

    fun isOpen(player: Player): Boolean = player.ui.containsModal(BANK_MAIN)

    /** Per-tick sync: open/close transitions and bank contents while open. */
    fun sync(connection: LinkConnection, player: Player, line: (String, org.rsmod.game.inv.Inventory) -> String) {
        val open = isOpen(player)
        if (open != connection.bankOpen) {
            connection.bankOpen = open
            connection.lastBankLine = ""
            connection.lastBankNote = -1
            connection.send(if (open) "BANKOPEN" else "BANKCLOSE")
        }
        if (!open) {
            return
        }
        val note = if (player.withdrawNotes) 1 else 0
        if (note != connection.lastBankNote) {
            connection.lastBankNote = note
            connection.send("BANKNOTE $note")
        }
        val bankLine = line("BANK", player.bank)
        if (bankLine != connection.lastBankLine) {
            connection.lastBankLine = bankLine
            connection.send(bankLine)
        }
    }

    fun withdraw(connection: LinkConnection, player: Player, slot: Int, id: Int, count: Int) {
        if (!ready(connection, player)) return
        val bank = player.bank
        if (slot !in 0 until bank.size || bank[slot]?.id != id) {
            connection.lastBankLine = "" // client is stale; resend
            return
        }
        val op =
            when (count) {
                1 -> IfButtonOp.Op2
                5 -> IfButtonOp.Op3
                10 -> IfButtonOp.Op4
                ALL -> IfButtonOp.Op7
                else -> {
                    if (count <= 0) return
                    player.lastQtyInput = count
                    IfButtonOp.Op5 // "Withdraw-<last X>"
                }
            }
        press(player, "component.bankmain:items", slot, op)
    }

    fun deposit(connection: LinkConnection, player: Player, slot: Int, id: Int, count: Int) {
        if (!ready(connection, player)) return
        val inv = player.inv
        if (slot !in 0 until inv.size || inv[slot]?.id != id) {
            connection.lastInvLine = ""
            return
        }
        val op =
            when (count) {
                1 -> IfButtonOp.Op3
                5 -> IfButtonOp.Op4
                10 -> IfButtonOp.Op5
                ALL -> IfButtonOp.Op8
                else -> {
                    if (count <= 0) return
                    player.lastQtyInput = count
                    IfButtonOp.Op6 // "Deposit-<last X>"
                }
            }
        press(player, "component.bankside:items", slot, op)
    }

    fun depositAll(connection: LinkConnection, player: Player, worn: Boolean) {
        if (!ready(connection, player)) return
        press(player, if (worn) "component.bankmain:depositworn" else "component.bankmain:depositinv", -1, IfButtonOp.Op1)
    }

    fun setNoteMode(connection: LinkConnection, player: Player, note: Boolean) {
        if (!isOpen(player)) return
        player.withdrawNotes = note
    }

    fun close(player: Player) {
        if (isOpen(player)) {
            player.ifClose(eventBus)
        }
    }

    @OptIn(InternalApi::class)
    private fun ready(connection: LinkConnection, player: Player): Boolean {
        if (!isOpen(player)) {
            connection.send("BANKCLOSE")
            connection.bankOpen = false
            return false
        }
        return !player.isModalButtonProtected
    }

    /** Fires the bank interface button the way If3ButtonHandler does for a real client click. */
    @OptIn(InternalApi::class)
    private fun press(player: Player, component: String, comsub: Int, op: IfButtonOp) {
        val type = ServerCacheManager.fromComponent(component.asRSCM(RSCMType.COMPONENT))
        val event = IfModalButton(type, comsub, null, op)
        protectedAccess.launchLenient(player) { eventBus.publish(this, event) }
    }

    companion object {
        /** Count sentinel meaning "all" in WITHDRAW/DEPOSIT. */
        const val ALL = -1

        const val BANK_MAIN = "interface.bankmain"
    }
}