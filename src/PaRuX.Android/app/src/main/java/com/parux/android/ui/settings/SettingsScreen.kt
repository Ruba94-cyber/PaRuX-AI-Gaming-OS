package com.parux.android.ui.settings

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Card
import androidx.compose.material3.Divider
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import com.parux.android.BuildConfig
import com.parux.android.R

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(viewModel: SettingsViewModel, onForgetDevice: () -> Unit) {
    val state by viewModel.state.collectAsState()

    Scaffold(topBar = { TopAppBar(title = { Text(stringResource(R.string.settings_title)) }) }) { padding ->
        Column(
            Modifier
                .padding(padding)
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(16.dp)
        ) {
            SectionHeader(stringResource(R.string.settings_section_connection))
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp)) {
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                        Text(stringResource(R.string.settings_pc_name))
                        Text(
                            if (state.serverReachable == true) stringResource(R.string.settings_connected)
                            else if (state.serverReachable == false) stringResource(R.string.settings_offline)
                            else stringResource(R.string.settings_checking),
                            color = if (state.serverReachable == true) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.error
                        )
                    }
                    Text(state.endpoint, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    Divider(Modifier.padding(vertical = 12.dp))
                    Row(
                        Modifier
                            .fillMaxWidth()
                            .clickable(role = Role.Button) { viewModel.forgetDevice(); onForgetDevice() }
                            .padding(vertical = 8.dp),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Column(Modifier.weight(1f)) {
                            Text(stringResource(R.string.settings_forget_title), color = MaterialTheme.colorScheme.error)
                            Text(
                                stringResource(R.string.settings_forget_hint),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                        Text(
                            stringResource(R.string.settings_forget_action),
                            color = MaterialTheme.colorScheme.error,
                            style = MaterialTheme.typography.labelLarge,
                            modifier = Modifier.padding(start = 12.dp)
                        )
                    }
                }
            }

            SectionHeader(stringResource(R.string.settings_section_execution))
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp)) {
                    Text(stringResource(R.string.settings_quality_title), style = MaterialTheme.typography.titleSmall)
                    Text(
                        stringResource(R.string.settings_quality_hint),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(top = 4.dp, bottom = 8.dp)
                    )
                    QualityProfileRow(stringResource(R.string.settings_quality_fast), "fast", state, viewModel)
                    QualityProfileRow(stringResource(R.string.settings_quality_balanced), "balanced", state, viewModel)
                    QualityProfileRow(stringResource(R.string.settings_quality_maximum), "maximum-quality", state, viewModel)
                    QualityProfileRow(stringResource(R.string.settings_quality_overnight), "overnight", state, viewModel)
                    Divider(Modifier.padding(vertical = 10.dp))
                    StatusRow(stringResource(R.string.settings_preferred_context), formatTokens(state.preferredContextTokens, stringResource(R.string.settings_auto)))
                    StatusRow(stringResource(R.string.settings_reasoning_effort), state.reasoningEffort.toString())
                    if (state.executionEvidenceSource.isNotBlank())
                        StatusRow(stringResource(R.string.settings_policy_evidence), state.executionEvidenceSource)
                    state.executionProfileError?.let {
                        Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall,
                            modifier = Modifier.padding(top = 8.dp))
                    }
                }
            }

            SectionHeader(stringResource(R.string.settings_section_health))
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp)) {
                    if (state.workstationMetrics.isEmpty()) {
                        Text(stringResource(R.string.settings_no_observability), style = MaterialTheme.typography.bodySmall)
                    } else {
                        state.workstationMetrics.take(6).forEach { metric ->
                            StatusRow(metric.label, metric.value)
                            metric.detail?.takeIf { it.isNotBlank() }?.let {
                                Text(it, style = MaterialTheme.typography.labelSmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                    modifier = Modifier.padding(bottom = 4.dp))
                            }
                        }
                    }
                    Divider(Modifier.padding(vertical = 8.dp))
                    StatusRow(stringResource(R.string.settings_engine_races), state.engineRaceSummary.total.toString())
                    StatusRow(stringResource(R.string.settings_race_winners), state.engineRaceSummary.completed.toString())
                }
            }

            SectionHeader(stringResource(R.string.settings_section_system))
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp)) {
                    StatusRow(stringResource(R.string.settings_gaming_mode), if (state.gamingMode) stringResource(R.string.settings_active) else stringResource(R.string.settings_off))
                    StatusRow(stringResource(R.string.settings_model_runtime), if (state.providerReady) stringResource(R.string.settings_ready) else stringResource(R.string.settings_not_loaded))
                }
            }

            SectionHeader(stringResource(R.string.settings_section_advanced))
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp)) {
                    StatusRow(stringResource(R.string.settings_api_surface), stringResource(R.string.settings_api_surface_value))
                    StatusRow(stringResource(R.string.settings_app_version), BuildConfig.VERSION_NAME)
                    StatusRow(
                        stringResource(R.string.settings_source_revision),
                        if (BuildConfig.SOURCE_COMMIT == "development") stringResource(R.string.settings_development_build)
                        else BuildConfig.SOURCE_COMMIT.take(12)
                    )
                }
            }
        }
    }
}

@Composable
private fun QualityProfileRow(
    label: String,
    value: String,
    state: SettingsUiState,
    viewModel: SettingsViewModel
) {
    val selected = state.executionQualityProfile == value
    Row(
        Modifier
            .fillMaxWidth()
            .clickable(
                enabled = state.serverReachable == true && !state.executionProfileChanging,
                role = Role.Button
            ) { viewModel.setExecutionQuality(value) }
            .padding(vertical = 9.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(label, color = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface)
        Text(
            if (selected) stringResource(R.string.settings_profile_active) else if (state.executionProfileChanging) "…" else stringResource(R.string.settings_profile_select),
            style = MaterialTheme.typography.labelMedium,
            color = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

@Composable
private fun SectionHeader(text: String) {
    Text(
        text.uppercase(),
        style = MaterialTheme.typography.labelMedium,
        color = MaterialTheme.colorScheme.primary,
        modifier = Modifier.padding(top = 16.dp, bottom = 8.dp)
    )
}

@Composable
private fun StatusRow(label: String, value: String) {
    Row(Modifier.fillMaxWidth().padding(vertical = 4.dp), horizontalArrangement = Arrangement.SpaceBetween) {
        Text(label, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(value)
    }
}

private fun formatTokens(tokens: Int, autoLabel: String): String = when {
    tokens >= 1_000_000 -> String.format("%.2fM", tokens / 1_000_000.0)
    tokens >= 1_000 -> String.format("%.0fk", tokens / 1_000.0)
    tokens > 0 -> tokens.toString()
    else -> autoLabel
}