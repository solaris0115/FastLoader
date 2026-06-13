using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using RimWorld;
using UnityEngine;
using Verse;

namespace FastLoader
{
    internal sealed class WorkshopUpdatedMod
    {
        public string Name;
        public string PackageId;
        public string WorkshopId;
        public DateTime UpdatedUtc;
    }

    internal sealed class WorkshopUpdateCheckResult
    {
        public bool Failed;
        public string FailureReason;
        public DateTime ReferenceUtc;
        public double ElapsedMs;
        public int WorkshopModCount;
        public int LocalModCount;
        public int BatchCount;
        public int FailedBatchCount;
        public int MissingRemoteCount;
        public string Signature;
        public List<string> Reasons = new List<string>();
        public List<string> FailedBatchMessages = new List<string>();
        public List<WorkshopUpdatedMod> UpdatedMods = new List<WorkshopUpdatedMod>();
    }

    internal static class FastLoaderModUpdateChecker
    {
        private const int BatchSize = 50;
        private const int MaxParallelRequests = 4;
        private const int MaxRequestAttempts = 3;
        private const int RequestTimeoutMs = 8000;
        private const string Endpoint = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";

        private static readonly object SyncRoot = new object();
        private static Task<WorkshopUpdateCheckResult> runningTask;
        private static bool started;
        private static bool completed;
        private static bool promptShown;

