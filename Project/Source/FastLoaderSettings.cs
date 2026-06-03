using Verse;

namespace FastLoader
{
    public sealed class FastLoaderSettings : ModSettings
    {
        public bool CacheEnabled = true;
        public string IgnoredWorkshopUpdateSignature = string.Empty;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref CacheEnabled, "cacheEnabled", true);
            Scribe_Values.Look(ref IgnoredWorkshopUpdateSignature, "ignoredWorkshopUpdateSignature", string.Empty);
        }
    }
}
