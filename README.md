# FTP Mirror Service

This .NET 10 Windows Service checks the configured local source directory every 2 minutes and copies only files modified within the preceding 60 minutes that do not already exist on the SFTP destination. It never deletes or overwrites a destination file, and creates remote folders only when needed for an eligible upload. Up to 5 files upload concurrently.

## Configuration

The committed `appsettings.json` contains the configured SFTP connection information. Treat access to this repository as access to those credentials. You can override these settings through environment variables:

```text
FtpMirror__UserName=<FTP username>
FtpMirror__Password=<FTP password>
```

Optional overrides:

```text
FtpMirror__SourceDirectory=Z:\\
FtpMirror__Destination__Host=www.camera.mgmsolutions.net
FtpMirror__Destination__Port=22
FtpMirror__Destination__RootPath=/home/mgmcamera_ftp/camera.mgmsolutions.net/
FtpMirror__Interval=00:02:00
FtpMirror__RecentFileWindow=01:00:00
FtpMirror__MaxConcurrentUploads=5
```

The destination uses SFTP over SSH on port 22.

## Publish and install

Publish a self-contained 64-bit build:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o C:\Services\FtpMirrorService
```

Register the published executable from an elevated PowerShell prompt:

```powershell
sc.exe create "FTP Mirror Service" binPath= "C:\Services\FtpMirrorService\FtpMirrorService.exe" start= auto
sc.exe start "FTP Mirror Service"
```

Set the two secret environment variables for the service account, then restart the service. The service machine must have access to the configured `Z:\\` directory. A drive mapped only in an interactive user's session is not normally available to a Windows Service; use a service account that has the mapping, or configure the share's UNC path instead.
