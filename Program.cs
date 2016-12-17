﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Authentication;
using AshMind.Extensions;
using CommandLine.Text;
using FluentFTP;
using Ftpush.Internal;

namespace Ftpush {
    public static class Program {
        public static int Main(string[] args) {
            try {

                var arguments = new Arguments();
                if (!CommandLine.Parser.Default.ParseArgumentsStrict(args, arguments))
                    return -1;

                Console.WriteLine(HeadingInfo.Default);
                Console.WriteLine(CopyrightInfo.Default);
                Console.WriteLine();

                Main(arguments);
                return 0;
            }
            catch (ArgumentValidationException ex) {
                FluentConsole.Red.Text(ex.Message);
                return -1;
            }
            catch (Exception ex) {
                var ftpException = GetFtpCommandException(ex);
                if (ftpException != null)
                    FluentConsole.Red.Line($"{ftpException.CompletionCode} {ftpException.Message}");

                FluentConsole.Red.Text(ex);
                return ex.HResult;
            }
        }

        private static FtpCommandException GetFtpCommandException(Exception ex) {
            var ftpException = ex as FtpCommandException;
            if (ftpException != null)
                return ftpException;

            var aggregateException = ex as AggregateException;
            if (aggregateException != null)
                return GetFtpCommandException(aggregateException.InnerException);

            return null;
        }

        private static void Main(Arguments args) {
            if (args.Verbose)
                FtpTrace.AddListener(new ConsoleTraceListener());

            var ftpUrl = new Uri(args.FtpUrl);
            var password = Environment.GetEnvironmentVariable(args.FtpPasswordVariableName, EnvironmentVariableTarget.Process);
            if (string.IsNullOrEmpty(password))
                throw new ArgumentValidationException($"Password env variable '{args.FtpPasswordVariableName}' is not set for the current process (user/machine vars are ignored).");

            FluentConsole.White.Line(ftpUrl);
            var started = DateTime.Now;

            var basePath = ftpUrl.LocalPath;
            var credentials = new NetworkCredential(args.FtpUserName, password);
            var sourceInfo = Directory.Exists(args.SourcePath) ? (FileSystemInfo)new DirectoryInfo(args.SourcePath) : new FileInfo(args.SourcePath);

            using (var pool = new FtpClientPool(() => CreateFtpClient(ftpUrl, credentials), 6))
            using (var process = new Process(pool, args.Excludes.AsReadOnlyList())) {
                process.SynchronizeTopLevel(sourceInfo, basePath);
            }

            FluentConsole.NewLine().Green.Line(@"Finished in {0:dd\.hh\:mm\:ss}.", DateTime.Now - started);
        }

        private static FtpClient CreateFtpClient(Uri url, NetworkCredential credentials) {
            var encryptionMode = FtpEncryptionMode.None;
            if (url.Scheme.Equals("ftps", StringComparison.OrdinalIgnoreCase)) {
                encryptionMode = FtpEncryptionMode.Explicit;
            }
            else if (!url.Scheme.Equals("ftp", StringComparison.OrdinalIgnoreCase)) {
                throw new ArgumentValidationException($"URL scheme '{url.Scheme}' is not supported.");
            }

            if (encryptionMode == FtpEncryptionMode.Explicit)
                throw new NotSupportedException("FTPS hangs at the moment, needs more research.");

            FtpClient client = null;
            try {
                client = new FtpClient{
                    DataConnectionType = FtpDataConnectionType.AutoActive,
                    Host = url.Host,
                    Port = !url.IsDefaultPort ? url.Port : 0,
                    EncryptionMode = encryptionMode,
                    Credentials = credentials,
                    SslProtocols = SslProtocols.Default | SslProtocols.Tls11 | SslProtocols.Tls12
                };
                client.Connect();
                return client;
            }
            catch (Exception) {
                client?.Dispose();
                throw;
            }
        }

        [Serializable]
        private class ArgumentValidationException : Exception {
            // ReSharper disable UnusedMember.Local
            public ArgumentValidationException() { }
            public ArgumentValidationException(string message) : base(message) { }
            public ArgumentValidationException(string message, Exception inner) : base(message, inner) { }
            protected ArgumentValidationException(SerializationInfo info, StreamingContext context) : base(info, context) { }
            // ReSharper restore UnusedMember.Local
        }
    }
}
