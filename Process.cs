using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentFTP;
using AshMind.Extensions;
using Ftpush.Internal;
using JetBrains.Annotations;
using Minimatch;

namespace Ftpush {
    public class Process : IDisposable {
        private static readonly Task Done = Task.FromResult((object)null);

        private static readonly Options MinimatcherOptions = new Options{
            AllowWindowsPaths = true,
            Dot = true,
            IgnoreCase = true,
            NoComment = true,
            NoExt = true
        };

        private readonly FtpClient _mainClient;
        private readonly FtpClientPool _backgroundPool;
        private readonly IReadOnlyCollection<Rule> _excludes;
        private string _remoteWorkingDirectory;

        public Process(FtpClient mainClient, FtpClientPool backgroundPool, IReadOnlyCollection<string> excludes) {
            _mainClient = mainClient;
            _backgroundPool = backgroundPool;
            _excludes = excludes.Select(p => new Rule(new Minimatcher(p, MinimatcherOptions), p)).ToArray();
        }

        public void SynchronizeTopLevel(FileSystemInfo local, string ftpPath) {
            var ftpPathObject = new FtpPath(ftpPath.SubstringAfterLast("/"), ftpPath, "", 0);
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
                    PushFile(new LocalFile((FileInfo)local, local.Name, 0), "/", ftpPath, _mainClient);
                    return;
                }

                if (!_mainClient.HasFeature(FtpCapability.MLSD))
                    throw new NotSupportedException($"FTP server does not support MLST and no other way to know whether file {ftpPath} exists was implemented.");

                if (_mainClient.GetObjectInfo(ftpPath) != null)
                    DeleteFtpFile(ftpPathObject);
                Log(0, ItemAction.Add, localAsDirectory.Name);
                FtpRetry.ConnectedCall(_mainClient, "/", c => c.CreateDirectory(localAsDirectory.Name));
                PushDirectoryContents(new LocalDirectory(localAsDirectory, "", 0), ftpPathObject);
                return;
            }

            if (localAsDirectory == null) {
                var remoteChild = _mainClient.GetListing(".", FtpListOption.AllFiles).FirstOrDefault(l => l.Name.Equals(local.Name, StringComparison.OrdinalIgnoreCase));
                SynchronizeFileAsync(new LocalFile((FileInfo)local, local.Name, 0), ftpPathObject, remoteChild)?.GetAwaiter().GetResult();
            }

