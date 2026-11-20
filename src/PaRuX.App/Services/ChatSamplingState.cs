using System.IO;

namespace PaRuX.Services
{
    /// <summary>The app-wide chat generation settings, stored next to the prompt library. Reads are cached; Save updates the next request.</summary>
    public static class ChatSamplingState
    {
        private static readonly ChatSamplingStore Store = new(Path.Combine(AppPaths.PaRuXRoot, "chat-sampling.json"));
        public static ChatSamplingValues Current => Store.Load();
        public static ChatSamplingValues Save(ChatSamplingValues values) => Store.Save(values);
    }
}
