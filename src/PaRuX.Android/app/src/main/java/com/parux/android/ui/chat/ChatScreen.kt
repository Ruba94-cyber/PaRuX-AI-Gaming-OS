package com.parux.android.ui.chat

import android.Manifest
import android.content.ContentResolver
import android.content.pm.PackageManager
import android.net.Uri
import android.util.Base64
import java.io.ByteArrayOutputStream
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.AttachFile
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.MicOff
import androidx.compose.material.icons.filled.Send
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import com.parux.android.core.network.ChatRoutingPreference
import com.parux.android.core.network.ConversationSummary
import com.parux.android.core.network.ManagedChatModelInfo
import com.parux.android.core.network.ProjectDto

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ChatScreen(viewModel: ChatViewModel) {
    val state by viewModel.state.collectAsState()
    val localCore by viewModel.localCoreConnection.collectAsState()
    var showHistory by remember { mutableStateOf(false) }
    // Text is safe to preserve through Activity recreation. The base64 image
    // is deliberately NOT stored in savedInstanceState (Android binder limit).
    var composerText by rememberSaveable { mutableStateOf("") }
    var pendingImage by remember { mutableStateOf<PendingImage?>(null) }
    var attachmentError by remember { mutableStateOf<String?>(null) }
    val context = LocalContext.current
    val listState = rememberLazyListState()

    LaunchedEffect(Unit) {
        viewModel.refreshHealthAndProfile()
        viewModel.refreshModelRouting()
        viewModel.refreshConversationList()
        viewModel.refreshSkills()
        viewModel.refreshProjects()
    }
    LaunchedEffect(state.current.messages.size) {
        if (state.current.messages.isNotEmpty()) listState.animateScrollToItem(state.current.messages.size - 1)
    }

    val imagePicker = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri: Uri? ->
        if (uri != null) {
            val image = uriToPendingImage(context.contentResolver, uri)
            if (image == null) {
                attachmentError = "Could not attach this image. Use a supported image smaller than 8 MB."
            } else {
                pendingImage = image
                attachmentError = null
            }
        }
    }
    val microphonePermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) viewModel.startVoiceMode() else viewModel.microphonePermissionDenied()
    }

    fun toggleVoice() {
        if (state.voiceModeEnabled) {
            if (state.voicePhase == LiveVoicePhase.Speaking) viewModel.interruptVoicePlayback()
            else viewModel.stopVoiceMode()
            return
        }
        if (ContextCompat.checkSelfPermission(context, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) {
            viewModel.startVoiceMode()
        } else microphonePermission.launch(Manifest.permission.RECORD_AUDIO)
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("PaRuX") },
                actions = {
                    IconButton(onClick = { showHistory = true }) { Icon(Icons.Default.History, contentDescription = "History") }
                    IconButton(onClick = { viewModel.newConversation() }) { Icon(Icons.Default.Add, contentDescription = "New chat") }
                }
            )
        }
    ) { padding ->
        Column(Modifier.padding(padding).fillMaxSize().imePadding()) {
            if (state.gamingMode) GamingBanner()
            if (localCore == null) {
                Surface(color = MaterialTheme.colorScheme.surfaceVariant,
                    modifier = Modifier.fillMaxWidth()) {
                    Text("Local PaRuX PC not connected. Pair in Settings; your unsent text stays here.",
                        modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            ProfileRow(current = state.aiProfile, onChange = viewModel::setAiProfile)
            ModelSelectorRow(
                preference = state.modelRouting,
                models = state.managedModels,
                onAuto = viewModel::setAutoModelRouting,
                onManual = viewModel::setManualModelRouting
            )
            ProjectSelectorRow(state.projects, state.selectedProjectId, viewModel::setProject)
            SkillSelectorRow(state.skills, state.forcedSkillLabel) { id, label -> viewModel.setForcedSkill(id, label) }
            if (state.voiceModeEnabled) {
                LiveVoiceStatusBar(state.voicePhase, state.voiceLastTranscript, viewModel::interruptVoicePlayback, viewModel::stopVoiceMode)
            }

            if (state.current.messages.isEmpty()) {
                EmptyState(Modifier.weight(1f), state.selectedProjectId != null)
            } else {
                LazyColumn(Modifier.weight(1f).fillMaxWidth(), state = listState, contentPadding = PaddingValues(12.dp)) {
                    items(state.current.messages, key = { it.id }) { message -> MessageBubble(message) }
                }
            }

            state.errorMessage?.let { message ->
                Surface(color = MaterialTheme.colorScheme.errorContainer, modifier = Modifier.fillMaxWidth()) {
                    Row(Modifier.padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
                        Text(message, color = MaterialTheme.colorScheme.onErrorContainer, modifier = Modifier.weight(1f))
                        TextButton(onClick = viewModel::dismissError) { Text("Dismiss") }
                    }
                }
            }

            attachmentError?.let { problem ->
                Text(problem, modifier = Modifier.padding(horizontal = 12.dp, vertical = 4.dp),
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
            }

            pendingImage?.let { image ->
                Row(Modifier.padding(horizontal = 12.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text("Attached: ${image.fileName}", style = MaterialTheme.typography.labelMedium, modifier = Modifier.weight(1f))
                    TextButton(onClick = { pendingImage = null }) { Text("Remove") }
                }
            }

            Composer(
                text = composerText,
                onTextChange = { composerText = it },
                isStreaming = state.isStreaming,
                hasAttachment = pendingImage != null,
                voiceModeEnabled = state.voiceModeEnabled,
                voicePhase = state.voicePhase,
                onVoiceClick = ::toggleVoice,
                onAttachClick = { imagePicker.launch("image/*") },
                onSend = {
                    val toSend = composerText
                    val image = pendingImage
                    val (skillId, stripped) = viewModel.parseSlashCommand(toSend.trimStart())
                    val accepted = if (skillId != null) viewModel.sendMessage(stripped, image, skillId)
                        else viewModel.sendMessage(toSend, image)
                    if (accepted) {
                        composerText = ""
                        pendingImage = null
                        attachmentError = null
                    }
                },
                onStop = viewModel::stopGeneration
            )
        }
    }

    if (showHistory) {
        HistorySheet(state.conversations, { showHistory = false }) { id ->
            viewModel.openConversation(id)
            showHistory = false
        }
    }
}

@Composable
private fun ModelSelectorRow(
    preference: ChatRoutingPreference,
    models: List<ManagedChatModelInfo>,
    onAuto: (String) -> Unit,
    onManual: (String) -> Unit
) {
    var expanded by remember { mutableStateOf(false) }
    var filter by remember { mutableStateOf("") }
    var sort by remember { mutableStateOf(MobileModelSort.Name) }
    val current = if (preference.mode == "manual") {
        val file = preference.manualModelFileName ?: "missing model"
        val alias = models.firstOrNull { it.fileName.equals(file, ignoreCase = true) }?.alias
        "Manual · " + (alias?.takeIf { it.isNotBlank() } ?: file)
    } else {
        "Auto · " + when (preference.autoProfile) {
            "max_intelligence" -> "Max intelligence"
            "responsive" -> "Responsive"
            "custom" -> "Custom"
            else -> "Balanced"
        }
    }

    // One pure bounded policy for selection, search and order. No GPU load or
    // routing change occurs until the user taps an actual installed model.
    val modelPage = MobileModelBrowsePolicy.browse(
        models, filter, sort,
        preference.manualModelFileName.takeIf { preference.mode == "manual" })
    val visible = modelPage.visible
    val matchingCount = modelPage.totalMatches

    Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 2.dp)) {
        Box {
            TextButton(onClick = { filter = ""; expanded = true }) { Text("Model: $current") }
            DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
                listOf(
                    "balanced" to "Auto · Balanced",
                    "max_intelligence" to "Auto · Max intelligence",
                    "responsive" to "Auto · Responsive",
                    "custom" to "Auto · Custom (configured on PC)"
                ).forEach { (profile, label) ->
                    DropdownMenuItem(text = { Text(label) }, onClick = {
                        onAuto(profile)
                        expanded = false
                    })
                }
                if (models.size > 10) {
                    OutlinedTextField(
                        value = filter,
                        onValueChange = { filter = it },
                        placeholder = { Text("Search installed models") },
                        singleLine = true,
                        modifier = Modifier.width(280.dp).padding(horizontal = 8.dp)
                    )
                }
                if (models.size > 1) {
                    Row(
                        Modifier.padding(horizontal = 8.dp),
                        horizontalArrangement = Arrangement.spacedBy(6.dp)
                    ) {
                        FilterChip(
                            selected = sort == MobileModelSort.Name,
                            onClick = { sort = MobileModelSort.Name },
                            label = { Text("Name") }
                        )
                        FilterChip(
                            selected = sort == MobileModelSort.SmallestFile,
                            onClick = { sort = MobileModelSort.SmallestFile },
                            label = { Text("Small") }
                        )
                        FilterChip(
                            selected = sort == MobileModelSort.MeasuredSpeed,
                            onClick = { sort = MobileModelSort.MeasuredSpeed },
                            label = { Text("Speed") }
                        )
                    }
                }
                if (matchingCount == 0) {
                    DropdownMenuItem(
                        text = { Text("No installed models match this search") },
                        onClick = {}, enabled = false
                    )
                }
                visible.forEach { model ->
                    val display = model.alias?.takeIf { it.isNotBlank() }?.let { "$it · ${model.fileName}" } ?: model.fileName
                    val size = model.sizeBytes.takeIf { it > 0 }?.let {
                        " · ${"%.1f".format(it / 1073741824.0)} GiB"
                    } ?: ""
                    val speed = model.lastBenchmarkTokensPerSecond?.takeIf { it.isFinite() && it > 0 }?.let {
                        " · ${"%.1f".format(it)} last tok/s"
                    } ?: ""
                    DropdownMenuItem(text = { Text("Manual · $display$size$speed") }, onClick = {
                        onManual(model.fileName)
                        expanded = false
                    })
                }
                if (matchingCount > visible.size) {
                    DropdownMenuItem(
                        text = { Text("Showing ${visible.size} of $matchingCount · refine search") },
                        onClick = {}, enabled = false
                    )
                }
            }
        }
    }
}

@Composable
private fun LiveVoiceStatusBar(phase: LiveVoicePhase, transcript: String?, onInterrupt: () -> Unit, onStop: () -> Unit) {
    Surface(tonalElevation = 2.dp, modifier = Modifier.fillMaxWidth()) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Icon(Icons.Default.Mic, contentDescription = null)
            Column(Modifier.weight(1f)) {
                Text("Live Voice · ${voicePhaseLabel(phase)}", style = MaterialTheme.typography.labelLarge)
                transcript?.takeIf { it.isNotBlank() }?.let {
                    Text("Heard: ${it.take(90)}${if (it.length > 90) "…" else ""}", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            if (phase == LiveVoicePhase.Speaking) TextButton(onClick = onInterrupt) { Text("Interrupt") }
            TextButton(onClick = onStop) { Text("Stop") }
        }
    }
}

private fun voicePhaseLabel(phase: LiveVoicePhase): String = when (phase) {
    LiveVoicePhase.Off -> "Off"
    LiveVoicePhase.Checking -> "Checking local speech"
    LiveVoicePhase.Listening -> "Listening"
    LiveVoicePhase.Transcribing -> "Transcribing locally"
    LiveVoicePhase.Thinking -> "Thinking"
    LiveVoicePhase.Speaking -> "Speaking · say something to interrupt"
}

@Composable
private fun ProfileRow(current: String, onChange: (String) -> Unit) {
    Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        listOf("STANDARD", "UNCENSORED").forEach { option ->
            FilterChip(selected = current == option, onClick = { onChange(option) }, label = { Text(option.lowercase().replaceFirstChar { it.uppercase() }) })
        }
    }
}

@Composable
private fun ProjectSelectorRow(projects: List<ProjectDto>, selectedProjectId: String?, onSelect: (String?) -> Unit) {
    var expanded by remember { mutableStateOf(false) }
    val selected = projects.firstOrNull { it.id == selectedProjectId }
    val label = selected?.goal?.take(36) ?: "None"
    Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 2.dp)) {
        Box {
            TextButton(onClick = { expanded = true }) { Text("Project: $label") }
            DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
                DropdownMenuItem(text = { Text("None · ordinary Chat") }, onClick = { onSelect(null); expanded = false })
                projects.forEach { project ->
                    DropdownMenuItem(text = { Text(project.goal) }, onClick = { onSelect(project.id); expanded = false })
                }
            }
        }
    }
}

