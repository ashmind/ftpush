## ftpush

ftpush is a small command line utility for uploading local files to FTP.  
At the moment it is tested with Azure FTP, but not much else.

You can get it from NuGet (https://www.nuget.org/packages/ftpush).

#### Example usage

```batch
SET FTP_PASS=mypassword
ftpush -source D:\build\site -target ftp://example.com/wwwroot -username uploader -passvar FTP_PASS
```

#### Features

* Directory synchronization
* Timestamp-based file comparison and skipping
* Parallel file uploads (connection per file, up to a limit)
* Exclusion patterns
* FTPS (very simple)

#### Limitations

* Timestamps are fine-tuned for Azure (uses MDTM to modify) and might not work elsewhere
* Minimal configuration, e.g. always deletes unmatched, etc
* Does not run on .NET Core
