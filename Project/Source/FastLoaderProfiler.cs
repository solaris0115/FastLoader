using System;
using Verse;

namespace FastLoader
{
    internal sealed class FastLoaderProfileScope : IDisposable
    {
        private readonly string name;
        private readonly ProfileScope localScope;
        private readonly long startupTimestamp;
        private bool disposed;

        public FastLoaderProfileScope(string name)
        {
            this.name = string.IsNullOrEmpty(name) ? "(unnamed)" : name;
            localScope = FastProfile.Scope(this.name);
            startupTimestamp = StartupProfilerBridge.Begin(this.name);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            if (localScope != null)
            {
                localScope.Dispose();
            }

            StartupProfilerBridge.End(name, startupTimestamp);
        }
    }

    internal static class FastLoaderProfiler
    {
        public static FastLoaderProfileScope Scope(string name)
        {
            return new FastLoaderProfileScope(name);
        }

        public static string DescribeMod(ModContentPack mod)
        {
            if (mod == null)
            {
                return "(null mod)";
            }

            string name = mod.Name;
            string packageId = mod.PackageId;
            if (string.IsNullOrEmpty(name))
            {
                name = "(unnamed mod)";
            }

            if (string.IsNullOrEmpty(packageId))
            {
                return name;
            }

            return name + " [" + packageId + "]";
        }
    }
}
