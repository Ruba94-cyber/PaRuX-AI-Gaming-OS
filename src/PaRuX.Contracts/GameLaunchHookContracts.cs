namespace PaRuX.Contracts;

/// <summary>One user-chosen helper program for a hook phase (a single local .exe; no shell, no script).</summary>
public sealed record GameHookSpec(string Path, string Arguments = "", int TimeoutSeconds = 15);

/// <summary>Hooks of one title. Either phase may be unset.</summary>
public sealed record GameHooksDto(GameHookSpec? Pre, GameHookSpec? Post);

/// <summary>Global switch plus every configured per-title hook (for the settings surface).</summary>
public sealed record GameHooksStateDto(bool Enabled, IReadOnlyDictionary<string, GameHooksDto> Hooks);
