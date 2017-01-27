using System;
using System.Collections.Generic;
using System.IO;

namespace Ftpush.Internal {
    public class LocalDirectory : LocalItem {
        private readonly DirectoryInfo _info;

        public LocalDirectory(DirectoryInfo info, string relativePath, int depth) : base(info, relativePath, depth) {
            _info = info;
        }

        public IEnumerable<LocalItem> EnumerateChildren() {
            foreach (var childInfo in _info.EnumerateFileSystemInfos()) {
                var directoryInfo = childInfo as DirectoryInfo;
                if (directoryInfo != null) {
                    yield return new LocalDirectory(directoryInfo, Path.Combine(RelativePath, directoryInfo.Name), Depth + 1);
                    continue;
                }

                yield return new LocalFile((FileInfo)childInfo, Path.Combine(RelativePath, childInfo.Name), Depth + 1);
            }
        }
    }
}
