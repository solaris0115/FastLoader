using Verse;

namespace FastLoader
{
    public sealed class FastLoaderSettings : ModSettings
    {
        public bool CacheEnabled = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref CacheEnabled, "cacheEnabled", true);
        }
    }
}
