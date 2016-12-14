using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using FluentFTP;
using Ftpush.Internal;

namespace Ftpush {
    public static class Program {
        public static int Main(string[] args) {
            try {
                var arguments = new Arguments();
                if (!CommandLine.Parser.Default.ParseArgumentsStrict(args, arguments))
                    return -1;

                Main(arguments);
                return 0;
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
                throw new Exception($"Password env variable '{args.FtpPasswordVariableName}' is not set for the current process (user/machine var might exist, but is not looked at).");

            FluentConsole.White.Line("Connecting to {0}.", ftpUrl);
            var started = DateTime.Now;

            var basePath = ftpUrl.LocalPath;
            var credentials = new NetworkCredential(args.FtpUserName, password);

            using (var pool = new FtpClientPool(() => CreateFtpClient(ftpUrl.Host, credentials), 5)) {
                Process.SynchronizeDirectory(pool, new DirectoryInfo(args.SourcePath), basePath);
            }

            FluentConsole.NewLine().Green.Line(@"Finished in {0:dd\.hh\:mm\:ss}.", DateTime.Now - started);
        }

        private static FtpClient CreateFtpClient(string host, NetworkCredential credentials) {
            FtpClient client = null;
            try {
                client = new FtpClient{
                    DataConnectionType = FtpDataConnectionType.AutoActive,
                    Host = host,
                    Credentials = credentials
                };
                client.Connect();
                return client;
            }
            catch (Exception) {
                client?.Dispose();
                throw;
            }
        }
    }
}
