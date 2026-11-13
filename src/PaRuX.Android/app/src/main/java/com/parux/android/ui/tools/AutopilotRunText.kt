package com.parux.android.ui.tools

import com.parux.android.core.network.AutopilotRunDto

/**
 * Presentation rules for an Autopilot run. A run a phone starts is held until the desktop owner approves it on the
 * PaRuX desktop; the phone can only cancel it, so it never offers an Approve action.
 */
object AutopilotRunText {
    const val PENDING = "pending_approval"
    const val EXPIRED = "expired"

    fun isAwaitingDesktop(run: AutopilotRunDto): Boolean =
        run.awaitingDesktopApproval || run.status == PENDING

    /** True while the phone may still cancel the run. */
    fun canCancel(run: AutopilotRunDto?): Boolean = run != null && isAwaitingDesktop(run)

    fun statusMessage(run: AutopilotRunDto): String = when {
        isAwaitingDesktop(run) ->
            "Run ${run.id}: waiting for approval on the PaRuX desktop (Settings, Autopilot). You can cancel it here."
        run.status == EXPIRED ->
            "Run ${run.id}: expired because the desktop did not approve it in time."
        else -> "Run ${run.id}: ${run.status}"
    }
}
