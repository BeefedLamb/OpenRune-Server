plugins {
    id("base-conventions")
}

dependencies {
    implementation(projects.api.pluginCommons)
    implementation(projects.api.account)
    implementation(projects.api.attr)
    implementation(projects.api.realm)
    implementation(projects.api.registry)
    implementation(projects.api.specials)
    implementation(projects.api.shops)
    implementation(projects.api.spells)
    implementation(projects.api.spellsRunes)
    implementation(libs.rsprot.api)
    implementation(libs.openrune.central.common)
}
