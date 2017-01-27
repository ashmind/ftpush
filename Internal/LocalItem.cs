using System.IO;

namespace Ftpush.Internal {
    public abstract class LocalItem {
        private readonly FileSystemInfo _info;

        public string RelativePath { get; }
        public int Depth { get; }
        public string Name => _info.Name;

        protected LocalItem(FileSystemInfo info, string relativePath, int depth) {
            _info = info;
            RelativePath = relativePath;
            Depth = depth;
        }
    }
}