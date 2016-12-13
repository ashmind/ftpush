using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FluentFTP;
using AshMind.Extensions;
using Ftpush.Internal;

namespace Ftpush {
    public class Process {
        private readonly FtpClient _client;
        private string _remoteWorkingDirectory;

        private Process(FtpClient client) {
            _client = client;
        }

        public static void SynchronizeDirectory(FtpClient client, DirectoryInfo localDirectory, string ftpPath) {
            new Process(client).SynchronizeDirectory(localDirectory, ftpPath);
        }

        private void SynchronizeDirectory(DirectoryInfo localDirectory, string ftpPath) {
            Log(0, ItemAction.Synchronize, localDirectory.Name);
            SynchronizeDirectory(localDirectory, ftpPath, 0);
        }

        private void SynchronizeDirectory(DirectoryInfo localDirectory, string ftpPath, int depth) {
            EnsureRemoteWorkingDirectory(ftpPath);
            var allRemote = _client.GetListing().ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);
            var allRemoteFound = new HashSet<FtpListItem>();

            foreach (var local in localDirectory.GetFileSystemInfos()) {
                EnsureRemoteWorkingDirectory(ftpPath);
                var remote = allRemote.GetValueOrDefault(local.Name);
                if (remote == null) {
                    Log(depth + 1, ItemAction.Add, local.Name);
                    PushAny(local, ftpPath, depth + 1);
                    continue;
                }

                allRemoteFound.Add(remote);
                var localAsDirectory = local as DirectoryInfo;
                if (localAsDirectory != null) {
                    if (remote.Type == FtpFileSystemObjectType.Directory) {
                        Log(depth + 1, ItemAction.Synchronize, localAsDirectory.Name);
                        SynchronizeDirectory(localAsDirectory, remote.FullName, depth + 1);
                        continue;
                    }

                    Log(depth + 1, ItemAction.Replace, localAsDirectory.Name, "remote is a file");
                    DeleteFtpFile(remote);
                    PushDirectory(localAsDirectory, ftpPath, depth + 1);
                    continue;
                }

                var localAsFile = (FileInfo)local;
                if (remote.Type == FtpFileSystemObjectType.Directory) {
                    Log(depth + 1, ItemAction.Replace, localAsFile.Name, "remote is a dir");
                    DeleteFtpDirectory(remote);
                    PushFile(localAsFile);
                    continue;
                }

                if (remote.Type == FtpFileSystemObjectType.Link) {
                    Log(depth+1, ItemAction.Replace, localAsFile.Name, "remote is a link");
                    PushFile(localAsFile);
                    continue;
                }

                if (remote.Modified.ToLocalTime().TruncateToMinutes() != localAsFile.LastWriteTime.TruncateToMinutes()) {
                    Log(depth+1, ItemAction.Replace, localAsFile.Name);
                    PushFile(localAsFile);
                    continue;
                }

                Log(depth + 1, ItemAction.Skip, local.Name);
            }

            EnsureRemoteWorkingDirectory(ftpPath);
            foreach (var missing in allRemote.Values.Except(allRemoteFound)) {
                Log(depth + 1, ItemAction.Delete, missing.Name);
                if (missing.Type == FtpFileSystemObjectType.Directory) {
                    DeleteFtpDirectory(missing);
                }
                else {
                    DeleteFtpFile(missing);
                }
            }
        }

        private void PushAny(FileSystemInfo local, string ftpParentPath, int depth) {
            var directory = local as DirectoryInfo;
            if (directory != null) {
                PushDirectory(directory, ftpParentPath, depth);
                return;
            }

            PushFile((FileInfo) local);
        }

        private void PushDirectory(DirectoryInfo localDirectory, string ftpParentPath, int depth) {
            var ftpPath = $"{ftpParentPath}/{localDirectory.Name}";
            _client.CreateDirectory(localDirectory.Name);
            foreach (var local in localDirectory.GetFileSystemInfos()) {
                EnsureRemoteWorkingDirectory(ftpPath);
                Log(depth + 1, ItemAction.Add, local.Name);
                PushAny(local, ftpParentPath, depth + 1);
            }
        }

        private void PushFile(FileInfo localFile) {
            using (var readStream = localFile.OpenRead())
            using (var writeStream = _client.OpenWrite(localFile.Name)) {
                readStream.CopyTo(writeStream, 256 * 1024);
            }
            RetryIfFileBusy(() => _client.SetModifiedTime(localFile.Name, localFile.LastWriteTimeUtc));
        }

        private void DeleteFtpDirectory(FtpListItem remote) {
            _client.DeleteDirectory(remote.Name);
        }

        private void DeleteFtpFile(FtpListItem remote) {
            _client.DeleteFile(remote.Name);
        }

        private void EnsureRemoteWorkingDirectory(string path) {
            if (_remoteWorkingDirectory == path)
                return;

            _client.SetWorkingDirectory(path);
            _remoteWorkingDirectory = path;
        }

        private void Log(int depth, ItemAction state, string itemName, string message = null) {
            var line = $"{new string(' ', depth*2)}{state.Character}{itemName}{(message != null ? ": " + message : "")}";
            FluentConsole.Color(state.Color).Line(line);
        }

        private void RetryIfFileBusy(Action action) {
            var maxRetryCount = 5;
            for (var i = 1; i <= maxRetryCount; i++) {
                try {
                    action();
                }
                catch (FtpCommandException ex) when (ex.CompletionCode == "550" && i < maxRetryCount) {
                    Thread.Sleep(1000);
                }
            }
        }

        private class ItemAction {
            public static ItemAction Add { get; } = new ItemAction('+', ConsoleColor.DarkGreen);
            public static ItemAction Skip { get; } = new ItemAction(null, ConsoleColor.DarkGray);
            public static ItemAction Delete { get; } = new ItemAction('-', ConsoleColor.Gray);
            public static ItemAction Synchronize { get; } = new ItemAction(null, ConsoleColor.Gray);
            public static ItemAction Replace { get; } = new ItemAction('>', ConsoleColor.DarkYellow);

            private ItemAction(char? character, ConsoleColor color) {
                Character = character;
                Color = color;
            }
            public char? Character { get; }
            public ConsoleColor Color { get; }
        }
    }
}