        public static void StartAfterMainMenu()
        {
            lock (SyncRoot)
            {
                if (started)
                {
                    return;
                }

                started = true;
            }

            if (FastLoaderRuntime.Settings != null &&
                !FastLoaderRuntime.Settings.UseXmlCache &&
                !FastLoaderRuntime.Settings.UseTextureCache &&
                !FastLoaderRuntime.Settings.UseAtlasCache)
            {
                return;
            }

            FastLoaderCacheStateData state;
            if (!FastLoaderCacheState.TryRead(out state))
            {
                Log.Message("[FastLoader] Cache state check skipped because cache state is missing.");
                return;
            }

            if (FastLoaderRuntime.ShouldUseXmlCache &&
                (FastLoaderRuntime.Settings == null ||
                 (!FastLoaderRuntime.Settings.UseTextureCache && !FastLoaderRuntime.Settings.UseAtlasCache)) &&
                FastLoaderRuntime.LastMode != FastLoaderMode.CacheHit &&
                string.Equals(FastLoaderRuntime.LastStatusReason, "cache files missing", StringComparison.OrdinalIgnoreCase))
            {
                Log.Message("[FastLoader] Cache state check skipped because XML cache files are missing.");
                return;
            }

            List<FastLoaderCacheStateMod> workshopMods = GetWorkshopMods(state);
            int localModCount = state.Mods != null ? Math.Max(0, state.Mods.Count - workshopMods.Count) : 0;
            List<string> earlyReasons = BuildLanguageReasons(state);

            string currentHash;
            try
            {
                currentHash = FastLoaderHasher.ComputeFastModListHash();
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Workshop update check skipped because current mod list hash failed.\n" + ex);
                return;
            }

            if (!string.Equals(state.InputHash, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                runningTask = Task.Run(delegate
                {
                    WorkshopUpdateCheckResult result = CreateBaseResult(state, localModCount);
                    result.WorkshopModCount = workshopMods.Count;
                    AddReasons(result, earlyReasons);
                    result.Reasons.Add("Active mod list/order changed since the cache was built.");
                    result.Signature = BuildSignature(state, result);
                    return result;
                });
                return;
            }

            if (FastLoaderRuntime.ShouldUseXmlCache && FastLoaderRuntime.LastMode != FastLoaderMode.CacheHit)
            {
                runningTask = Task.Run(delegate
                {
                    WorkshopUpdateCheckResult result = CreateBaseResult(state, localModCount);
                    result.WorkshopModCount = workshopMods.Count;
                    AddReasons(result, earlyReasons);
                    result.Reasons.Add("XML cache was not used: " + (FastLoaderRuntime.LastStatusReason ?? "unknown reason"));
                    result.Signature = BuildSignature(state, result);
                    return result;
                });
                return;
            }

            if (workshopMods.Count == 0)
            {
                if (earlyReasons.Count > 0)
                {
                    runningTask = Task.Run(delegate
                    {
                        WorkshopUpdateCheckResult result = CreateBaseResult(state, localModCount);
                        result.WorkshopModCount = workshopMods.Count;
                        AddReasons(result, earlyReasons);
                        result.Signature = BuildSignature(state, result);
                        return result;
                    });
                    return;
                }

                Log.Message("[FastLoader] Workshop update check skipped because no Steam Workshop mods were recorded. Local/untracked mods: " + localModCount);
                return;
            }

            Log.Message("[FastLoader] Workshop update check started. Workshop mods: " + workshopMods.Count + ", local/untracked mods: " + localModCount + ", batchSize=" + BatchSize + ", maxParallel=" + MaxParallelRequests);
            runningTask = Task.Run(delegate { return CheckWorkshopUpdates(state, workshopMods, localModCount, earlyReasons); });
        }

        public static void PollUi()
        {
            Task<WorkshopUpdateCheckResult> task = runningTask;
            if (completed || task == null || !task.IsCompleted)
            {
                return;
            }

            completed = true;
            WorkshopUpdateCheckResult result;
            try
            {
                result = task.Result;
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Workshop update check failed. Cache remains usable.\n" + ex);
                return;
            }

            if (result == null)
            {
                return;
            }

            LogCheckSummary(result);
            if (result.Reasons.Count == 0 && result.UpdatedMods.Count == 0)
            {
                return;
            }

            if (FastLoaderRuntime.Settings != null &&
                !string.IsNullOrEmpty(result.Signature) &&
                string.Equals(FastLoaderRuntime.Settings.IgnoredWorkshopUpdateSignature, result.Signature, StringComparison.Ordinal))
            {
                Log.Message("[FastLoader] Workshop update notice suppressed by previous Continue choice.");
                return;
            }

            if (promptShown)
            {
                return;
            }

            promptShown = true;
            Find.WindowStack.Add(new FastLoaderModUpdateDecisionWindow(result));
        }

        public static void IgnoreCurrentResult(WorkshopUpdateCheckResult result)
        {
            if (result == null || string.IsNullOrEmpty(result.Signature) || FastLoaderRuntime.Settings == null)
            {
                return;
            }

            FastLoaderRuntime.Settings.IgnoredWorkshopUpdateSignature = result.Signature;
            FastLoaderRuntime.Settings.Write();
            Log.Message("[FastLoader] Workshop update notice ignored for current cache state.");
        }

        private static WorkshopUpdateCheckResult CheckWorkshopUpdates(FastLoaderCacheStateData state, List<FastLoaderCacheStateMod> workshopMods, int localModCount, List<string> earlyReasons)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            WorkshopUpdateCheckResult result = CreateBaseResult(state, localModCount);
            result.WorkshopModCount = workshopMods.Count;
            AddReasons(result, earlyReasons);

            Dictionary<string, List<FastLoaderCacheStateMod>> modsById = new Dictionary<string, List<FastLoaderCacheStateMod>>(StringComparer.Ordinal);
            List<string> ids = new List<string>();
            for (int i = 0; i < workshopMods.Count; i++)
            {
                FastLoaderCacheStateMod mod = workshopMods[i];
                List<FastLoaderCacheStateMod> list;
                if (!modsById.TryGetValue(mod.WorkshopId, out list))
                {
                    list = new List<FastLoaderCacheStateMod>();
                    modsById.Add(mod.WorkshopId, list);
                    ids.Add(mod.WorkshopId);
                }

                list.Add(mod);
            }

            List<List<string>> batches = SplitBatches(ids, BatchSize);
            result.BatchCount = batches.Count;
            Dictionary<string, long> remoteTimes = new Dictionary<string, long>(StringComparer.Ordinal);
            object resultLock = new object();

            Parallel.ForEach(
                batches,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelRequests },
                delegate(List<string> batch)
                {
                    try
                    {
                        Dictionary<string, long> batchResult = FetchBatchWithRetry(batch);
                        lock (resultLock)
                        {
                            foreach (KeyValuePair<string, long> kvp in batchResult)
                            {
                                remoteTimes[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (resultLock)
                        {
                            result.FailedBatchCount++;
                            result.FailedBatchMessages.Add("batch " + DescribeBatch(batch) + ": " + ex.GetBaseException().Message);
                        }
                    }
                });

            if (result.FailedBatchCount >= result.BatchCount)
            {
                result.Failed = true;
                result.FailureReason = "all Steam request batches failed after " + MaxRequestAttempts + " attempts";
                result.Reasons.Add("Steam Workshop update check failed after " + MaxRequestAttempts + " attempts. Cache freshness could not be verified.");
                result.ElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
                result.Signature = BuildSignature(state, result);
                return result;
            }

            if (result.FailedBatchCount > 0)
            {
                result.Reasons.Add("Some Steam Workshop update checks failed after " + MaxRequestAttempts + " attempts.");
            }

            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                long updatedUnix;
                if (!remoteTimes.TryGetValue(id, out updatedUnix) || updatedUnix <= 0L)
                {
                    result.MissingRemoteCount++;
                    continue;
                }

                DateTime updatedUtc = UnixSecondsToUtc(updatedUnix);
                if (updatedUtc <= result.ReferenceUtc)
                {
                    continue;
                }

                List<FastLoaderCacheStateMod> mods = modsById[id];
                for (int j = 0; j < mods.Count; j++)
                {
                    FastLoaderCacheStateMod mod = mods[j];
                    result.UpdatedMods.Add(new WorkshopUpdatedMod
                    {
                        Name = mod.Name ?? mod.PackageId ?? id,
                        PackageId = mod.PackageId ?? string.Empty,
                        WorkshopId = id,
                        UpdatedUtc = updatedUtc
                    });
                }
            }

            if (result.MissingRemoteCount > 0)
            {
                result.Reasons.Add("Some Steam Workshop mods did not return update metadata.");
            }

            if (result.UpdatedMods.Count > 0)
            {
                result.Reasons.Add("Steam Workshop mods were updated after the cache was built.");
            }

            result.Signature = BuildSignature(state, result);
            result.ElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            return result;
        }

        private static WorkshopUpdateCheckResult CreateBaseResult(FastLoaderCacheStateData state, int localModCount)
        {
            WorkshopUpdateCheckResult result = new WorkshopUpdateCheckResult();
            result.ReferenceUtc = state.GetUpdateReferenceUtc();
            result.LocalModCount = localModCount;
            return result;
        }

        private static List<string> BuildLanguageReasons(FastLoaderCacheStateData state)
        {
            List<string> reasons = new List<string>();
            if (state == null)
            {
                return reasons;
            }

            string active = FastLoaderCacheState.GetCurrentActiveLanguage();
            string cachedActive = state.ActiveLanguage ?? string.Empty;
            if (FastLoaderRuntime.ShouldUseXmlCache &&
                !string.IsNullOrEmpty(cachedActive) &&
                !string.Equals(cachedActive, active ?? string.Empty, StringComparison.Ordinal))
            {
                reasons.Add("Active language changed since the language cache was built: " + cachedActive + " -> " + (active ?? string.Empty) + ".");
            }

            string defaultLanguage = FastLoaderCacheState.GetCurrentDefaultLanguage();
            string cachedDefault = state.DefaultLanguage ?? string.Empty;
            if (FastLoaderRuntime.ShouldUseXmlCache &&
                !string.IsNullOrEmpty(cachedDefault) &&
                !string.Equals(cachedDefault, defaultLanguage ?? string.Empty, StringComparison.Ordinal))
            {
                reasons.Add("Default language changed since the language cache was built: " + cachedDefault + " -> " + (defaultLanguage ?? string.Empty) + ".");
            }

            return reasons;
        }

        private static void AddReasons(WorkshopUpdateCheckResult result, List<string> reasons)
        {
            if (result == null || reasons == null)
            {
                return;
            }

            for (int i = 0; i < reasons.Count; i++)
            {
                result.Reasons.Add(reasons[i]);
            }
        }

        private static void LogCheckSummary(WorkshopUpdateCheckResult result)
        {
            Log.Message("[FastLoader] Workshop update check completed in " + FormatSeconds(result.ElapsedMs) +
                ". workshopMods=" + result.WorkshopModCount +
                ", localOrUntrackedMods=" + result.LocalModCount +
                ", batches=" + result.BatchCount +
                ", failedBatches=" + result.FailedBatchCount +
                ", missingRemote=" + result.MissingRemoteCount +
                ", updatedMods=" + result.UpdatedMods.Count);

            for (int i = 0; i < result.FailedBatchMessages.Count; i++)
            {
                Log.Warning("[FastLoader] Workshop update check partial failure: " + result.FailedBatchMessages[i]);
            }
        }

        private static Dictionary<string, long> FetchBatch(List<string> ids)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string body = BuildFormBody(ids);
            using (TimeoutWebClient client = new TimeoutWebClient(RequestTimeoutMs))
            {
                client.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded";
                string response = client.UploadString(Endpoint, "POST", body);
                return ParseUpdateTimes(response);
            }
        }

