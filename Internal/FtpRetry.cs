using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP;

namespace Ftpush.Internal {
    public static class FtpRetry {
        private static readonly HashSet<string> CanRetryReturnCode = new HashSet<string>{
            "530", // Not logged in -- should be fine since we check initial credentials before any retries
            "550"  // File not available, sometimes happens due to file lock
        };

        public static void Call(Action action, int? retryCount = null) {
            Call<object>(() => {
                action();
                return null;
            }, retryCount);
        }

        public static T Call<T>(Func<T> func, int? retryCount = null) {
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

        private static bool CanRetry(Exception exception) {
            var ftpException = exception as FtpCommandException;
            return ftpException != null && CanRetryReturnCode.Contains(ftpException.CompletionCode)
                || (exception is TimeoutException);
        }
    }
}
