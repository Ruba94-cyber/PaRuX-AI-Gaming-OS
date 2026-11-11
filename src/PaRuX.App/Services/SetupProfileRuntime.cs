using System;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// Applies a setup-profile switch (AI only / Gaming only / Both) to the running app without a restart: starts and stops the
    /// background services the profile needs, using the pure <see cref="SetupProfileTransition"/> planner (stops first, rollback
    /// on failure). It tracks which services it has actually started, so applying the same profile twice does nothing. It never
    /// deletes models, history, games, settings or indexes and never installs anything; the profile is persisted only after
    /// every step succeeded.
    /// </summary>
    public static class SetupProfileRuntime
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static SetupRunningServices _running = new SetupRunningServices(false, false, false);

        /// <summary>Called once by start-up, after it started the services the stored profile asks for.</summary>
        public static void MarkStartedBy(SetupProfilePlan plan) => _running = SetupRunningServices.Of(plan);

        public static SetupRunningServices Running => _running;

        /// <summary>Switches to <paramref name="requested"/>. On success the profile is saved; on failure nothing changes
        /// (every applied step was undone) and the stored profile stays as it was.</summary>
        public static async Task<SetupTransitionResult> SwitchAsync(SetupProfile requested, CancellationToken ct = default)
        {
            await Gate.WaitAsync(ct).ConfigureAwait(true);
            try
            {
                SetupProfile previous = SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId);
                SetupTransitionPlan plan = SetupProfileTransition.Plan(
                    _running, requested, PaRuX.Contracts.CorporateMode.IsActive, GamingManager.Current.IsActive);

                // A chat answer in flight belongs to a workspace that may be about to be hidden: end it cleanly first.
                if (plan.Steps.Count > 0)
                {
                    try { ChatSession.Current.Cancel(); ChatSession.Current.Persist(); }
                    catch (Exception ex) { Log.Write("Setup switch: chat cleanup failed: " + ex.GetType().Name); }
                }

                SetupTransitionResult result = await SetupProfileTransition.ApplyAsync(plan, previous, RunStepAsync, ct).ConfigureAwait(true);
                if (result.Success)
                {
                    SettingsStore.Current.Data.SetupProfileId = SetupProfilePolicy.ToStorage(result.EffectiveProfile);
                    SettingsStore.Current.Save();
                }
                Log.Write("Setup profile switch " + SetupProfilePolicy.ToStorage(previous) + " -> " + SetupProfilePolicy.ToStorage(plan.Target)
                    + ": success=" + result.Success + ", steps=" + plan.Steps.Count + (result.Success ? string.Empty : " (" + result.Message + ")"));
                return result;
            }
            finally { Gate.Release(); }
        }

        private static async Task<bool> RunStepAsync(SetupTransitionStep step, CancellationToken ct)
        {
            bool start = step.Kind == SetupStepKind.Start;
            switch (step.Service)
            {
                case SetupService.LocalAi:
                    if (start)
                    {
                        LocalAiController.Current.StartWatching();
                        // Same idempotent warm-up as start-up; Core refuses it while a game is active or the mode is OFF.
                        _ = Task.Run(async () =>
                        {
                            try { await LocalAiController.Current.RunActionAsync("Warmup", CancellationToken.None).ConfigureAwait(false); }
                            catch (Exception ex) { Log.Write("Setup switch: model warm-up failed: " + ex.GetType().Name); }
                        });
                    }
                    else
                    {
                        // Unload the text and media processes without touching the saved AI mode, then stop watching.
                        LocalAiController.ActionResult stopped = await LocalAiController.Current.RunActionAsync("MediaStop", ct).ConfigureAwait(true);
                        if (!stopped.Success) return false;
                        LocalAiController.Current.StopWatching();
                    }
                    break;
                case SetupService.Gaming:
                    if (start) GamingManager.Current.Start(); else GamingManager.Current.Stop();
                    break;
                default:
                    if (start)
                    {
                        AgentCodingWorkerService.Current.Start();
                        AgentBinaryAnalysisWorkerService.Current.Start();
                        UiVerificationWorkerService.Current.Start();
                        ComputerUseWorkerService.Current.Start();
                    }
                    else
                    {
                        AgentCodingWorkerService.Current.Stop();
                        AgentBinaryAnalysisWorkerService.Current.Stop();
                        UiVerificationWorkerService.Current.Stop();
                        ComputerUseWorkerService.Current.Stop();
                    }
                    break;
            }
            _running = _running.With(step.Service, start);
            return true;
        }
    }
}
