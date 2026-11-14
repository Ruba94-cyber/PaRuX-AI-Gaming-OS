package com.parux.android

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Italian resources must stay in step with the default strings, and screens that were moved to resources must not
 * grow hard-coded user-visible text again. Runs on the JVM from the module directory (Gradle's working directory).
 */
class LocalizationResourcesTest {
    private val res = File("src/main/res")
    private val source = File("src/main/java/com/parux/android")

    private data class Entry(val name: String, val value: String, val translatable: Boolean)

    private fun read(file: File): List<Entry> {
        val text = file.readText()
        val tag = Regex("""<string\s+([^>]*)>(.*?)</string>""", RegexOption.DOT_MATCHES_ALL)
        return tag.findAll(text).map { m ->
            val attrs = m.groupValues[1]
            val name = Regex("""name="([^"]+)"""").find(attrs)!!.groupValues[1]
            Entry(name, m.groupValues[2].trim(), !attrs.contains("""translatable="false""""))
        }.toList()
    }

    private val defaults get() = read(File(res, "values/strings.xml"))

    private fun localeFiles(): List<File> =
        (res.listFiles { f -> f.isDirectory && f.name.startsWith("values-") } ?: emptyArray())
            .map { File(it, "strings.xml") }.filter { it.exists() }

    @Test
    fun italianResourcesExist() {
        assertTrue("values-it/strings.xml must exist", File(res, "values-it/strings.xml").exists())
    }

    @Test
    fun everyTranslatableDefaultStringHasATranslationInEveryLocale() {
        val needed = defaults.filter { it.translatable }.map { it.name }.toSet()
        for (file in localeFiles()) {
            val have = read(file).map { it.name }.toSet()
            val missing = (needed - have).sorted()
            assertTrue("${file.parentFile.name} is missing translations for: $missing", missing.isEmpty())
        }
    }

    @Test
    fun noLocaleDefinesAKeyThatDoesNotExistInTheDefaults() {
        val all = defaults.map { it.name }.toSet()
        for (file in localeFiles()) {
            val extra = (read(file).map { it.name }.toSet() - all).sorted()
            assertTrue("${file.parentFile.name} has keys without a default string: $extra", extra.isEmpty())
        }
    }

    @Test
    fun translationsKeepTheFormatPlaceholdersOfTheDefault() {
        val placeholder = Regex("""%(\d+\$)?[sdf]""")
        val byName = defaults.associateBy { it.name }
        for (file in localeFiles()) {
            for (entry in read(file)) {
                val original = byName[entry.name] ?: continue
                assertEquals(
                    "${file.parentFile.name}/${entry.name}: placeholders differ",
                    placeholder.findAll(original.value).map { it.value }.sorted().toList(),
                    placeholder.findAll(entry.value).map { it.value }.sorted().toList()
                )
            }
        }
    }

    @Test
    fun translationsAreNotEmptyAndDifferFromTheDefaultsExceptWhenExplicitlySame() {
        // These read the same in both languages (brand names, single loan words).
        val sameInBoth = setOf("settings_auto")
        val byName = defaults.associateBy { it.name }
        for (file in localeFiles()) {
            for (entry in read(file)) {
                assertTrue("${entry.name} is empty", entry.value.isNotBlank())
                val original = byName[entry.name] ?: continue
                if (entry.name !in sameInBoth) {
                    assertTrue("${file.parentFile.name}/${entry.name} is still the English text", entry.value != original.value)
                }
            }
        }
    }

    /**
     * Screens that already read their text from resources. A hard-coded literal in a Text(...), contentDescription or title there
     * fails the build; the remaining screens are tracked by a count that may only go down.
     */
    private val migrated = setOf("ui/settings/SettingsScreen.kt")

    private val remainingHardCoded = mapOf(
        // file (relative to the app package) to the number of Compose text literals not yet moved to resources
        "ui/tools/ToolsScreen.kt" to 81,
        "ui/studio/PcStudioScreen.kt" to 48,
        "ui/chat/ChatScreen.kt" to 34,
        "ui/pairing/PairingScreen.kt" to 13,
        "ui/goals/GoalsScreen.kt" to 12,
        "ui/jobs/JobsScreen.kt" to 11,
        "ui/media/MediaScreen.kt" to 9,
        "ui/studio/ProjectKnowledgeSection.kt" to 6,
        "ui/chat/ChatViewModel.kt" to 1,
        "ui/jobs/JobsViewModel.kt" to 1,
    )

    private val composeText = Regex(
        """(?:Text\(\s*|text\s*=\s*|contentDescription\s*=\s*|title\s*=\s*\{?\s*Text\(\s*|label\s*=\s*\{\s*Text\(\s*)"((?:[^"\\]|\\.)*)""""
    )

    private fun literalCount(file: File): Int = file.readLines().sumOf { line ->
        composeText.findAll(line).count { Regex("[A-Za-z]{3,}").containsMatchIn(it.groupValues[1]) }
    }

    @Test
    fun migratedScreensHaveNoHardCodedUserVisibleText() {
        for (relative in migrated) {
            val count = literalCount(File(source, relative))
            assertEquals("$relative still has hard-coded Compose text; use stringResource", 0, count)
        }
    }

    @Test
    fun screensNotYetMigratedDoNotGrowNewHardCodedText() {
        val grown = remainingHardCoded.filter { (relative, allowed) -> literalCount(File(source, relative)) > allowed }
            .mapValues { (relative, allowed) -> "now ${literalCount(File(source, relative))}, allowed $allowed" }
        assertTrue("New hard-coded Compose text (use stringResource): $grown", grown.isEmpty())
        // Any other screen file is held to zero.
        val others = source.walkTopDown().filter { it.isFile && it.extension == "kt" && it.path.replace('\\', '/').contains("/ui/") }
            .map { it.relativeTo(source).path.replace('\\', '/') }
            .filter { it !in remainingHardCoded && it !in migrated && literalCount(File(source, it)) > 0 }
            .map { it + " (" + literalCount(File(source, it)) + ")" }
            .toList()
        assertTrue("Hard-coded Compose text in files outside the tracked list: $others", others.isEmpty())
    }
}
