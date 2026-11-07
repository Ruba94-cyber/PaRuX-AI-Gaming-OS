package com.parux.android.ui.chat

import com.parux.android.core.network.ManagedChatModelInfo

/** Browse-only ordering. These are not model-routing commands.
 * A previous benchmark may not describe a changed GPU/runtime configuration. */
internal enum class MobileModelSort { Name, SmallestFile, MeasuredSpeed }

internal data class MobileModelPage(
    val visible: List<ManagedChatModelInfo>,
    val totalMatches: Int
)

/** Pure search and presentation policy shared by desktop-remote and mobile Chat. */
internal object MobileModelBrowsePolicy {
    private const val LIMIT = 20

    fun browse(
        models: List<ManagedChatModelInfo>, query: String,
        sort: MobileModelSort = MobileModelSort.Name,
        pinnedManualFile: String? = null
    ): MobileModelPage {
        val term = query.trim()
        val matching = models.asSequence()
            .filter { it.fileName.isNotBlank() }
            .distinctBy { it.fileName.lowercase() }
            .filter {
                term.isEmpty() ||
                    it.fileName.contains(term, ignoreCase = true) ||
                    (it.alias?.contains(term, ignoreCase = true) == true) ||
                    it.quantization.contains(term, ignoreCase = true)
            }
            .toList()
        fun nameKey(model: ManagedChatModelInfo) =
            (model.alias?.takeIf { it.isNotBlank() } ?: model.fileName).lowercase()
        val sorted = when (sort) {
            MobileModelSort.SmallestFile -> matching.sortedWith(
                compareBy<ManagedChatModelInfo> { it.sizeBytes <= 0 }
                    .thenBy { if (it.sizeBytes > 0) it.sizeBytes else Long.MAX_VALUE }
                    .thenBy { nameKey(it) }
                    .thenBy { it.fileName.lowercase() })
            MobileModelSort.MeasuredSpeed -> matching.sortedWith(
                compareBy<ManagedChatModelInfo> {
                    it.lastBenchmarkTokensPerSecond?.takeIf { speed -> speed.isFinite() && speed > 0 } == null
                }.thenByDescending {
                    it.lastBenchmarkTokensPerSecond?.takeIf { speed -> speed.isFinite() && speed > 0 } ?: 0.0
                }.thenBy { nameKey(it) }
                    .thenBy { it.fileName.lowercase() })
            MobileModelSort.Name -> matching.sortedWith(
                compareBy<ManagedChatModelInfo> { nameKey(it) }
                    .thenBy { it.fileName.lowercase() })
        }
        val visible = sorted.take(LIMIT).toMutableList()
        if (term.isEmpty() && !pinnedManualFile.isNullOrBlank()) {
            val pinned = matching.firstOrNull {
                it.fileName.equals(pinnedManualFile, ignoreCase = true)
            }
            if (pinned != null && visible.none {
                it.fileName.equals(pinned.fileName, ignoreCase = true)
            }) {
                if (visible.size == LIMIT) visible.removeAt(visible.lastIndex)
                visible.add(0, pinned)
            }
        }
        return MobileModelPage(visible, matching.size)
    }
}
