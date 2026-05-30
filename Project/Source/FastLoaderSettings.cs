using Verse;

namespace FastLoader
{
    public sealed class FastLoaderSettings : ModSettings
    {
        public bool CacheEnabled = true;
        public bool ForceRebuildOnNextLoad;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref CacheEnabled, "cacheEnabled", true);
            Scribe_Values.Look(ref ForceRebuildOnNextLoad, "forceRebuildOnNextLoad", false);
        }
    }
}