@Composable
private fun SkillSelectorRow(
    skills: List<com.parux.android.core.network.SkillManifest>,
    selectedLabel: String,
    onSelect: (String?, String) -> Unit
) {
    var expanded by remember { mutableStateOf(false) }
    Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 2.dp)) {
        Box {
            TextButton(onClick = { expanded = true }) { Text("Skill: $selectedLabel") }
            DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
                DropdownMenuItem(text = { Text("Auto") }, onClick = { onSelect(null, "Auto"); expanded = false })
                skills.sortedBy { it.displayName }.forEach { skill ->
                    DropdownMenuItem(text = { Text(skill.displayName) }, onClick = { onSelect(skill.id, skill.displayName); expanded = false })
                }
            }
        }
    }
}

@Composable
private fun GamingBanner() {
    Surface(color = MaterialTheme.colorScheme.tertiaryContainer, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp)) {
            Text("Gaming Mode", style = MaterialTheme.typography.titleSmall)
            Text("PaRuX AI is suspended for now to leave all resources for the game on your PC.", style = MaterialTheme.typography.bodySmall)
        }
    }
}

@Composable
private fun EmptyState(modifier: Modifier = Modifier, projectScoped: Boolean = false) {
    Column(modifier.fillMaxWidth().padding(32.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) {
        Text(if (projectScoped) "Continue this Project" else "Ask me something", style = MaterialTheme.typography.titleMedium)
        Text(
            if (projectScoped) "PaRuX will load this Project's bounded Core knowledge before every turn." else "Runs on your PC over the local network.",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

@Composable
private fun MessageBubble(message: UiMessage) {
    val isUser = message.role == "user"
    Row(Modifier.fillMaxWidth().padding(vertical = 4.dp), horizontalArrangement = if (isUser) Arrangement.End else Arrangement.Start) {
        Column(horizontalAlignment = if (isUser) Alignment.End else Alignment.Start) {
            if (message.switchedToUncensored) {
                Text("Switched to Uncensored", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.tertiary, modifier = Modifier.padding(bottom = 2.dp))
            }
            message.modelRouteLabel?.let {
                Text("Model · $it", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.primary, modifier = Modifier.padding(bottom = 2.dp))
            }
            // Above the bubble, not below it: the user should know the turn left their PC before
            // they read the answer, not after.
            message.cloudFallbackNotice?.let {
                Text(it, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(bottom = 2.dp))
            }
            Card(
                colors = androidx.compose.material3.CardDefaults.cardColors(
                    containerColor = when {
                        message.isError -> MaterialTheme.colorScheme.errorContainer
                        isUser -> MaterialTheme.colorScheme.primaryContainer
                        else -> MaterialTheme.colorScheme.surfaceVariant
                    }
                )
            ) {
                Box(Modifier.padding(12.dp)) {
                    if (message.content.isEmpty() && message.isStreaming) CircularProgressIndicator(Modifier.height(16.dp).width(16.dp), strokeWidth = 2.dp)
                    else MarkdownText(message.content)
                }
            }
            message.route?.let { Text(it, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            message.modelRouteReason?.takeIf { it.isNotBlank() }?.let {
                Text("Why: ${it.take(180)}${if (it.length > 180) "…" else ""}", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 2.dp))
            }
            if (message.skillUsed != null || message.ragSources.isNotEmpty()) {
                val skillPart = message.skillUsed?.let { "Skill: $it" }
                val sourcesPart = if (message.ragSources.isNotEmpty()) "Sources: " + message.ragSources.joinToString(", ") else null
                Text(listOfNotNull(skillPart, sourcesPart).joinToString("  ·  "), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 2.dp))
            }
        }
    }
}

@Composable
private fun Composer(
    text: String,
    onTextChange: (String) -> Unit,
    isStreaming: Boolean,
    hasAttachment: Boolean,
    voiceModeEnabled: Boolean,
    voicePhase: LiveVoicePhase,
    onVoiceClick: () -> Unit,
    onAttachClick: () -> Unit,
    onSend: () -> Unit,
    onStop: () -> Unit
) {
    Row(Modifier.fillMaxWidth().padding(8.dp), verticalAlignment = Alignment.Bottom, horizontalArrangement = Arrangement.spacedBy(4.dp)) {
        IconButton(onClick = onAttachClick) { Icon(Icons.Default.AttachFile, contentDescription = "Attach image") }
        IconButton(onClick = onVoiceClick) {
            Icon(
                if (voiceModeEnabled && voicePhase != LiveVoicePhase.Speaking) Icons.Default.MicOff else Icons.Default.Mic,
                contentDescription = when {
                    !voiceModeEnabled -> "Start local Live Voice"
                    voicePhase == LiveVoicePhase.Speaking -> "Interrupt spoken reply"
                    else -> "Stop Live Voice"
                }
            )
        }
        OutlinedTextField(
            value = text,
            onValueChange = onTextChange,
            modifier = Modifier.weight(1f),
            placeholder = { Text(if (voiceModeEnabled) "Live Voice is active…" else "Message PaRuX…") },
            maxLines = 6
        )
        Spacer(Modifier.width(4.dp))
        if (isStreaming) IconButton(onClick = onStop) { Icon(Icons.Default.Stop, contentDescription = "Stop") }
        else IconButton(onClick = onSend, enabled = text.isNotBlank() || hasAttachment) { Icon(Icons.Default.Send, contentDescription = "Send") }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun HistorySheet(conversations: List<ConversationSummary>, onDismiss: () -> Unit, onSelect: (String) -> Unit) {
    val sheetState = rememberModalBottomSheetState()
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState) {
        Column(Modifier.padding(16.dp)) {
            Text("Conversations", style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(bottom = 8.dp))
            if (conversations.isEmpty()) Text("No conversations yet.", color = MaterialTheme.colorScheme.onSurfaceVariant)
            LazyColumn {
                items(conversations, key = { it.id }) { conversation ->
                    Surface(onClick = { onSelect(conversation.id) }, modifier = Modifier.fillMaxWidth()) {
                        Text(conversation.title, Modifier.padding(vertical = 12.dp))
                    }
                }
            }
        }
    }
}

private const val MAX_CHAT_IMAGE_BYTES = 8 * 1024 * 1024

private fun uriToPendingImage(resolver: ContentResolver, uri: Uri): PendingImage? {
    return try {
    val mimeType = resolver.getType(uri) ?: return null
    if (!mimeType.startsWith("image/")) return null
    // Content providers may not supply a trustworthy length. Bound the
    // streamed bytes before base64 expansion to protect low-memory phones.
    val bytes = resolver.openInputStream(uri)?.use { input ->
        val output = ByteArrayOutputStream()
        val buffer = ByteArray(8192)
        var size = 0
        while (true) {
            val n = input.read(buffer)
            if (n < 0) break
            size += n
            if (size > MAX_CHAT_IMAGE_BYTES) return null
            output.write(buffer, 0, n)
        }
        output.toByteArray()
    } ?: return null
    if (bytes.isEmpty()) return null
    PendingImage(
        fileName = "attachment.${mimeType.substringAfterLast('/')}",
        mediaType = mimeType,
        base64Data = Base64.encodeToString(bytes, Base64.NO_WRAP)
    )
    } catch (_: Exception) {
        null
    }
}
