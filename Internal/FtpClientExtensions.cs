using System;
using FluentFTP;

namespace Ftpush.Internal {
    public static class FtpClientExtensions {
        public static void SetModifiedTime(this FtpClient client, string path, DateTime time) {
            var reply = client.Execute("MDTM {0:yyyyMMddHHmmss} {1}", time, path);
            if (!reply.Success)
                throw new FtpCommandException(reply);
        }
    }
}