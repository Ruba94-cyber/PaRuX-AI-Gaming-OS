package com.parux.android.ui.jobs

import com.parux.android.core.network.MediaJobStatus

/**
 * Pure completion notification selection, separate from Android's notification service.
 * Initial sync is a historical snapshot, not a flood of new notifications.
 * Only newly completed jobs from subsequent successful polls can notify.
 */
internal class JobCompletionNotificationPolicy {
    private var initialized = false
    private val seen = LinkedHashSet<String>()

    fun newlyCompleted(jobs: List<MediaJobStatus>): List<MediaJobStatus> {
        val complete = jobs.filter { it.status == "completed" }
        val fresh = if (initialized) complete.filter { it.id !in seen } else emptyList()
        complete.forEach { seen.add(it.id) }
        initialized = true

        // Backend retains a bounded set of terminal media jobs, but keep a
        // cap for device memory even if that server policy changes.
        while (seen.size > 1024) seen.remove(seen.first())
        return fresh
    }
}
