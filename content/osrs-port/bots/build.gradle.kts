plugins {
    id("base-conventions")
}

dependencies {
    implementation(projects.api.pluginCommons)
    implementation(projects.api.realm)
    implementation(projects.api.registry)
    implementation(projects.api.npc)
    implementation(projects.content.osrsPort.unityLink)
    implementation(libs.jackson.databind)
}
