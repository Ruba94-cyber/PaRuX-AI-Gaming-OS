package com.parux.android.ui.chat

import com.parux.android.core.network.ManagedChatModelInfo
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class MobileModelBrowsePolicyTest {
    private fun model(index: Int, alias: String? = null) = ManagedChatModelInfo(
        fileName = "local-${index.toString().padStart(3, '0')}.gguf",
        alias = alias,
        sizeBytes = index * 1_000_000L,
        quantization = if (index % 2 == 0) "Q4_K_M" else "Q8_0"
    )

    @Test
    fun one_hundred_models_stay_accessible_without_hundred_item_compose_menu() {
        val models = (0 until 125).map { model(it) }
        val first = MobileModelBrowsePolicy.browse(models, "")
        assertEquals(125, first.totalMatches)
        assertEquals(20, first.visible.size)
        assertEquals("local-000.gguf", first.visible.first().fileName)

        val target = MobileModelBrowsePolicy.browse(models, "LOCAL-124")
        assertEquals(1, target.totalMatches)
        assertEquals("local-124.gguf", target.visible.single().fileName)

        val pinned = MobileModelBrowsePolicy.browse(
            models, "", MobileModelSort.Name, "LOCAL-124.GGUF")
        assertEquals(20, pinned.visible.size)
        assertEquals("local-124.gguf", pinned.visible.first().fileName)
    }

    @Test
    fun alias_quantization_and_unicode_are_searchable_without_duplicate_files() {
        val models = listOf(
            model(1, "Analisi qualità"),
            model(2, "Work"),
            model(2, "Duplicated alias")
        )
        val alias = MobileModelBrowsePolicy.browse(models, "QUALITÀ")
        assertEquals("local-001.gguf", alias.visible.single().fileName)
        val quantization = MobileModelBrowsePolicy.browse(models, "q4_k_m")
        assertEquals("local-002.gguf", quantization.visible.single().fileName)
        assertEquals(2, MobileModelBrowsePolicy.browse(models, "").totalMatches)
        assertTrue(MobileModelBrowsePolicy.browse(models, "does not exist").visible.isEmpty())
    }

    @Test
    fun small_first_never_guesses_unknown_model_sizes() {
        val models = listOf(
            ManagedChatModelInfo(fileName = "large.gguf", sizeBytes = 8_000_000_000L),
            ManagedChatModelInfo(fileName = "unknown.gguf"),
            ManagedChatModelInfo(fileName = "small.gguf", sizeBytes = 600_000_000L)
        )
        val result = MobileModelBrowsePolicy.browse(models, "", MobileModelSort.SmallestFile)
        assertEquals(listOf("small.gguf", "large.gguf", "unknown.gguf"),
            result.visible.map { it.fileName })
    }

    @Test
    fun only_positive_finite_historical_benchmarks_rank_as_measured() {
        val models = listOf(
            ManagedChatModelInfo(fileName = "unknown.gguf"),
            ManagedChatModelInfo(fileName = "slow.gguf", lastBenchmarkTokensPerSecond = 8.0),
            ManagedChatModelInfo(fileName = "fast.gguf", lastBenchmarkTokensPerSecond = 44.0),
            ManagedChatModelInfo(fileName = "bad.gguf", lastBenchmarkTokensPerSecond = Double.NaN)
        )
        val result = MobileModelBrowsePolicy.browse(models, "", MobileModelSort.MeasuredSpeed)
        assertEquals(listOf("fast.gguf", "slow.gguf", "bad.gguf", "unknown.gguf"),
            result.visible.map { it.fileName })
        assertFalse(result.visible.any { it.fileName.isBlank() })
    }
}
