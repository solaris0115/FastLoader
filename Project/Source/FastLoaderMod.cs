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
        private bool showMissingCacheList;
        private Vector2 missingCacheListScroll;

        public FastLoaderMod(ModContentPack content) : base(content)
        {
            settings = GetSettings<FastLoaderSettings>();
            FastLoaderRuntime.Settings = settings;

            try
            {
                FastLoaderRuntime.ModContent = content;
                Harmony harmony = new Harmony("solaris.fastloader");
                harmony.PatchAll(typeof(FastLoaderMod).Assembly);
                HarmonyPatchProfiler.TryInstall(harmony);
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
                    "Clear all FastLoader cache files (XML + texture + language)? Use the build buttons to create XML and texture caches again. Language cache will rebuild on the next load.",
                    ResetAllCachesFromSettings,
                    true));
            }

            if (Widgets.ButtonText(xmlClearRect, "Reset XML cache"))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "Clear FastLoader XML cache files? Use Build XML cache now to create them again.",
                    DeleteXmlCacheFromSettings,
                    true));
            }

            if (Widgets.ButtonText(texClearRect, "Reset texture cache"))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "Clear FastLoader texture cache files? Use Build texture cache now to create them again.",
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
                Find.WindowStack.Add(new TextureCacheBuildWindow());
            }
            if (Widgets.ButtonText(buildXmlRect, "Build XML cache now"))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "Write XML cache from the current resolved defs in memory?",
                    BuildXmlCacheFromMemory,
                    true));
            }

            listing.Gap(12f);
            listing.Label("Texture cache: " + TextureRawCache.GetStatusSummary());

            List<string> missingCacheList = TextureRawCache.GetModsWithoutCurrentCacheInfo();
            listing.Gap(4f);
            Rect missingRow = listing.GetRect(24f);
            Widgets.Label(missingRow, "Mods without texture cache info: " + missingCacheList.Count);
            if (missingCacheList.Count > 0)
            {
                Rect toggleRect = new Rect(missingRow.xMax - 60f, missingRow.y, 60f, missingRow.height);
                if (Widgets.ButtonText(toggleRect, showMissingCacheList ? "Hide" : "Show"))
                {
                    showMissingCacheList = !showMissingCacheList;
                }

                if (showMissingCacheList)
                {
                    listing.Gap(4f);
                    float listHeight = Mathf.Min(missingCacheList.Count * 22f, 200f);
                    Rect scrollOuter = listing.GetRect(listHeight);
                    Rect scrollInner = new Rect(0f, 0f, scrollOuter.width - 16f, missingCacheList.Count * 22f);
                    Widgets.BeginScrollView(scrollOuter, ref missingCacheListScroll, scrollInner);
                    float y = 0f;
                    for (int i = 0; i < missingCacheList.Count; i++)
                    {
                        Rect row = new Rect(0f, y, scrollInner.width, 22f);
                        if (i % 2 == 1)
                        {
                            Widgets.DrawLightHighlight(row);
                        }
                        Widgets.Label(row, "  " + missingCacheList[i]);
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
                Messages.Message("All FastLoader caches cleared.", MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception ex)
            {
                Log.Error("[FastLoader] Failed to clear all caches.\n" + ex);
                Messages.Message("Failed to clear FastLoader caches. See log for details.", MessageTypeDefOf.RejectInput, false);
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
