plugins {
    id("base-conventions")
}

dependencies {
    implementation(projects.api.pluginCommons)
    implementation(projects.api.account)
    implementation(projects.api.realm)
    implementation(projects.api.registry)
    implementation(projects.api.specials)
    implementation(libs.rsprot.api)
    implementation(libs.openrune.central.common)
}
