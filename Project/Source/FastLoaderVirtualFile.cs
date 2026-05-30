using System.IO;
using RimWorld.IO;

namespace FastLoader
{
    internal sealed class FastLoaderVirtualFile : VirtualFile
    {
        private readonly string fullPath;
        private readonly string name;

        public FastLoaderVirtualFile(string fullPath, string name)
        {
            this.fullPath = fullPath ?? string.Empty;
            this.name = name ?? Path.GetFileName(this.fullPath);
        }

        public override string Name
        {
            get { return name; }
        }

        public override string FullPath
        {
            get { return fullPath; }
        }

        public override bool Exists
        {
            get { return File.Exists(fullPath); }
        }

        public override long Length
        {
            get { return Exists ? new FileInfo(fullPath).Length : 0L; }
        }

        public override Stream CreateReadStream()
        {
            return File.OpenRead(fullPath);
        }

        public override string ReadAllText()
        {
            return File.ReadAllText(fullPath);
        }

        public override string[] ReadAllLines()
        {
            return File.ReadAllLines(fullPath);
        }

        public override byte[] ReadAllBytes()
        {
            return File.ReadAllBytes(fullPath);
        }

        public override string ToString()
        {
            return "FastLoaderVirtualFile [" + fullPath + "]";
        }
    }
}
