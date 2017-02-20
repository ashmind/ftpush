using System;
using System.Collections.Generic;
using System.Threading;
using FluentFTP;

namespace Ftpush.Internal {
    public static class FtpRetry {
        private static class ReturnCodes {
            public const string NotLoggedIn = "530";
            public const string FileNotAvailable = "550"; // Sometimes happens due to file lock
        }
        private static readonly HashSet<string> CanRetryReturnCode = new HashSet<string>{
            ReturnCodes.NotLoggedIn,
            ReturnCodes.FileNotAvailable
        };

        public static void ConnectedCall(FtpClient client, string workingDirectoryPath, Action<FtpClient> action, int? retryCount = null) {
            ConnectedCall<object>(client, workingDirectoryPath, c => {
                action(c);
                return null;
            }, retryCount);
        }

        public static T ConnectedCall<T>(FtpClient client, string workingDirectoryPath, Func<FtpClient, T> func, int? retryCount = null) {
            retryCount = retryCount ?? 30;
            var currentRetryCount = 0;
            var hadLoginException = false;
            while (true) {
                try {
                    if (!client.IsConnected || hadLoginException) {
                        client.Connect();
                        if (workingDirectoryPath != "/")
                            client.SetWorkingDirectory(workingDirectoryPath);
                    }
                    return func(client);
                }
                catch (Exception ex) when (CanRetry(ex) && currentRetryCount < retryCount) {
                    hadLoginException = ((ex as FtpCommandException)?.CompletionCode == ReturnCodes.NotLoggedIn);
                    Thread.Sleep(currentRetryCount > 0 ? 1000 : 500);
                }
                currentRetryCount += 1;
            }
        }

        private static bool CanRetry(Exception exception) {
            var ftpException = exception as FtpCommandException;
            return ftpException != null && CanRetryReturnCode.Contains(ftpException.CompletionCode)
                || (exception is TimeoutException);
        }
    }
}
