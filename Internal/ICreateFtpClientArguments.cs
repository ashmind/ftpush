using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ftpush.Internal {
    public interface ICreateFtpClientArguments {
        bool FtpUseActive { get; }
        bool TcpKeepAlive { get; }
    }
}
