using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace FastLoader
{
    internal sealed class TextureCacheBuildWindow : Window
    {
        private readonly TextureRawCache.TextureBuildSession session;
        private bool completionHandled;

        public TextureCacheBuildWindow()
        {
            session = TextureRawCache.StartRebuildFromLoadedMods();
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            closeOnCancel = false;
            doCloseX = false;
            forcePause = false;
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(620f, 390f); }
        }

        public override void WindowUpdate()
        {
            base.WindowUpdate();

            if (!session.Finished)
            {
                session.Step();
                return;
            }

            if (!completionHandled && !session.Cancelled)
            {
                completionHandled = true;
                Messages.Message("Texture cache built: " + session.SavedTextureCount + " textures saved.", MessageTypeDefOf.TaskCompletion, false);
                Close();
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "Build texture cache");

            Text.Font = GameFont.Small;
            Rect progressRect = new Rect(0f, 42f, inRect.width, 24f);
            Widgets.FillableBar(progressRect, session.Progress);
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(progressRect, session.ProcessedModCount + " / " + session.TotalMods);
            Text.Anchor = TextAnchor.UpperLeft;

            string status = session.Finished
                ? "Done. Cached mods: " + session.CachedModCount + ", textures: " + session.SavedTextureCount
                : "Processing: " + session.CurrentModName;
            Widgets.Label(new Rect(0f, 74f, inRect.width, 24f), status);

            Rect listRect = new Rect(0f, 108f, inRect.width, 226f);
            Widgets.DrawMenuSection(listRect);
            DrawRows(listRect.ContractedBy(8f));

            Rect closeRect = new Rect(inRect.width - 120f, inRect.height - 32f, 120f, 32f);
            if (session.Finished)
            {
                if (Widgets.ButtonText(closeRect, "Close"))
                {
                    Close();
                }
            }
            else
            {
                if (Widgets.ButtonText(closeRect, "Cancel"))
                {
                    session.Cancel();
                    Close();
                }
            }
        }

        private void DrawRows(Rect rect)
        {
            List<TextureRawCache.BuildDisplayRow> rows = session.GetDisplayRows();
            float rowHeight = 21f;
            for (int i = 0; i < 10; i++)
            {
                Rect rowRect = new Rect(rect.x, rect.y + i * rowHeight, rect.width, rowHeight);
                if (i % 2 == 1)
                {
                    Widgets.DrawLightHighlight(rowRect);
                }

                if (i >= rows.Count)
                {
                    continue;
                }

                TextureRawCache.BuildDisplayRow row = rows[i];
                Rect nameRect = new Rect(rowRect.x + 4f, rowRect.y, rowRect.width - 8f, rowRect.height);
                Widgets.Label(nameRect, row.ModName);
            }
        }
    }
}
