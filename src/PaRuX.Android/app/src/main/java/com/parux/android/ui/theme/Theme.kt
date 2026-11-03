package com.parux.android.ui.theme

import android.app.Activity
import android.os.Build
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.core.view.WindowCompat

// Shared Dark Graphene Luxury palette — Windows and Android have one visual identity.
val PaRuXBrand = Color(0xFF344452)
val PaRuXBrandLight = Color(0xFFB7C0C9)
val PaRuXBrandDeep = Color(0xFF273845)
val PaRuXSecondary = Color(0xFF9FAAB6)
val PaRuXChampagne = Color(0xFFB89B6A)
val PaRuXDanger = Color(0xFFB02A1D)
val PaRuXSuccess = Color(0xFF0F7B3C)
val PaRuXWarn = Color(0xFFB8860B)

private val LightColors = lightColorScheme(
    primary = PaRuXBrand,
    onPrimary = Color.White,
    primaryContainer = Color(0xFFE8EDF1),
    onPrimaryContainer = Color(0xFF19222B),
    secondary = Color(0xFF495967),
    onSecondary = Color.White,
    tertiary = Color(0xFF84693C),
    onTertiary = Color.White,
    background = Color(0xFFF5F6F8),
    onBackground = Color(0xFF19222B),
    surface = Color.White,
    onSurface = Color(0xFF19222B),
    surfaceVariant = Color(0xFFF0F3F6),
    onSurfaceVariant = Color(0xFF495967),
    outline = Color(0xFFB5C0CA),
    error = PaRuXDanger
)

private val DarkColors = darkColorScheme(
    primary = PaRuXBrandLight,
    onPrimary = Color(0xFF07090C),
    primaryContainer = Color(0xFF202934),
    onPrimaryContainer = Color(0xFFD8DEE5),
    secondary = PaRuXSecondary,
    onSecondary = Color(0xFF07090C),
    tertiary = PaRuXChampagne,
    onTertiary = Color(0xFF07090C),
    background = Color(0xFF07090C),
    onBackground = Color(0xFFF2F4F7),
    surface = Color(0xFF0D1116),
    onSurface = Color(0xFFF2F4F7),
    surfaceVariant = Color(0xFF191F27),
    onSurfaceVariant = Color(0xFFA7B0BA),
    outline = Color(0xFF4F5B68),
    error = Color(0xFFFF9A8F)
)

@Composable
fun PaRuXTheme(
    darkTheme: Boolean = true,
    // Dynamic (Material You) color is available from Android 12, but PaRuX keeps its own
    // brand identity by default rather than re-skinning to the wallpaper - off unless asked.
    dynamicColor: Boolean = false,
    content: @Composable () -> Unit
) {
    val context = LocalContext.current
    val colorScheme = when {
        dynamicColor && Build.VERSION.SDK_INT >= Build.VERSION_CODES.S ->
            if (darkTheme) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
        darkTheme -> DarkColors
        else -> LightColors
    }

    val view = LocalView.current
    if (!view.isInEditMode) {
        SideEffect {
            val window = (view.context as Activity).window
            window.statusBarColor = colorScheme.background.toArgb()
            WindowCompat.getInsetsController(window, view).isAppearanceLightStatusBars = !darkTheme
        }
    }

    MaterialTheme(
        colorScheme = colorScheme,
        content = content
    )
}
