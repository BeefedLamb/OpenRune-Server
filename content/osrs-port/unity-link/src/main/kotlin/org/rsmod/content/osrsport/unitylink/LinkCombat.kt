package org.rsmod.content.osrsport.unitylink

import jakarta.inject.Inject
import org.rsmod.api.npc.hit.NpcDamageContributor
import org.rsmod.events.EventBus
import org.rsmod.events.UnboundEvent
import org.rsmod.game.entity.Npc
import org.rsmod.game.entity.Player
import org.rsmod.plugin.module.PluginModule

/**
 * A player's hit landed on an npc (published for every player hit, including 0s/misses). The
 * npc's hitpoints already have the damage taken off, so `npc.hitpoints == 0` means it is dying.
 *
 * NpcHitEvents.Impact is keyed by npc type (one subscriber per type), so the link listens through
 * the [NpcDamageContributor] set binding instead, which sees every player-sourced npc hit.
 */
data class LinkNpcHit(val npc: Npc, val source: Player, val damage: Int) : UnboundEvent

class LinkNpcDamageContributor @Inject constructor(private val eventBus: EventBus) : NpcDamageContributor {
    override fun onPlayerDamageNpc(npc: Npc, source: Player, damage: Int) {
        eventBus.publish(LinkNpcHit(npc, source, damage))
    }
}

class UnityLinkModule : PluginModule() {
    override fun bind() {
        addSetBinding<NpcDamageContributor>(LinkNpcDamageContributor::class.java)
    }
}