        private static Dictionary<string, long> FetchBatchWithRetry(List<string> ids)
        {
            Exception lastException = null;
            for (int attempt = 1; attempt <= MaxRequestAttempts; attempt++)
            {
                try
                {
                    return FetchBatch(ids);
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    if (attempt < MaxRequestAttempts)
                    {
                        System.Threading.Thread.Sleep(250 * attempt);
                    }
                }
            }

            throw lastException ?? new InvalidOperationException("Steam request failed");
        }

        private static string BuildFormBody(List<string> ids)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("itemcount=");
            builder.Append(ids.Count);
            for (int i = 0; i < ids.Count; i++)
            {
                builder.Append("&publishedfileids%5B");
                builder.Append(i);
                builder.Append("%5D=");
                builder.Append(Uri.EscapeDataString(ids[i]));
            }

            return builder.ToString();
        }

        private static Dictionary<string, long> ParseUpdateTimes(string json)
        {
            Dictionary<string, long> result = new Dictionary<string, long>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(json))
            {
                return result;
            }

            MatchCollection ids = Regex.Matches(json, "\"publishedfileid\"\\s*:\\s*\"(?<id>\\d+)\"", RegexOptions.CultureInvariant);
            for (int i = 0; i < ids.Count; i++)
            {
                Match idMatch = ids[i];
                int start = idMatch.Index;
                int end = i + 1 < ids.Count ? ids[i + 1].Index : json.Length;
                string segment = json.Substring(start, end - start);
                Match timeMatch = Regex.Match(segment, "\"time_updated\"\\s*:\\s*(?<time>\\d+)", RegexOptions.CultureInvariant);
                if (!timeMatch.Success)
                {
                    continue;
                }

                long unix;
                if (long.TryParse(timeMatch.Groups["time"].Value, out unix))
                {
                    result[idMatch.Groups["id"].Value] = unix;
                }
            }

