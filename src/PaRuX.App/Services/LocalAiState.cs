namespace PaRuX.Services
{
    /// <summary>A point-in-time snapshot of the LocalAI stack, for the diagnostics panel.</summary>
    public class LocalAiState
    {
        /// <summary>ON | OFF | AUTO</summary>
        public string Mode { get; set; } = "AUTO";

        /// <summary>STANDARD | UNCENSORED</summary>
        public string TextProfile { get; set; } = "STANDARD";

        /// <summary>Human-readable, e.g. "Qwen3.8-27B Standard GPU". Empty when nothing is loaded.</summary>
        public string ActiveProfile { get; set; } = string.Empty;

        public bool GameActive { get; set; }
        public bool ServerProcessPresent { get; set; }
        public bool Healthy { get; set; }

        /// <summary>Chat header derived from the reported profile without guessing the model family.</summary>
        public string ShortProfileLabel
        {
            get
            {
                string p = (ActiveProfile ?? string.Empty).Trim();
                if (p.Length == 0) return "Model idle";
                const System.StringComparison comparison = System.StringComparison.OrdinalIgnoreCase;
                const string mediaSuffix = " media assistant";
                if (p.EndsWith(mediaSuffix, comparison))
                {
                    string model = p[..^mediaSuffix.Length].TrimEnd();
                    const string uncensoredSuffix = " Uncensored";
                    if (model.EndsWith(uncensoredSuffix, comparison))
                        model = model[..^uncensoredSuffix.Length].TrimEnd();
                    return model.Length == 0 ? "Media prompt assistant" : "Media prompt assistant · " + model;
                }

                // Strip only role suffixes explicitly encoded in the reported profile. Unknown
                // profiles stay intact: neither model identity nor role is inferred from size.
                foreach (string role in new[] { "Standard", "Uncensored" })
                {
                    foreach (string backend in new[] { " GPU", " CPU/Vulkan", "" })
                    {
                        string suffix = " " + role + backend;
                        if (p.EndsWith(suffix, comparison))
                        {
                            string model = p[..^suffix.Length].TrimEnd();
                            return model.Length == 0 ? p : model + " · " + role;
                        }
                    }
                }
                return p;
            }
        }
    }
}