            SynchronizeDirectory(new LocalDirectory(localAsDirectory, "", 0), ftpPathObject);
        }

        private void SynchronizeDirectory(LocalDirectory localDirectory, FtpPath ftpPath) {
            Log(localDirectory.Depth, ItemAction.Synchronize, localDirectory.Name);

            EnsureRemoteWorkingDirectory(ftpPath.Absolute);
            var allRemote = FtpRetry.ConnectedCall(_mainClient, ftpPath.Absolute, c => c.GetListing(".", FtpListOption.AllFiles))
                .ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);
            var allRemoteFound = new HashSet<FtpListItem>();

            var pushTasks = new List<Task>();
            foreach (var local in localDirectory.EnumerateChildren()) {
                var remote = allRemote.GetValueOrDefault(local.Name);
                var exclude = MatchExcludes(local.RelativePath);
                if (exclude != null) {
                    Log(localDirectory.Depth + 1, ItemAction.Skip, local.Name, $"excluded ({exclude.Original})");
                    allRemoteFound.Add(remote);
                    continue;
                }

                EnsureRemoteWorkingDirectory(ftpPath.Absolute);

                if (remote != null)
                    allRemoteFound.Add(remote);

                var localAsDirectory = local as LocalDirectory;
                if (localAsDirectory != null) {
                    if (remote == null) {
                        Log(localDirectory.Depth + 1, ItemAction.Add, localAsDirectory.Name);
                        PushDirectory(localAsDirectory, ftpPath);
                        continue;
                    }

                    if (remote.Type == FtpFileSystemObjectType.Directory) {
                        SynchronizeDirectory(localAsDirectory, ftpPath.GetChildPath(remote.Name));
                        continue;
                    }

                    DeleteFtpFile(ftpPath.GetChildPath(remote.Name));
                    Log(localDirectory.Depth + 1, ItemAction.Add, localAsDirectory.Name);
                    PushDirectory(localAsDirectory, ftpPath);
                    continue;
                }

                var localAsFile = (LocalFile)local;
                var pushTask = SynchronizeFileAsync(localAsFile, ftpPath, remote);
                if (pushTask != null)
                    pushTasks.Add(pushTask);
            }
            if (pushTasks.Count > 0)
                Task.WaitAll(pushTasks.ToArray());

            EnsureRemoteWorkingDirectory(ftpPath.Absolute);
            foreach (var missing in allRemote.Values.Except(allRemoteFound)) {
                var missingPath = ftpPath.GetChildPath(missing.Name);
                var exclude = MatchExcludes(missingPath.Relative);
                if (exclude != null) {
                    Log(localDirectory.Depth + 1, ItemAction.Skip, missing.Name, $"excluded ({exclude.Original})");
                    continue;
                }
                DeleteFtpAny(missingPath, missing.Type);
            }
        }

        [CanBeNull]
        private Task SynchronizeFileAsync(LocalFile localFile, FtpPath parentPath, FtpListItem remote) {
            if (remote == null) {
                Log(localFile.Depth, ItemAction.Add, localFile.Name);
                return PushFileAsync(localFile);
            }

            if (remote.Type == FtpFileSystemObjectType.Directory) {
                DeleteFtpDirectory(parentPath.GetChildPath(remote.Name));
                Log(localFile.Depth, ItemAction.Replace, localFile.Name);
                return PushFileAsync(localFile);
            }

            if (remote.Type == FtpFileSystemObjectType.Link) {
                Log(localFile.Depth, ItemAction.Replace, localFile.Name, "remote is a link");
                return PushFileAsync(localFile);
            }

            if (remote.Modified.ToLocalTime().TruncateToMinutes() != localFile.LastWriteTime.TruncateToMinutes()) {
                Log(localFile.Depth, ItemAction.Replace, localFile.Name);
                return PushFileAsync(localFile);
            }

            Log(localFile.Depth, ItemAction.Skip, localFile.Name);
            return null; // I know it's bad style, but it optimizes the collection/wait for a common case when everything is up to date
        }

        private Task PushAnyAsync(LocalItem local, FtpPath ftpParentPath) {
            var directory = local as LocalDirectory;
            if (directory != null) {
                PushDirectory(directory, ftpParentPath);
                return Done;
            }

            return PushFileAsync((LocalFile)local);
        }

        private void PushDirectory(LocalDirectory localDirectory, FtpPath ftpParentPath) {
            var ftpPath = ftpParentPath.GetChildPath(localDirectory.Name);
            FtpRetry.ConnectedCall(_mainClient, ftpParentPath.Absolute, c => c.CreateDirectory(localDirectory.Name));
            PushDirectoryContents(localDirectory, ftpPath);
        }

        private void PushDirectoryContents(LocalDirectory localDirectory, FtpPath ftpPath) {
            var pushTasks = new List<Task>();
            foreach (var local in localDirectory.EnumerateChildren()) {
                var exclude = MatchExcludes(local.RelativePath);
                if (exclude != null) {
                    Log(local.Depth, ItemAction.Skip, local.Name, $"excluded ({exclude.Original})");
                    continue;
                }

                EnsureRemoteWorkingDirectory(ftpPath.Absolute);
                Log(local.Depth, ItemAction.Add, local.Name);
                pushTasks.Add(PushAnyAsync(local, ftpPath));
            }
            Task.WaitAll(pushTasks.ToArray());
        }

        private async Task PushFileAsync(LocalFile localFile) {
            var remoteWorkingDirectory = _remoteWorkingDirectory;
            using (var pushLease = _backgroundPool.LeaseClient()) {
                // ReSharper disable AccessToDisposedClosure
                await Task.Run(() => {
                    try {
                        FtpRetry.ConnectedCall(pushLease.Client, "/", c => c.SetWorkingDirectory(remoteWorkingDirectory));
                        PushFile(localFile, remoteWorkingDirectory, localFile.Name, pushLease.Client);
                    }
                    catch (Exception ex) {
                        throw new Exception($"Failed to push file '{localFile.RelativePath}': {ex.Message}", ex);
                    }
                }).ConfigureAwait(false);
                // ReSharper restore AccessToDisposedClosure
            }
        }

        private void PushFile(LocalFile localFile, string ftpParentPath, string ftpPath, FtpClient client) {
            // https://github.com/hgupta9/FluentFTP/issues/46
            using (var readStream = localFile.OpenRead())
            using (var writeStream = FtpRetry.ConnectedCall(client, ftpParentPath, c => c.OpenWrite(ftpPath))) {
                readStream.CopyTo(writeStream, 256 * 1024);
            }
            FtpRetry.ConnectedCall(client, ftpParentPath, c => c.SetModifiedTime(ftpPath, localFile.LastWriteTimeUtc));
        }

        private bool DeleteFtpAny(FtpPath path, FtpFileSystemObjectType type) {
            if (type == FtpFileSystemObjectType.Directory) {
                return DeleteFtpDirectory(path);
            }
            else {
                DeleteFtpFile(path);
                return true;
            }
        }

        private bool DeleteFtpDirectory(FtpPath path) {
            Log(path.Depth, ItemAction.Delete, path.Name);
            var parentWorkingDirectory = _remoteWorkingDirectory;
            var itemsRemain = false;
            foreach (var child in _mainClient.GetListing(path.Name, FtpListOption.AllFiles)) {
                var childPath = path.GetChildPath(child.Name);
                var exclude = MatchExcludes(childPath.Relative);
                if (exclude != null) {
                    Log(path.Depth + 1, ItemAction.Skip, child.Name, $"excluded ({exclude.Original})");
                    itemsRemain = true;
                    continue;
                }

                EnsureRemoteWorkingDirectory(path.Absolute);
                if (!DeleteFtpAny(childPath, child.Type))
                    itemsRemain = true;
            }
            EnsureRemoteWorkingDirectory(parentWorkingDirectory);
            if (itemsRemain) {
                Log(path.Depth + 1, ItemAction.Skip, $"# dir '{path.Name}' not deleted (items remain)");
                return false;
            }
            FtpRetry.ConnectedCall(_mainClient, parentWorkingDirectory, c => c.DeleteDirectory(path.Name));
            return true;
        }
        
        private void DeleteFtpFile(FtpPath path) {
            Log(path.Depth, ItemAction.Delete, path.Name);
            FtpRetry.ConnectedCall(_mainClient, _remoteWorkingDirectory, c => c.DeleteFile(path.Name));
        }

        private void EnsureRemoteWorkingDirectory(string absolutePath, int? retryCount = null) {
            if (_remoteWorkingDirectory == absolutePath)
                return;

            FtpRetry.ConnectedCall(_mainClient, "/", c => c.SetWorkingDirectory(absolutePath), retryCount);
            _remoteWorkingDirectory = absolutePath;
        }

        private Rule MatchExcludes(string relativePath) {
            foreach (var exclude in _excludes) {
                if (exclude.Minimatcher.IsMatch(relativePath))
                    return exclude;
            }
            return null;
        }

        private void Log(int depth, ItemAction state, string itemName, string message = null) {
            var line = $"{new string(' ', depth*2)}{state.DisplayPrefix}{itemName}{(message != null ? ": " + message : "")}";
            FluentConsole.Color(state.Color).Line(line);
        }

        public void Dispose() {
            _mainClient.Dispose();
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
            public Minimatcher Minimatcher { get; }
            public string Original { get; }

            public Rule(Minimatcher minimatcher, string original) {
                Minimatcher = minimatcher;
                Original = original;
            }
        }
    }
}
