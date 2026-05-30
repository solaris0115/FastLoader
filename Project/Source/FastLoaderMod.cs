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
            const float gap = 4f;
            float buttonWidth = Mathf.Min(180f, (buttonRow.width - gap * 2f) / 3f);
            Rect texClearRect = new Rect(buttonRow.xMax - buttonWidth, buttonRow.y, buttonWidth, buttonRow.height);
            Rect xmlClearRect = new Rect(texClearRect.x - gap - buttonWidth, buttonRow.y, buttonWidth, buttonRow.height);
            Rect resetAllRect = new Rect(xmlClearRect.x - gap - buttonWidth, buttonRow.y, buttonWidth, buttonRow.height);

            if (Widgets.ButtonText(resetAllRect, "Reset all caches"))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "Clear all FastLoader caches (XML + texture)? They will be automatically rebuilt on next load.",
                    ResetAllCachesFromSettings,
                    true));
            }

            if (Widgets.ButtonText(xmlClearRect, "Reset XML cache"))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "Clear FastLoader XML cache files? The XML cache will be rebuilt on the next load.",
                    DeleteXmlCacheFromSettings,
                    true));
            }

            if (Widgets.ButtonText(texClearRect, "Reset texture cache"))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "Clear FastLoader texture cache files? They will be rebuilt automatically on next load.",
                    DeleteResourceCacheFromSettings,
                    true));
            }

            listing.Gap(12f);
            listing.Label("Texture cache: " + TextureRawCache.GetStatusSummary());

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

        private static void ResetAllCachesFromSettings()
        {
            try
            {
                FastLoaderRuntime.DeleteAllCaches();
                Messages.Message("All FastLoader caches cleared. They will be rebuilt on next load.", MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception ex)
            {
                Log.Error("[FastLoader] Failed to clear all caches.\n" + ex);
                Messages.Message("Failed to clear FastLoader caches. See log for details.", MessageTypeDefOf.RejectInput, false);
            }
        }

        public override string SettingsCategory()
        {
            return "FastLoader";
        }
    }
}
