using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace FastLoader
{
    internal sealed class FastLoaderCacheIssue
    {
        public string Stage;
        public string Detail;
    }

    internal static class FastLoaderCacheIssueReporter
    {
        private static readonly List<FastLoaderCacheIssue> Issues = new List<FastLoaderCacheIssue>();
        private static bool shown;

        public static void Report(string stage, string detail)
        {
            Issues.Add(new FastLoaderCacheIssue
            {
                Stage = stage ?? "cache",
                Detail = detail ?? "unknown failure"
            });
            Log.Warning("[FastLoader] Cache fallback activated at " + (stage ?? "cache") + ": " + (detail ?? "unknown failure"));
        }

        public static void PollUi()
        {
            if (shown || Issues.Count == 0)
            {
                return;
            }

            shown = true;
            Find.WindowStack.Add(new FastLoaderCacheFailureWindow(new List<FastLoaderCacheIssue>(Issues)));
        }
    }

    internal sealed class FastLoaderCacheFailureWindow : Window
    {
        private readonly List<FastLoaderCacheIssue> issues;

        public FastLoaderCacheFailureWindow(List<FastLoaderCacheIssue> issues)
        {
            this.issues = issues ?? new List<FastLoaderCacheIssue>();
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            closeOnCancel = true;
            doCloseX = true;
            forcePause = false;
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(680f, 420f); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "FastLoader cache fallback");

            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, 42f, inRect.width, 64f),
                "A FastLoader cache step failed during load.\n" +
                "FastLoader disabled the remaining cache paths for this load and used vanilla fallback where possible.");

            Rect listRect = new Rect(0f, 112f, inRect.width, 220f);
            Widgets.DrawMenuSection(listRect);
            DrawIssues(listRect.ContractedBy(8f));

            const float buttonWidth = 150f;
            const float buttonHeight = 34f;
            const float gap = 8f;
            Rect continueRect = new Rect(inRect.width - buttonWidth, inRect.height - buttonHeight, buttonWidth, buttonHeight);
            Rect buildRect = new Rect(continueRect.x - gap - buttonWidth, continueRect.y, buttonWidth, buttonHeight);

            if (Widgets.ButtonText(buildRect, "Build Cache"))
            {
                Close();
                FastLoaderCacheUiActions.StartBuildAllCaches();
            }

            if (Widgets.ButtonText(continueRect, "Continue"))
            {
                Close();
            }
        }

        private void DrawIssues(Rect rect)
        {
            int count = Math.Min(issues.Count, 8);
            float rowHeight = 24f;
            for (int i = 0; i < count; i++)
            {
                FastLoaderCacheIssue issue = issues[i];
                Rect rowRect = new Rect(rect.x, rect.y + i * rowHeight, rect.width, rowHeight);
                if (i % 2 == 1)
                {
                    Widgets.DrawLightHighlight(rowRect);
                }

                Widgets.Label(new Rect(rowRect.x + 4f, rowRect.y, rowRect.width - 8f, rowRect.height),
                    issue.Stage + ": " + issue.Detail);
            }

            if (issues.Count > count)
            {
                Widgets.Label(new Rect(rect.x + 4f, rect.y + count * rowHeight + 4f, rect.width - 8f, 24f),
                    "... and " + (issues.Count - count) + " more");
            }
        }
    }
}
