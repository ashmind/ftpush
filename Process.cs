using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP;
using AshMind.Extensions;
using Ftpush.Internal;

namespace Ftpush {
    public class Process : IDisposable {
        private static readonly Task Done = Task.FromResult((object)null);
        private static readonly HashSet<string> CanRetryReturnCode = new HashSet<string>{
            "530", // Not logged in -- should be fine since we check initial credentials before any retries
            "550"  // File not available, sometimes happens due to file lock
        };

        private readonly FtpClient _mainClient;
        private readonly FtpClientLease _mainClientLease;
        private readonly FtpClientPool _clientPool;
        private readonly IReadOnlyCollection<Rule> _excludes;
        private string _remoteWorkingDirectory;

        private Process(FtpClientPool clientPool, IReadOnlyCollection<string> excludes) {
            _clientPool = clientPool;
            _mainClientLease = clientPool.LeaseClient();
            try {
                _mainClient = _mainClientLease.Client;
            }
            catch (Exception) {
                _mainClientLease.Dispose();
                throw;
            }
            _excludes = excludes.Select(p => new Rule(GlobHelper.ConvertGlobsToRegex(p), p)).ToArray();
        }

        public static void SynchronizeDirectory(FtpClientPool clientPool, DirectoryInfo localDirectory, string ftpPath, IReadOnlyCollection<string> excludes) {
            using (var process = new Process(clientPool, excludes)) {
                process.SynchronizeDirectory(localDirectory, ftpPath);
            }
        }

        private void SynchronizeDirectory(DirectoryInfo localDirectory, string ftpPath) {
            Log(0, ItemAction.Synchronize, localDirectory.Name);
            SynchronizeDirectory(localDirectory, ftpPath, 0);
        }

        private void SynchronizeDirectory(DirectoryInfo localDirectory, string ftpPath, int depth) {
            EnsureRemoteWorkingDirectory(ftpPath);
            var allRemote = _mainClient.GetListing().ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);
            var allRemoteFound = new HashSet<FtpListItem>();

            var pushTasks = new List<Task>();
            foreach (var local in localDirectory.EnumerateFileSystemInfos()) {
                var remote = allRemote.GetValueOrDefault(local.Name);
                var exclude = MatchExcludes(local.FullName);
                if (exclude != null) {
                    Log(depth + 1, ItemAction.Skip, local.Name, $"excluded ({exclude.Original})");
                    allRemoteFound.Add(remote);
                    continue;
                }

                EnsureRemoteWorkingDirectory(ftpPath);
                if (remote == null) {
                    Log(depth + 1, ItemAction.Add, local.Name);
                    pushTasks.Add(PushAnyAsync(local, ftpPath, depth + 1));
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

                    DeleteFtpFile(remote, depth + 1);
                    Log(depth + 1, ItemAction.Add, localAsDirectory.Name);
                    PushDirectory(localAsDirectory, ftpPath, depth + 1);
                    continue;
                }

                var localAsFile = (FileInfo)local;
                if (remote.Type == FtpFileSystemObjectType.Directory) {
                    DeleteFtpDirectory(remote, depth + 1);
                    Log(depth + 1, ItemAction.Replace, localAsFile.Name);
                    pushTasks.Add(PushFileAsync(localAsFile));
                    continue;
                }

                if (remote.Type == FtpFileSystemObjectType.Link) {
                    Log(depth+1, ItemAction.Replace, localAsFile.Name, "remote is a link");
                    pushTasks.Add(PushFileAsync(localAsFile));
                    continue;
                }

                if (remote.Modified.ToLocalTime().TruncateToMinutes() != localAsFile.LastWriteTime.TruncateToMinutes()) {
                    Log(depth+1, ItemAction.Replace, localAsFile.Name);
                    pushTasks.Add(PushFileAsync(localAsFile));
                    continue;
                }

                Log(depth + 1, ItemAction.Skip, local.Name);
            }
            Task.WaitAll(pushTasks.ToArray());

            EnsureRemoteWorkingDirectory(ftpPath);
            foreach (var missing in allRemote.Values.Except(allRemoteFound)) {
                var exclude = MatchExcludes(missing.FullName);
                if (exclude != null) {
                    Log(depth + 1, ItemAction.Skip, missing.Name, $"excluded ({exclude.Original})");
                    continue;
                }
                DeleteFtpAny(missing, depth + 1);
            }
        }

        private Task PushAnyAsync(FileSystemInfo local, string ftpParentPath, int depth) {
            var directory = local as DirectoryInfo;
            if (directory != null) {
                PushDirectory(directory, ftpParentPath, depth);
                return Done;
            }

            return PushFileAsync((FileInfo) local);
        }

