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
                    "Clear all FastLoader cache files (XML + texture + language)? Use the build buttons to create them again.",
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
            float buildButtonWidth = Mathf.Min(190f, (buildRow.width - gap * 2f) / 3f);
            Rect buildAllRect = new Rect(buildRow.x, buildRow.y, buildButtonWidth, buildRow.height);
            Rect buildTexRect = new Rect(buildAllRect.xMax + gap, buildRow.y, buildButtonWidth, buildRow.height);
            Rect buildXmlRect = new Rect(buildTexRect.xMax + gap, buildRow.y, buildButtonWidth, buildRow.height);
            if (Widgets.ButtonText(buildAllRect, "Build all caches now"))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "Build XML, language, and texture caches from the current loaded data?",
                    BuildAllCachesFromSettings,
                    true));
            }
            if (Widgets.ButtonText(buildTexRect, "Build texture cache now"))
            {
                Find.WindowStack.Add(new TextureCacheBuildWindow());
            }
            if (Widgets.ButtonText(buildXmlRect, "Build XML cache now"))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "Write XML and language caches from the current loaded data?",
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
                FastLoaderBuildResult result = FastLoaderBridge.BuildXmlAndLanguageCaches();
                if (result.XmlCacheBuilt)
                {
                    Messages.Message("XML and language caches built. Languages: " + result.LanguageCachesBuilt + ".", MessageTypeDefOf.TaskCompletion, false);
                }
                else if (result.LanguageCachesBuilt > 0)
                {
                    Messages.Message("Language cache built. XML cache is not available yet.", MessageTypeDefOf.TaskCompletion, false);
                }
                else
                {
                    Messages.Message("XML/language cache not available yet. Load the game first, then try again.", MessageTypeDefOf.RejectInput, false);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[FastLoader] Failed to build XML/language cache from memory.\n" + ex);
                Messages.Message("Failed to build XML/language cache. See log for details.", MessageTypeDefOf.RejectInput, false);
            }
        }

        private static void BuildAllCachesFromSettings()
        {
            try
            {
                FastLoaderBuildResult result = FastLoaderBridge.BuildXmlAndLanguageCaches();
                Messages.Message("XML/language cache step done. Languages: " + result.LanguageCachesBuilt + ". Building texture cache...", MessageTypeDefOf.TaskCompletion, false);
                Find.WindowStack.Add(new TextureCacheBuildWindow());
            }
            catch (Exception ex)
            {
                Log.Error("[FastLoader] Failed to start Build all caches.\n" + ex);
                Messages.Message("Failed to build FastLoader caches. See log for details.", MessageTypeDefOf.RejectInput, false);
            }
        }

        public override string SettingsCategory()
        {
            return "FastLoader";
        }
    }
}
