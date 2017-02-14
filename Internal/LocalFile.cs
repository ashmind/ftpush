using System;
using System.IO;

namespace Ftpush.Internal {
    public class LocalFile : LocalItem {
        private readonly FileInfo _info;

        public LocalFile(FileInfo info, string relativePath, int depth) : base(info, relativePath, depth) {
            _info = info;
        }

        public DateTime LastWriteTime => _info.LastWriteTime;
        public DateTime LastWriteTimeUtc => _info.LastWriteTimeUtc;
        public Stream OpenRead() => _info.OpenRead();
    }
}