        private void PushDirectory(DirectoryInfo localDirectory, string ftpParentPath, int depth) {
            var ftpPath = $"{ftpParentPath}/{localDirectory.Name}";
            _mainClient.CreateDirectory(localDirectory.Name);

            var pushTasks = new List<Task>();
            foreach (var local in localDirectory.EnumerateFileSystemInfos()) {
                var exclude = MatchExcludes(local.FullName);
                if (exclude != null) {
                    Log(depth + 1, ItemAction.Skip, local.Name, $"excluded ({exclude.Original})");
                    continue;
                }

                EnsureRemoteWorkingDirectory(ftpPath);
                Log(depth + 1, ItemAction.Add, local.Name);
                pushTasks.Add(PushAnyAsync(local, ftpParentPath, depth + 1));
            }
            Task.WaitAll(pushTasks.ToArray());
        }

        private async Task PushFileAsync(FileInfo localFile) {
            var remoteWorkingDirectory = _remoteWorkingDirectory;
            using (var pushLease = _clientPool.LeaseClient()) {
                // ReSharper disable AccessToDisposedClosure
                await Task.Run(() => {
                    pushLease.Client.SetWorkingDirectory(remoteWorkingDirectory);

                    using (var readStream = localFile.OpenRead())
                    using (var writeStream = Retry(() => pushLease.Client.OpenWrite(localFile.Name))) {
                        readStream.CopyTo(writeStream, 256*1024);
                    }
                    Retry(() => pushLease.Client.SetModifiedTime(localFile.Name, localFile.LastWriteTimeUtc));
                }).ConfigureAwait(false);
                // ReSharper restore AccessToDisposedClosure
            }
        }
        private bool DeleteFtpAny(FtpListItem remote, int depth) {
            if (remote.Type == FtpFileSystemObjectType.Directory) {
                return DeleteFtpDirectory(remote, depth);
            }
            else {
                DeleteFtpFile(remote, depth);
                return true;
            }
        }

        private bool DeleteFtpDirectory(FtpListItem remote, int depth) {
            Log(depth, ItemAction.Delete, remote.Name);
            var parentWorkingDirectory = _remoteWorkingDirectory;
            var itemsRemain = false;
            foreach (var child in _mainClient.GetListing(remote.Name)) {
                var exclude = MatchExcludes(child.FullName);
                if (exclude != null) {
                    Log(depth + 1, ItemAction.Skip, child.Name, $"excluded ({exclude.Original})");
                    itemsRemain = true;
                    continue;
                }

                EnsureRemoteWorkingDirectory(remote.FullName);
                if (!DeleteFtpAny(child, depth + 1))
                    itemsRemain = true;
            }
            if (itemsRemain) {
                Log(depth + 1, ItemAction.Skip, $"# dir '{remote.Name}' not deleted (items remain)");
                return false;
            }

            EnsureRemoteWorkingDirectory(parentWorkingDirectory);
            Retry(() => _mainClient.DeleteDirectory(remote.Name));
            return true;
        }

        private void DeleteFtpFile(FtpListItem remote, int depth) {
            Log(depth, ItemAction.Delete, remote.Name);
            Retry(() => _mainClient.DeleteFile(remote.Name));
        }

        private void EnsureRemoteWorkingDirectory(string path) {
            if (_remoteWorkingDirectory == path)
                return;

            _mainClient.SetWorkingDirectory(path);
            _remoteWorkingDirectory = path;
        }

        private Rule MatchExcludes(string path) {
            foreach (var exclude in _excludes) {
                if (exclude.Regex.IsMatch(path))
                    return exclude;
            }
            return null;
        }

        private void Log(int depth, ItemAction state, string itemName, string message = null) {
            var line = $"{new string(' ', depth*2)}{state.DisplayPrefix}{itemName}{(message != null ? ": " + message : "")}";
            FluentConsole.Color(state.Color).Line(line);
        }

        private T Retry<T>(Func<T> func) {
            var maxRetryCount = 5;
            var retry = 1;
            while (true) {
                try {
                    return func();
                }
                catch (FtpCommandException ex) when (CanRetryReturnCode.Contains(ex.CompletionCode) && retry < maxRetryCount) {
                    Thread.Sleep(1000);
                }
                retry += 1;
            }
        }

        private void Retry(Action action) {
            Retry<object>(() => {
                action();
                return null;
            });
        }

        public void Dispose() {
            _mainClientLease.Dispose();
        }

        private class ItemAction {
            public static ItemAction Add { get; } = new ItemAction("+ ", ConsoleColor.DarkGreen);
            public static ItemAction Skip { get; } = new ItemAction(null, ConsoleColor.DarkGray);
            public static ItemAction Delete { get; } = new ItemAction("- ", ConsoleColor.DarkRed);
            public static ItemAction Synchronize { get; } = new ItemAction(null, ConsoleColor.Gray);
            public static ItemAction Replace { get; } = new ItemAction("* ", ConsoleColor.DarkYellow);

            private ItemAction(string displayPrefix, ConsoleColor color) {
                DisplayPrefix = displayPrefix;
                Color = color;
            }
            public string DisplayPrefix { get; }
            public ConsoleColor Color { get; }
        }

        private class Rule {
            public Regex Regex { get; }
            public string Original { get; }

            public Rule(Regex regex, string original) {
                Regex = regex;
                Original = original;
            }
        }
    }
}
