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

                var rootDirectoryIndex = ListRemoteDirectory(discoveryClient, RemoteRootPath);
                var knownRemoteDirectories = new HashSet<string>(StringComparer.Ordinal) { string.Empty };
                DiscoverTransfers(
                    discoveryClient,
                    sourceDirectory,
                    string.Empty,
                    rootDirectoryIndex,
                    knownRemoteDirectories,
                    transfers,
                    cancellationToken);
                EnsureRemoteDirectories(discoveryClient, transfers, knownRemoteDirectories);
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
        RemoteDirectoryIndex remoteDirectoryIndex,
        ISet<string> knownRemoteDirectories,
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
                var childRemoteDirectoryIndex = GetRemoteDirectoryIndex(
                    client,
                    childPath,
                    name,
                    remoteDirectoryIndex,
                    knownRemoteDirectories);
                DiscoverTransfers(
                    client,
                    itemPath,
                    childPath,
                    childRemoteDirectoryIndex,
                    knownRemoteDirectories,
                    transfers,
                    cancellationToken);
            }
            else if (File.Exists(itemPath))
            {
                var sourceFile = new FileInfo(itemPath);
                if (sourceFile.LastWriteTimeUtc < oldestEligibleFileUtc)
                {
                    logger.LogDebug("Skipped old file {Path}.", childPath);
                }
                else if (remoteDirectoryIndex.Files.Contains(name))
                {
                    logger.LogDebug("Skipped existing remote file {Path}.", childPath);
                }
                else
                {
                    transfers.Add(new TransferJob(sourceFile, childPath));
                }
            }
        }
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

            client.UploadFile(sourceStream, BuildRemotePath(transfer.RelativePath), canOverride: false);
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

    private RemoteDirectoryIndex GetRemoteDirectoryIndex(
        SftpClient client,
        string relativePath,
        string directoryName,
        RemoteDirectoryIndex parentDirectoryIndex,
        ISet<string> knownRemoteDirectories)
    {
        if (parentDirectoryIndex.Files.Contains(directoryName))
        {
            return new RemoteDirectoryIndex();
        }

        var remotePath = BuildRemotePath(relativePath);
        if (parentDirectoryIndex.Directories.Contains(directoryName))
        {
            knownRemoteDirectories.Add(relativePath);
            return ListRemoteDirectory(client, remotePath);
        }

        return new RemoteDirectoryIndex();
    }

    private void EnsureRemoteDirectories(
        SftpClient client,
        IEnumerable<TransferJob> transfers,
        ISet<string> knownRemoteDirectories)
    {
        var requiredDirectories = transfers
            .SelectMany(transfer => GetParentDirectories(transfer.RelativePath))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path.Count(character => character == '/'))
            .ThenBy(path => path, StringComparer.Ordinal);

        foreach (var directoryPath in requiredDirectories)
        {
            if (knownRemoteDirectories.Contains(directoryPath))
            {
                continue;
            }

            client.CreateDirectory(BuildRemotePath(directoryPath));
            knownRemoteDirectories.Add(directoryPath);
        }
    }

    private static IEnumerable<string> GetParentDirectories(string relativeFilePath)
    {
        var segments = relativeFilePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var segmentCount = 1; segmentCount < segments.Length; segmentCount++)
        {
            yield return string.Join('/', segments[..segmentCount]);
        }
    }

    private static RemoteDirectoryIndex ListRemoteDirectory(SftpClient client, string remotePath)
    {
        var directoryIndex = new RemoteDirectoryIndex();

        foreach (var entry in client.ListDirectory(remotePath))
        {
            if (entry.Name is "." or "..")
            {
                continue;
            }

            if (entry.IsDirectory)
            {
                directoryIndex.Directories.Add(entry.Name);
            }
            else
            {
                directoryIndex.Files.Add(entry.Name);
            }
        }

        return directoryIndex;
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

    private sealed class RemoteDirectoryIndex
    {
        public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Files { get; } = new(StringComparer.Ordinal);
    }
}
