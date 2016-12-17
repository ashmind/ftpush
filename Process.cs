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
using JetBrains.Annotations;

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

        public Process(FtpClientPool clientPool, IReadOnlyCollection<string> excludes) {
            _clientPool = clientPool;
            try {
                _mainClientLease = clientPool.LeaseClient();
                _mainClient = _mainClientLease.Client;
                _excludes = excludes.Select(p => new Rule(GlobHelper.ConvertGlobsToRegex(p), p)).ToArray();
            }
            catch (Exception) {
                _mainClientLease?.Dispose();
                throw;
            }
        }

        public void SynchronizeTopLevel(FileSystemInfo local, string ftpPath) {
            var remoteIsDirectory = false;
            try {
                EnsureRemoteWorkingDirectory(ftpPath, retryCount: 0);
                remoteIsDirectory = true;
            }
            catch (FtpCommandException ex) when (ex.CompletionCode == "550") {
            }

            var localAsDirectory = local as DirectoryInfo;
            if (!remoteIsDirectory) {
                if (localAsDirectory == null) {
                    Log(0, ItemAction.Replace, local.Name);
                    PushFile((FileInfo) local, ftpPath, _mainClient);
                    return;
                }

                if (!_mainClient.HasFeature(FtpCapability.MLSD))
                    throw new NotSupportedException($"FTP server does not support MLST and no other way to know whether file {ftpPath} exists was implemented.");

                if (_mainClient.GetObjectInfo(ftpPath) != null)
                    DeleteFtpFile(ftpPath, 0);
                Log(0, ItemAction.Add, localAsDirectory.Name);
                Retry(() => _mainClient.CreateDirectory(localAsDirectory.Name));
                PushDirectoryContents(localAsDirectory, ftpPath, 0);
                return;
            }

            if (localAsDirectory == null) {
                var remoteChild = _mainClient.GetListing().FirstOrDefault(l => l.Name.Equals(local.Name, StringComparison.OrdinalIgnoreCase));
                SynchronizeFileAsync((FileInfo) local, remoteChild, 0)?.GetAwaiter().GetResult();
            }

            SynchronizeDirectory(localAsDirectory, ftpPath, 0);
        }

        private void SynchronizeDirectory(DirectoryInfo localDirectory, string ftpPath, int depth) {
            Log(depth, ItemAction.Synchronize, localDirectory.Name);

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

                if (remote != null)
                    allRemoteFound.Add(remote);

                var localAsDirectory = local as DirectoryInfo;
                if (localAsDirectory != null) {
                    if (remote == null) {
                        Log(depth + 1, ItemAction.Add, local.Name);
                        PushDirectory(localAsDirectory, ftpPath, depth + 1);
                        continue;
                    }

                    if (remote.Type == FtpFileSystemObjectType.Directory) {
                        SynchronizeDirectory(localAsDirectory, remote.FullName, depth + 1);
                        continue;
                    }

                    DeleteFtpFile(remote, depth + 1);
                    Log(depth + 1, ItemAction.Add, localAsDirectory.Name);
                    PushDirectory(localAsDirectory, ftpPath, depth + 1);
                    continue;
                }

                var localAsFile = (FileInfo)local;
                var pushTask = SynchronizeFileAsync(localAsFile, remote, depth);
                if (pushTask != null)
                    pushTasks.Add(pushTask);
            }
            if (pushTasks.Count > 0)
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

        [CanBeNull]
        private Task SynchronizeFileAsync(FileInfo localFile, FtpListItem remote, int depth) {
            if (remote == null) {
                Log(depth + 1, ItemAction.Add, localFile.Name);
                return PushFileAsync(localFile);
            }

            if (remote.Type == FtpFileSystemObjectType.Directory) {
                DeleteFtpDirectory(remote, depth + 1);
                Log(depth + 1, ItemAction.Replace, localFile.Name);
                return PushFileAsync(localFile);
            }

            if (remote.Type == FtpFileSystemObjectType.Link) {
                Log(depth + 1, ItemAction.Replace, localFile.Name, "remote is a link");
                return PushFileAsync(localFile);
            }

            if (remote.Modified.ToLocalTime().TruncateToMinutes() != localFile.LastWriteTime.TruncateToMinutes()) {
                Log(depth + 1, ItemAction.Replace, localFile.Name);
                return PushFileAsync(localFile);
            }

            Log(depth + 1, ItemAction.Skip, localFile.Name);
            return null; // I know it's bad style, but it optimizes the collection/wait for a common case when everything is up to date
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
            Retry(() => _mainClient.CreateDirectory(localDirectory.Name));
            PushDirectoryContents(localDirectory, ftpPath, depth);
        }

        private void PushDirectoryContents(DirectoryInfo localDirectory, string ftpPath, int depth) {
            var pushTasks = new List<Task>();
            foreach (var local in localDirectory.EnumerateFileSystemInfos()) {
                var exclude = MatchExcludes(local.FullName);
                if (exclude != null) {
                    Log(depth + 1, ItemAction.Skip, local.Name, $"excluded ({exclude.Original})");
                    continue;
                }

                EnsureRemoteWorkingDirectory(ftpPath);
                Log(depth + 1, ItemAction.Add, local.Name);
                pushTasks.Add(PushAnyAsync(local, ftpPath, depth + 1));
            }
            Task.WaitAll(pushTasks.ToArray());
        }

        private async Task PushFileAsync(FileInfo localFile) {
            var remoteWorkingDirectory = _remoteWorkingDirectory;
            using (var pushLease = _clientPool.LeaseClient()) {
                // ReSharper disable AccessToDisposedClosure
                await Task.Run(() => {
                    Retry(() => pushLease.Client.SetWorkingDirectory(remoteWorkingDirectory));
                    PushFile(localFile, localFile.Name, pushLease.Client);
                }).ConfigureAwait(false);
                // ReSharper restore AccessToDisposedClosure
            }
        }

        private void PushFile(FileInfo localFile, string ftpPath, FtpClient client) {
            using (var readStream = localFile.OpenRead())
            using (var writeStream = Retry(() => client.OpenWrite(ftpPath))) {
                readStream.CopyTo(writeStream, 256*1024);
            }
            Retry(() => client.SetModifiedTime(ftpPath, localFile.LastWriteTimeUtc));
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
            DeleteFtpFile(remote.Name, depth);
        }

        private void DeleteFtpFile(string ftpPath, int depth) {
            Log(depth, ItemAction.Delete, ftpPath);
            Retry(() => _mainClient.DeleteFile(ftpPath));
        }

        private void EnsureRemoteWorkingDirectory(string path, int? retryCount = null) {
            if (_remoteWorkingDirectory == path)
                return;

            Retry(() => _mainClient.SetWorkingDirectory(path), retryCount);
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

        private void Retry(Action action, int? retryCount = null) {
            Retry<object>(() => {
                action();
                return null;
            });
        }

        private T Retry<T>(Func<T> func, int? retryCount = null) {
            retryCount = retryCount ?? 10;
            var currentRetryCount = 0;
            while (true) {
                try {
                    return func();
                }
                catch (Exception ex) when (CanRetry(ex) && currentRetryCount < retryCount) {
                    Thread.Sleep(1000);
                }
                currentRetryCount += 1;
            }
        }

        private bool CanRetry(Exception exception) {
            var ftpException = exception as FtpCommandException;
            return ftpException != null && CanRetryReturnCode.Contains(ftpException.CompletionCode)
                || (exception is TimeoutException);
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
