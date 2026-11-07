package com.parux.android.ui.jobs

import com.parux.android.core.network.MediaJobStatus
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class JobCompletionNotificationPolicyTest {
    private fun job(id: String, status: String = "completed") = MediaJobStatus(
        id = id,
        type = "image",
        status = status,
        stage = status,
        progressPercent = 100,
        createdUtc = "2026-11-07T06:00:00Z",
        updatedUtc = "2026-11-07T06:01:00Z"
    )

    @Test
    fun first_sync_does_not_notify_for_old_completed_jobs() {
        val policy = JobCompletionNotificationPolicy()
        val old = (0 until 100).map { job("old-$it") }
        assertTrue(policy.newlyCompleted(old).isEmpty())
        assertTrue(policy.newlyCompleted(old).isEmpty())
    }

    @Test
    fun first_empty_poll_then_new_completion_notifies_once() {
        val policy = JobCompletionNotificationPolicy()
        assertTrue(policy.newlyCompleted(emptyList()).isEmpty())
        assertEquals(listOf("new"), policy.newlyCompleted(listOf(job("new"))).map { it.id })
        assertTrue(policy.newlyCompleted(listOf(job("new"))).isEmpty())
    }

    @Test
    fun only_newly_completed_jobs_notify_after_initial_snapshot() {
        val policy = JobCompletionNotificationPolicy()
        policy.newlyCompleted(listOf(job("old"), job("waiting", "running")))
        val fresh = policy.newlyCompleted(listOf(
            job("old"), job("waiting", "completed"), job("queued", "queued")))
        assertEquals(listOf("waiting"), fresh.map { it.id })
        assertTrue(policy.newlyCompleted(listOf(job("waiting"))).isEmpty())
    }

    @Test
    fun a_failed_poll_does_not_advance_baseline_until_policy_receives_a_successful_list() {
        val policy = JobCompletionNotificationPolicy()
        // The ViewModel only calls this policy on successful listJobs().
        assertTrue(policy.newlyCompleted(listOf(job("historic"))).isEmpty())
        assertEquals(listOf("later"),
            policy.newlyCompleted(listOf(job("historic"), job("later"))).map { it.id })
    }
}
