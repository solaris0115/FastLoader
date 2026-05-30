using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FastLoader
{
    public sealed class FastLoaderMod : Mod
    {
        private readonly FastLoaderSettings settings;
        private bool showMissList;
        private Vector2 missListScroll;

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

            listing.Gap(8f);
            Rect buildRow = listing.GetRect(30f);
            float buildButtonWidth = Mathf.Min(200f, (buildRow.width - 8f) * 0.5f);
            Rect buildTexRect = new Rect(buildRow.x, buildRow.y, buildButtonWidth, buildRow.height);
            Rect buildXmlRect = new Rect(buildTexRect.xMax + 8f, buildRow.y, buildButtonWidth, buildRow.height);
            if (Widgets.ButtonText(buildTexRect, "Build texture cache now"))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "Build texture cache from currently loaded textures in memory?\nThis may take a few seconds.",
                    BuildTextureCacheFromMemory,
                    true));
            }
            if (Widgets.ButtonText(buildXmlRect, "Build XML cache now"))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "Build XML cache from current resolved defs in memory?",
                    BuildXmlCacheFromMemory,
                    true));
            }

            listing.Gap(12f);
            listing.Label("Texture cache: " + TextureRawCache.GetStatusSummary());

            List<string> missList = TextureRawCache.GetCacheMissList();
            if (missList != null && missList.Count > 0)
            {
                listing.Gap(4f);
                Rect missRow = listing.GetRect(24f);
                Widgets.Label(missRow, "Cache misses: " + missList.Count);
                Rect toggleRect = new Rect(missRow.xMax - 60f, missRow.y, 60f, missRow.height);
                if (Widgets.ButtonText(toggleRect, showMissList ? "Hide" : "Show"))
                {
                    showMissList = !showMissList;
                }

                if (showMissList)
                {
                    listing.Gap(4f);
                    float listHeight = Mathf.Min(missList.Count * 22f, 200f);
                    Rect scrollOuter = listing.GetRect(listHeight);
                    Rect scrollInner = new Rect(0f, 0f, scrollOuter.width - 16f, missList.Count * 22f);
                    Widgets.BeginScrollView(scrollOuter, ref missListScroll, scrollInner);
                    float y = 0f;
                    for (int i = 0; i < missList.Count; i++)
                    {
                        Rect row = new Rect(0f, y, scrollInner.width, 22f);
                        if (i % 2 == 1)
                        {
                            Widgets.DrawLightHighlight(row);
                        }
                        Widgets.Label(row, "  " + missList[i]);
                        y += 22f;
                    }
                    Widgets.EndScrollView();
                }
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

        private static void BuildTextureCacheFromMemory()
        {
            try
            {
                int count = TextureRawCache.RebuildFromLoadedMods();
                Messages.Message("Texture cache built: " + count + " textures saved.", MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception ex)
            {
                Log.Error("[FastLoader] Failed to build texture cache from memory.\n" + ex);
                Messages.Message("Failed to build texture cache. See log for details.", MessageTypeDefOf.RejectInput, false);
            }
        }

        private static void BuildXmlCacheFromMemory()
        {
            try
            {
                bool success = FastLoaderRuntime.WriteXmlCacheFromCurrentSnapshot();
                if (success)
                {
                    Messages.Message("XML cache built from current resolved defs.", MessageTypeDefOf.TaskCompletion, false);
                }
                else
                {
                    Messages.Message("XML cache not available yet. Load the game first, then try again.", MessageTypeDefOf.RejectInput, false);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[FastLoader] Failed to build XML cache from memory.\n" + ex);
                Messages.Message("Failed to build XML cache. See log for details.", MessageTypeDefOf.RejectInput, false);
            }
        }

        public override string SettingsCategory()
        {
            return "FastLoader";
        }
    }
}
