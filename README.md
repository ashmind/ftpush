## ftpush

ftpush is a small command line utility for uploading local files to FTP.  
At the moment it is tested with Azure FTP, but not much else.

You can get it from NuGet (https://www.nuget.org/packages/ftpush).

#### Example usage

SET FTP_PASS=mypassword
ftpush -source D:\build\site -target ftp://example.com/wwwroot -username uploader -passvar FTP_PASS

#### Features

* Directory synchronization
* Timestamp-based file comparison and skipping
* Parallel file uploads (connection per file, up to a limit)
* Exclusion patterns

#### Limitations

* No FTPS: this is definitely a goal, but not there yet
* Timestamps are fine-tuned for Azure (uses MDTM to modify) and might not work elsewhere
* Minimal configuration, e.g. fixed number of parallel uploads, always deletes unmatched, etc
* Does not run on .NET Core