            return result;
        }

        private static List<FastLoaderCacheStateMod> GetWorkshopMods(FastLoaderCacheStateData state)
        {
            List<FastLoaderCacheStateMod> result = new List<FastLoaderCacheStateMod>();
            if (state == null || state.Mods == null)
            {
                return result;
            }

            for (int i = 0; i < state.Mods.Count; i++)
            {
                FastLoaderCacheStateMod mod = state.Mods[i];
                if (mod != null && IsUnsignedInteger(mod.WorkshopId))
                {
                    result.Add(mod);
                }
            }

            return result;
        }

        private static List<List<string>> SplitBatches(List<string> ids, int batchSize)
        {
            List<List<string>> result = new List<List<string>>();
            for (int i = 0; i < ids.Count; i += batchSize)
            {
                int count = Math.Min(batchSize, ids.Count - i);
                List<string> batch = new List<string>(count);
                for (int j = 0; j < count; j++)
                {
                    batch.Add(ids[i + j]);
                }

                result.Add(batch);
            }

            return result;
        }

        private static string BuildSignature(FastLoaderCacheStateData state, WorkshopUpdateCheckResult result)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(state.InputHash ?? string.Empty);
            builder.Append('|');
            builder.Append(state.GetUpdateReferenceUtc().Ticks);
            for (int i = 0; i < result.Reasons.Count; i++)
            {
                builder.Append("|reason:");
                builder.Append(result.Reasons[i] ?? string.Empty);
            }

            for (int i = 0; i < result.UpdatedMods.Count; i++)
            {
                WorkshopUpdatedMod mod = result.UpdatedMods[i];
                builder.Append('|');
                builder.Append(mod.WorkshopId ?? string.Empty);
                builder.Append(':');
                builder.Append(mod.UpdatedUtc.Ticks);
            }

            return builder.ToString();
        }

        private static string DescribeBatch(List<string> batch)
        {
            if (batch == null || batch.Count == 0)
            {
                return "(empty)";
            }

            if (batch.Count == 1)
            {
                return batch[0];
            }

            return batch[0] + ".." + batch[batch.Count - 1] + " (" + batch.Count + " ids)";
        }

        private static string FormatSeconds(double elapsedMs)
        {
            return (elapsedMs / 1000.0).ToString("0.00") + "s";
        }

        private static DateTime UnixSecondsToUtc(long unixSeconds)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unixSeconds);
        }

        private static bool IsUnsignedInteger(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] < '0' || value[i] > '9')
                {
                    return false;
                }
            }

            return true;
        }

        private sealed class TimeoutWebClient : WebClient
        {
            private readonly int timeoutMs;

            public TimeoutWebClient(int timeoutMs)
            {
                this.timeoutMs = timeoutMs;
            }

            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest request = base.GetWebRequest(address);
                if (request != null)
                {
                    request.Timeout = timeoutMs;
                }

                return request;
            }
        }
    }

    internal sealed class FastLoaderModUpdateDecisionWindow : Window
    {
        private const int MaxVisibleMods = 10;
        private readonly WorkshopUpdateCheckResult result;

        public FastLoaderModUpdateDecisionWindow(WorkshopUpdateCheckResult result)
        {
            this.result = result;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            closeOnCancel = true;
            doCloseX = true;
            forcePause = false;
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(680f, 480f); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "FastLoader cache notice");

            int updatedCount = result != null && result.UpdatedMods != null ? result.UpdatedMods.Count : 0;
            int reasonCount = result != null && result.Reasons != null ? result.Reasons.Count : 0;
            Text.Font = GameFont.Small;
            Rect textRect = new Rect(0f, 42f, inRect.width, 84f);
            Widgets.Label(textRect,
                "FastLoader found cache state warnings after the game reached the main menu.\n" +
                "Reasons: " + reasonCount + ". Updated Workshop mods: " + updatedCount + ".\n" +
                "Continue without rebuilding or build caches again from the current loaded data.");

            Rect listRect = new Rect(0f, 132f, inRect.width, 260f);
            Widgets.DrawMenuSection(listRect);
            DrawReasonsAndUpdatedMods(listRect.ContractedBy(8f));

            const float buttonWidth = 150f;
            const float buttonHeight = 34f;
            const float gap = 8f;
            float y = inRect.height - buttonHeight;
            Rect continueRect = new Rect(inRect.width - buttonWidth, y, buttonWidth, buttonHeight);
            Rect rebuildRect = new Rect(continueRect.x - gap - buttonWidth, y, buttonWidth, buttonHeight);

            if (Widgets.ButtonText(rebuildRect, "Build Cache"))
            {
                Close();
                FastLoaderCacheUiActions.StartBuildAllCaches();
            }

            if (Widgets.ButtonText(continueRect, "Continue"))
            {
                FastLoaderModUpdateChecker.IgnoreCurrentResult(result);
                Close();
            }
        }

        private void DrawReasonsAndUpdatedMods(Rect rect)
        {
            List<string> reasons = result != null ? result.Reasons : null;
            List<WorkshopUpdatedMod> mods = result != null ? result.UpdatedMods : null;
            float rowHeight = 22f;
            int row = 0;

            if (reasons != null)
            {
                for (int i = 0; i < reasons.Count && row < MaxVisibleMods; i++)
                {
                    DrawRow(rect, row, rowHeight, "Reason: " + reasons[i]);
                    row++;
                }
            }

            int count = mods != null ? mods.Count : 0;
            for (int i = 0; i < count && row < MaxVisibleMods; i++)
            {
                DrawRow(rect, row, rowHeight, FormatMod(mods[i]));
                row++;
            }

            int hidden = Math.Max(0, (reasons != null ? reasons.Count : 0) + count - row);
            if (hidden > 0)
            {
                Rect moreRect = new Rect(rect.x + 4f, rect.y + row * rowHeight + 4f, rect.width - 8f, 24f);
                Widgets.Label(moreRect, "... and " + hidden + " more");
            }
        }

        private static void DrawRow(Rect rect, int index, float rowHeight, string text)
        {
            Rect rowRect = new Rect(rect.x, rect.y + index * rowHeight, rect.width, rowHeight);
            if (index % 2 == 1)
            {
                Widgets.DrawLightHighlight(rowRect);
            }

            Widgets.Label(new Rect(rowRect.x + 4f, rowRect.y, rowRect.width - 8f, rowRect.height), text);
        }

        private static string FormatMod(WorkshopUpdatedMod mod)
        {
            string name = mod.Name;
            if (string.IsNullOrEmpty(name))
            {
                name = mod.PackageId;
            }

            if (string.IsNullOrEmpty(name))
            {
                name = mod.WorkshopId;
            }

            return name + "  |  updated " + mod.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
    }

    internal static class FastLoaderCacheUiActions
    {
        private enum CacheFileScope
        {
            All,
            Xml,
            Texture,
            Atlas
        }

        public static void StartBuildAllCaches()
        {
            try
            {
                string diskSpaceMessage;
                if (FastLoaderDiskSpaceGuard.TryGetInsufficientSpaceMessage(out diskSpaceMessage))
                {
                    Log.Warning("[FastLoader] Build Cache skipped by disk space guard.\n" + diskSpaceMessage);
                    ShowFailurePopup("Build Cache warning", diskSpaceMessage);
                    return;
                }

                FastLoaderRuntime.DeleteAllCaches();
                EnsureNoCacheFilesRemain();
                FastLoaderBuildResult result = FastLoaderBridge.BuildXmlAndLanguageCaches();
                if (!result.XmlCacheBuilt)
                {
                    throw new InvalidOperationException("XML cache build failed: " + (FastLoaderRuntime.LastXmlCacheBuildFailureReason ?? "unknown reason"));
                }

                if (result.LanguageCachesBuilt <= 0)
                {
                    throw new InvalidOperationException("Language cache build failed.");
                }

                result.AtlasesSaved = FastLoaderBridge.BuildStaticAtlasCache();
                if (result.AtlasesSaved <= 0)
                {
                    Log.Warning("[FastLoader] Static atlas cache was not built. Continuing Build Cache without atlas cache.");
                }

                Messages.Message("XML/language cache step done. Languages: " + result.LanguageCachesBuilt + ". Atlas caches: " + result.AtlasesSaved + ". Building texture cache...", MessageTypeDefOf.TaskCompletion, false);
                Find.WindowStack.Add(new TextureCacheBuildWindow(TextureCacheBuildFailureCleanup.All));
            }
            catch (Exception ex)
            {
                Log.Error("[FastLoader] Failed to start Build all caches.\n" + ex);
                bool partialRemoved = TryDeleteAfterFailedBuild();
                ShowFailurePopup("Build Cache failed", "FastLoader could not build caches.\n" +
                    (partialRemoved ? "Partial cache files were removed." : "Partial cache files could not be fully removed.") +
                    "\n\nReason: " + FormatException(ex));
            }
        }

        public static void StartBuildXmlCaches()
        {
            try
            {
                string diskSpaceMessage;
                if (FastLoaderDiskSpaceGuard.TryGetInsufficientSpaceMessage(true, false, false, out diskSpaceMessage))
                {
                    Log.Warning("[FastLoader] Build XML Cache skipped by disk space guard.\n" + diskSpaceMessage);
                    ShowFailurePopup("Build XML Cache warning", diskSpaceMessage);
                    return;
                }

                DeleteXmlAndLanguageCaches();
                FastLoaderBuildResult result = FastLoaderBridge.BuildXmlAndLanguageCaches();
                if (!result.XmlCacheBuilt)
                {
                    throw new InvalidOperationException("XML cache build failed: " + (FastLoaderRuntime.LastXmlCacheBuildFailureReason ?? "unknown reason"));
                }

                if (result.LanguageCachesBuilt <= 0)
                {
                    throw new InvalidOperationException("Language cache build failed.");
                }

                Messages.Message("XML cache built. Languages: " + result.LanguageCachesBuilt + ".", MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception ex)
            {
                Log.Error("[FastLoader] Failed to build XML caches.\n" + ex);
                bool partialRemoved = TryDeleteAfterFailedXmlBuild();
                ShowFailurePopup("Build XML Cache failed", "FastLoader could not build XML caches.\n" +
                    (partialRemoved ? "Partial XML cache files were removed." : "Partial XML cache files could not be fully removed.") +
                    "\n\nReason: " + FormatException(ex));
            }
        }

        public static void StartBuildTextureCache()
        {
            try
            {
                string diskSpaceMessage;
                if (FastLoaderDiskSpaceGuard.TryGetInsufficientSpaceMessage(false, true, false, out diskSpaceMessage))
                {
                    Log.Warning("[FastLoader] Build Texture Cache skipped by disk space guard.\n" + diskSpaceMessage);
                    ShowFailurePopup("Build Texture Cache warning", diskSpaceMessage);
                    return;
                }

                FastLoaderCacheState.RemoveStages(FastLoaderCacheKind.Texture);
                Find.WindowStack.Add(new TextureCacheBuildWindow(TextureCacheBuildFailureCleanup.Texture));
            }
            catch (Exception ex)
            {
                Log.Error("[FastLoader] Failed to start texture cache build.\n" + ex);
                bool partialRemoved = TryDeleteAfterFailedTextureBuild();
                ShowFailurePopup("Build Texture Cache failed", "FastLoader could not build texture cache.\n" +
                    (partialRemoved ? "Partial texture cache files were removed." : "Partial texture cache files could not be fully removed.") +
                    "\n\nReason: " + FormatException(ex));
            }
        }

        public static void StartBuildAtlasCache()
        {
            try
            {
                string diskSpaceMessage;
                if (FastLoaderDiskSpaceGuard.TryGetInsufficientSpaceMessage(false, false, true, out diskSpaceMessage))
                {
                    Log.Warning("[FastLoader] Build Atlas Cache skipped by disk space guard.\n" + diskSpaceMessage);
                    ShowFailurePopup("Build Atlas Cache warning", diskSpaceMessage);
                    return;
                }

                StaticAtlasCache.DeleteAll();
                FastLoaderCacheState.RemoveStages(FastLoaderCacheKind.Atlas);
                int count = FastLoaderBridge.BuildStaticAtlasCache();
                if (count > 0)
                {
                    Messages.Message("Atlas cache built: " + count + " atlases saved.", MessageTypeDefOf.TaskCompletion, false);
                }
                else
                {
                    Messages.Message("No static atlases are available yet. Load to the main menu first, then try again.", MessageTypeDefOf.RejectInput, false);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[FastLoader] Failed to build atlas cache.\n" + ex);
                bool partialRemoved = TryDeleteAfterFailedAtlasBuild();
                ShowFailurePopup("Build Atlas Cache failed", "FastLoader could not build atlas cache.\n" +
                    (partialRemoved ? "Partial atlas cache files were removed." : "Partial atlas cache files could not be fully removed.") +
                    "\n\nReason: " + FormatException(ex));
            }
        }

        public static void ResetAllCaches()
        {
            try
            {
                FastLoaderRuntime.DeleteAllCaches();
                EnsureNoCacheFilesRemain();
                Messages.Message("All FastLoader caches cleared. Current load is unchanged; rebuild or restart to use the new state.", MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception ex)
            {
                Log.Error("[FastLoader] Failed to clear all caches.\n" + ex);
                ShowFailurePopup("Remove Cache failed", "FastLoader could not remove cache files.\n\n" + ex.Message);
            }
        }

        public static void ResetXmlCaches()
        {
            try
            {
                DeleteXmlAndLanguageCaches();
                EnsureNoCacheFilesRemain(CacheFileScope.Xml);
                Messages.Message("FastLoader XML caches cleared.", MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception ex)
            {
                Log.Error("[FastLoader] Failed to clear XML caches.\n" + ex);
                ShowFailurePopup("Remove XML Cache failed", "FastLoader could not remove XML cache files.\n\n" + ex.Message);
            }
        }

        public static void ResetTextureCache()
        {
            try
            {
                TextureRawCache.DeleteAll();
                FastLoaderCacheState.RemoveStages(FastLoaderCacheKind.Texture);
                EnsureNoCacheFilesRemain(CacheFileScope.Texture);
                Messages.Message("FastLoader texture cache cleared.", MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception ex)
            {
                Log.Error("[FastLoader] Failed to clear texture cache.\n" + ex);
                ShowFailurePopup("Remove Texture Cache failed", "FastLoader could not remove texture cache files.\n\n" + ex.Message);
            }
        }

        public static void ResetAtlasCache()
        {
            try
            {
                StaticAtlasCache.DeleteAll();
                FastLoaderCacheState.RemoveStages(FastLoaderCacheKind.Atlas);
                EnsureNoCacheFilesRemain(CacheFileScope.Atlas);
                Messages.Message("FastLoader atlas cache cleared.", MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception ex)
            {
                Log.Error("[FastLoader] Failed to clear atlas cache.\n" + ex);
                ShowFailurePopup("Remove Atlas Cache failed", "FastLoader could not remove atlas cache files.\n\n" + ex.Message);
            }
        }

        public static void ReportBuildFailureAndDeleteCaches(string detail, Exception exception)
        {
            Log.Error("[FastLoader] Build Cache failed.\n" + exception);
            bool partialRemoved = TryDeleteAfterFailedBuild();
            ShowFailurePopup("Build Cache failed", "FastLoader could not build caches.\n" +
                (partialRemoved ? "Partial cache files were removed." : "Partial cache files could not be fully removed.") +
                "\n\n" + (detail ?? "Unknown failure.") +
                "\nReason: " + FormatException(exception));
        }

        public static void ReportTextureBuildFailureAndDeleteTextureCache(string detail, Exception exception)
        {
            Log.Error("[FastLoader] Build Texture Cache failed.\n" + exception);
            bool partialRemoved = TryDeleteAfterFailedTextureBuild();
            ShowFailurePopup("Build Texture Cache failed", "FastLoader could not build texture cache.\n" +
                (partialRemoved ? "Partial texture cache files were removed." : "Partial texture cache files could not be fully removed.") +
                "\n\n" + (detail ?? "Unknown failure.") +
                "\nReason: " + FormatException(exception));
        }

        private static bool TryDeleteAfterFailedBuild()
        {
            try
            {
                FastLoaderRuntime.DeleteAllCaches();
                EnsureNoCacheFilesRemain();
                return true;
            }
            catch (Exception deleteEx)
            {
                Log.Error("[FastLoader] Failed to remove partial cache files after build failure.\n" + deleteEx);
                return false;
            }
        }

        private static bool TryDeleteAfterFailedXmlBuild()
        {
            try
            {
                DeleteXmlAndLanguageCaches();
                EnsureNoCacheFilesRemain(CacheFileScope.Xml);
                return true;
            }
            catch (Exception deleteEx)
            {
                Log.Error("[FastLoader] Failed to remove partial XML cache files after build failure.\n" + deleteEx);
                return false;
            }
        }

        private static bool TryDeleteAfterFailedTextureBuild()
        {
            try
            {
                TextureRawCache.DeleteAll();
                FastLoaderCacheState.RemoveStages(FastLoaderCacheKind.Texture);
                EnsureNoCacheFilesRemain(CacheFileScope.Texture);
                return true;
            }
            catch (Exception deleteEx)
            {
                Log.Error("[FastLoader] Failed to remove partial texture cache files after build failure.\n" + deleteEx);
                return false;
            }
        }

        private static bool TryDeleteAfterFailedAtlasBuild()
        {
            try
            {
                StaticAtlasCache.DeleteAll();
                FastLoaderCacheState.RemoveStages(FastLoaderCacheKind.Atlas);
                EnsureNoCacheFilesRemain(CacheFileScope.Atlas);
                return true;
            }
            catch (Exception deleteEx)
            {
                Log.Error("[FastLoader] Failed to remove partial atlas cache files after build failure.\n" + deleteEx);
                return false;
            }
        }

        private static void DeleteXmlAndLanguageCaches()
        {
            FastLoaderRuntime.DeleteXmlCache();
            LanguageBinaryCache.DeleteAll();
            FastLoaderCacheState.RemoveStages(FastLoaderCacheKind.Xml, FastLoaderCacheKind.Language);
        }

        private static void EnsureNoCacheFilesRemain()
        {
            EnsureNoCacheFilesRemain(CacheFileScope.All);
        }

        private static void EnsureNoCacheFilesRemain(CacheFileScope scope)
        {
            string root = Path.Combine(GenFilePaths.ConfigFolderPath, "FastLoader");
            if (!Directory.Exists(root))
            {
                return;
            }

            string[] remaining = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            for (int i = 0; i < remaining.Length; i++)
            {
                string path = remaining[i];
                string extension = Path.GetExtension(path);
                string fileName = Path.GetFileName(path);
                if (IsCacheFileForScope(path, fileName, extension, scope))
                {
                    throw new IOException("Cache file still exists: " + path);
                }
            }
        }

        private static bool IsCacheFileForScope(string path, string fileName, string extension, CacheFileScope scope)
        {
            if (string.Equals(fileName, "cache_state.xml.tmp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (scope == CacheFileScope.All)
            {
                return string.Equals(fileName, "manifest.xml", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, "resolved_defs.xml", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, "cache_state.xml", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".tmp", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".texcache", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".flang", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".finj", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".atlascache", StringComparison.OrdinalIgnoreCase);
            }

            if (scope == CacheFileScope.Xml)
            {
                return string.Equals(fileName, "manifest.xml", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, "resolved_defs.xml", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".flang", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".finj", StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(extension, ".tmp", StringComparison.OrdinalIgnoreCase) && IsInCacheFolder(path, "Cache")) ||
                    (string.Equals(extension, ".tmp", StringComparison.OrdinalIgnoreCase) && IsInCacheFolder(path, "LanguageCache"));
            }

            if (scope == CacheFileScope.Texture)
            {
                return string.Equals(extension, ".texcache", StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(extension, ".tmp", StringComparison.OrdinalIgnoreCase) && IsInCacheFolder(path, "TextureCache"));
            }

            return string.Equals(extension, ".atlascache", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(extension, ".tmp", StringComparison.OrdinalIgnoreCase) && IsInCacheFolder(path, "StaticAtlasCache"));
        }

        private static bool IsInCacheFolder(string path, string folderName)
        {
            return path != null && path.IndexOf(Path.DirectorySeparatorChar + folderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ShowFailurePopup(string title, string message)
        {
            Find.WindowStack.Add(new Dialog_MessageBox(
                message ?? "FastLoader operation failed.",
                "OK",
                null,
                null,
                null,
                title,
                false));
        }

        private static string FormatException(Exception exception)
        {
            if (exception == null)
            {
                return "unknown error";
            }

            Exception root = exception.GetBaseException();
            return root.GetType().Name + ": " + root.Message;
        }
    }
}
