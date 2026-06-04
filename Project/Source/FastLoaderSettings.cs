using System;
using Verse;

namespace FastLoader
{
    public sealed class FastLoaderSettings : ModSettings
    {
        public const int DefaultAtlasCacheChunkSizeMb = 32;
        public const int MinAtlasCacheChunkSizeMb = 4;
        public const int MaxAtlasCacheChunkSizeMb = 128;

        public bool CacheEnabled = true;
        public bool AtlasCacheCompressionEnabled = true;
        public int AtlasCacheChunkSizeMb = DefaultAtlasCacheChunkSizeMb;
        public string IgnoredWorkshopUpdateSignature = string.Empty;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref CacheEnabled, "cacheEnabled", true);
            Scribe_Values.Look(ref AtlasCacheCompressionEnabled, "atlasCacheCompressionEnabled", true);
            Scribe_Values.Look(ref AtlasCacheChunkSizeMb, "atlasCacheChunkSizeMb", DefaultAtlasCacheChunkSizeMb);
            Scribe_Values.Look(ref IgnoredWorkshopUpdateSignature, "ignoredWorkshopUpdateSignature", string.Empty);
            NormalizeAtlasCacheSettings();
        }

        public void NormalizeAtlasCacheSettings()
        {
            AtlasCacheChunkSizeMb = ClampAtlasCacheChunkSizeMb(AtlasCacheChunkSizeMb);
        }

        public static int ClampAtlasCacheChunkSizeMb(int value)
        {
            return Math.Max(MinAtlasCacheChunkSizeMb, Math.Min(MaxAtlasCacheChunkSizeMb, value));
        }
    }
}
