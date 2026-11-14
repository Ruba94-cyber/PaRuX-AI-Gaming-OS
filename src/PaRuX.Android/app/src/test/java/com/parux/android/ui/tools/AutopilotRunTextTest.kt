package com.parux.android.ui.tools

import com.parux.android.core.network.AutopilotRunDto
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AutopilotRunTextTest {

    @Test
    fun `a pending run says it needs desktop approval and can be cancelled`() {
        val run = AutopilotRunDto(id = "r1", status = "pending_approval", awaitingDesktopApproval = true)

        assertTrue(AutopilotRunText.canCancel(run))
        val message = AutopilotRunText.statusMessage(run)
        assertTrue(message.contains("PaRuX desktop"))
        assertTrue(message.contains("cancel"))
        assertFalse(message.contains("Approve", ignoreCase = false))
    }

    @Test
    fun `an older server without the flag is still recognised by its status`() {
        assertTrue(AutopilotRunText.canCancel(AutopilotRunDto(id = "r2", status = "pending_approval")))
    }

    @Test
    fun `a running or finished run cannot be cancelled from the phone`() {
        assertFalse(AutopilotRunText.canCancel(AutopilotRunDto(id = "r3", status = "running")))
        assertFalse(AutopilotRunText.canCancel(AutopilotRunDto(id = "r4", status = "completed")))
        assertFalse(AutopilotRunText.canCancel(null))
        assertEquals("Run r3: running", AutopilotRunText.statusMessage(AutopilotRunDto(id = "r3", status = "running")))
    }

    @Test
    fun `an expired run is reported honestly`() {
        val run = AutopilotRunDto(id = "r5", status = "expired")
        assertFalse(AutopilotRunText.canCancel(run))
        assertTrue(AutopilotRunText.statusMessage(run).contains("expired"))
    }
}
