using Microsoft.Extensions.Options;
using Renci.SshNet;

namespace FtpMirrorService;

public sealed class FtpMirror(
    IOptions<FtpMirrorOptions> options,
    ILogger<FtpMirror> logger)
{
    private readonly FtpMirrorOptions _options = options.Value;

    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        var sourceDirectory = Path.GetFullPath(_options.SourceDirectory);
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"The configured source directory does not exist: {sourceDirectory}");
        }

        logger.LogInformation("Starting SFTP mirror from local directory {Source} to {Host}:{RootPath}.",
            sourceDirectory, _options.Destination.Host, _options.Destination.RootPath);

        var transfers = new List<TransferJob>();
        using (var discoveryClient = CreateSftpClient())
        {
            discoveryClient.Connect();
            try
            {
                if (!discoveryClient.Exists(RemoteRootPath))
                {
                    throw new DirectoryNotFoundException($"The configured SFTP root does not exist: {RemoteRootPath}");
                }

                DiscoverTransfers(discoveryClient, sourceDirectory, string.Empty, transfers, cancellationToken);
            }
            finally
            {
                if (discoveryClient.IsConnected)
                {
                    discoveryClient.Disconnect();
                }
            }
        }

        logger.LogInformation("Found {Count} file(s) to upload with up to {Concurrency} concurrent transfer(s).",
            transfers.Count, _options.MaxConcurrentUploads);

        await Parallel.ForEachAsync(
            transfers,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.MaxConcurrentUploads,
                CancellationToken = cancellationToken
            },
            (transfer, token) =>
            {
                UploadFile(transfer, token);
                return ValueTask.CompletedTask;
            });

        logger.LogInformation("SFTP mirror completed.");
    }

    private void DiscoverTransfers(
        SftpClient client,
        string localDirectory,
        string relativePath,
        ICollection<TransferJob> transfers,
        CancellationToken cancellationToken)
    {
        var oldestEligibleFileUtc = DateTime.UtcNow - _options.RecentFileWindow;

        foreach (var itemPath in Directory.EnumerateFileSystemEntries(localDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(itemPath);
            var childPath = CombinePath(relativePath, name);

            if (Directory.Exists(itemPath))
            {
                EnsureDirectory(client, childPath);
                DiscoverTransfers(client, itemPath, childPath, transfers, cancellationToken);
            }
            else if (File.Exists(itemPath))
            {
                var sourceFile = new FileInfo(itemPath);
                if (sourceFile.LastWriteTimeUtc < oldestEligibleFileUtc)
                {
                    logger.LogDebug("Skipped old file {Path}.", childPath);
                }
                else if (!DestinationHasCurrentFile(client, childPath, sourceFile))
                {
                    transfers.Add(new TransferJob(sourceFile, childPath));
                }
            }
        }
    }

    private bool DestinationHasCurrentFile(SftpClient client, string relativePath, FileInfo sourceFile)
    {
        var remotePath = BuildRemotePath(relativePath);
        if (!client.Exists(remotePath))
        {
            return false;
        }

        var attributes = client.GetAttributes(remotePath);
        return attributes.Size == sourceFile.Length &&
               attributes.LastWriteTimeUtc >= sourceFile.LastWriteTimeUtc;
    }

    private void UploadFile(TransferJob transfer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var client = CreateSftpClient();
        client.Connect();

        try
        {
            using var sourceStream = new FileStream(
                transfer.SourceFile.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: false);

            client.UploadFile(sourceStream, BuildRemotePath(transfer.RelativePath), canOverride: true);
            logger.LogInformation("Copied {Path}.", transfer.RelativePath);
        }
        finally
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }
        }
    }

    private void EnsureDirectory(SftpClient client, string relativePath)
    {
        var remotePath = BuildRemotePath(relativePath);
        if (!client.Exists(remotePath))
        {
            client.CreateDirectory(remotePath);
        }
    }

    private SftpClient CreateSftpClient() => new(
        _options.Destination.Host,
        _options.Destination.Port,
        _options.UserName,
        _options.Password);

    private string RemoteRootPath => _options.Destination.RootPath.TrimEnd('/');

    private string BuildRemotePath(string relativePath) =>
        string.IsNullOrEmpty(relativePath)
            ? RemoteRootPath
            : RemoteRootPath + "/" + relativePath.Replace('\\', '/');

    private static string CombinePath(string parent, string name) =>
        string.IsNullOrEmpty(parent) ? name : parent + "/" + name;

    private sealed record TransferJob(FileInfo SourceFile, string RelativePath);
}
