package org.rsmod.content.osrsport.core

import com.github.michaelbull.logging.InlineLogger
import jakarta.inject.Inject
import org.rsmod.api.player.output.mes
import org.rsmod.api.script.onCommand
import org.rsmod.plugin.scripts.PluginScript
import org.rsmod.plugin.scripts.ScriptContext

class OsrsPortScript @Inject constructor() : PluginScript() {
    private val logger = InlineLogger()

    override fun ScriptContext.startup() {
        logger.info { "osrs-port content loaded." }
        onCommand("port") {
            desc = "Confirms the osrs-port content modules are loaded."
            cheat { player.mes("osrs-port is loaded.") }
        }
    }
}
