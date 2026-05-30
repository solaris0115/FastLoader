using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FastLoader
{
    public sealed class FastLoaderMod : Mod
    {
        private readonly FastLoaderSettings settings;

        public FastLoaderMod(ModContentPack content) : base(content)
        {
            settings = GetSettings<FastLoaderSettings>();
            FastLoaderRuntime.Settings = settings;

            try
            {
                FastLoaderRuntime.ModContent = content;
                new Harmony("solaris.fastloader").PatchAll(typeof(FastLoaderMod).Assembly);
                Log.Message("[FastLoader] Harmony patches installed.");
            }
            catch (Exception ex)
            {
                Log.Error("[FastLoader] Failed to install Harmony patches. FastLoader will not run.\n" + ex);
            }
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            bool enabled = settings.CacheEnabled;
            listing.CheckboxLabeled("Enable FastLoader cache", ref enabled, null, 30f, 1f);
            if (enabled != settings.CacheEnabled)
            {
                settings.CacheEnabled = enabled;
                settings.Write();
            }

            listing.Gap(8f);
            Rect buttonRow = listing.GetRect(30f);
            const float gap = 2f;
            float buttonWidth = Mathf.Min(150f, (buttonRow.width - gap * 3f) / 4f);
            Rect resourceClearRect = new Rect(buttonRow.xMax - buttonWidth, buttonRow.y, buttonWidth, buttonRow.height);
            Rect xmlClearRect = new Rect(resourceClearRect.x - gap - buttonWidth, buttonRow.y, buttonWidth, buttonRow.height);
            Rect prepareRect = new Rect(xmlClearRect.x - gap - buttonWidth, buttonRow.y, buttonWidth, buttonRow.height);
            Rect buildRect = new Rect(prepareRect.x - gap - buttonWidth, buttonRow.y, buttonWidth, buttonRow.height);

            if (Widgets.ButtonText(buildRect, "Build all cache"))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "Build all FastLoader caches? XML cache will be written now when a resolved XML snapshot is available. Otherwise it will be rebuilt on the next game load. Texture resource cache build will start now.",
                    BuildAllCacheFromSettings,
                    true));
            }

            if (Widgets.ButtonText(prepareRect, "Prepare resource build"))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "Prepare FastLoader texture resource cache build files? Unity will not be started. Run the generated command file outside RimWorld to build the cache.",
                    PrepareResourceBuildFromSettings,
                    true));
            }

            if (Widgets.ButtonText(xmlClearRect, "Reset XML cache"))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "Clear FastLoader XML cache files? The XML cache will be rebuilt on the next load.",
                    DeleteXmlCacheFromSettings,
                    true));
            }

            if (Widgets.ButtonText(resourceClearRect, "Reset resource cache"))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "Clear FastLoader texture/resource cache files?",
                    DeleteResourceCacheFromSettings,
                    true));
            }
            listing.End();
        }

        private static void DeleteXmlCacheFromSettings()
        {
            try
            {
                FastLoaderRuntime.DeleteXmlCache();
                Messages.Message("FastLoader XML cache cleared.", MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception ex)
            {
                Log.Error("[FastLoader] Failed to clear XML cache files.\n" + ex);
                Messages.Message("Failed to clear FastLoader XML cache. See log for details.", MessageTypeDefOf.RejectInput, false);
            }
        }

        private static void DeleteResourceCacheFromSettings()
        {
            try
            {
                FastLoaderRuntime.DeleteResourceCache();
                Messages.Message("FastLoader resource cache cleared.", MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception ex)
            {
                Log.Error("[FastLoader] Failed to clear resource cache files.\n" + ex);
                Messages.Message("Failed to clear FastLoader resource cache. See log for details.", MessageTypeDefOf.RejectInput, false);
            }
        }

        private static void BuildAllCacheFromSettings()
        {
            try
            {
                bool xmlBuiltNow = FastLoaderRuntime.RequestAllCacheBuildFromSettings();
                Messages.Message(xmlBuiltNow
                    ? "FastLoader cache build requested. XML cache was written now."
                    : "FastLoader cache build requested. XML cache will be regenerated on next game load.",
                    MessageTypeDefOf.TaskCompletion,
                    false);
            }
            catch (Exception ex)
            {
                Log.Error("[FastLoader] Failed to request cache build.\n" + ex);
                Messages.Message("Failed to request FastLoader cache build. See log for details.", MessageTypeDefOf.RejectInput, false);
            }
        }

        private static void PrepareResourceBuildFromSettings()
        {
            try
            {
                string commandPath = FastLoaderAssetBundleCache.PrepareExternalBuildFromSettings(FastLoaderHasher.ComputeFastModListHash());
                if (string.IsNullOrEmpty(commandPath))
                {
                    Messages.Message("Failed to prepare FastLoader resource build. See log for details.", MessageTypeDefOf.RejectInput, false);
                    return;
                }

                Messages.Message("FastLoader resource build command prepared: " + commandPath, MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception ex)
            {
                Log.Error("[FastLoader] Failed to prepare resource cache build.\n" + ex);
                Messages.Message("Failed to prepare FastLoader resource build. See log for details.", MessageTypeDefOf.RejectInput, false);
            }
        }

        public override string SettingsCategory()
        {
            return "FastLoader";
        }
    }
}
