using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Routing.Constraints;
using RdtClient.Data.Models.Data;
using Serilog;

namespace RdtClient.Service.Services.Downloaders;

public class SymlinkDownloader : IDownloader
{
    public event EventHandler<DownloadCompleteEventArgs>? DownloadComplete;
    public event EventHandler<DownloadProgressEventArgs>? DownloadProgress;

    private readonly Download _download;
    private readonly string _filePath;

    private readonly CancellationTokenSource _cancellationToken = new();

    private readonly ILogger _logger;

    public SymlinkDownloader(Download download, string filePath)
    {
        _logger = Log.ForContext<SymlinkDownloader>();
        _download = download;
        _filePath = filePath;
    }

    public async Task<string?> Download()
    {
        _logger.Debug($"Starting download of {_download.RemoteId}...");
        var filePath = new DirectoryInfo(_filePath);
        _logger.Debug($"Writing to path: ${filePath}");
        var fileName = filePath.Name;
        var fileExtension = filePath.Extension;
        var fileDirectory = Path.GetFileName(Path.GetDirectoryName(filePath.FullName));

        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var fileDirectoryWithoutExtension = Path.GetFileNameWithoutExtension(fileDirectory);

        string[] folders = { fileNameWithoutExtension, fileDirectoryWithoutExtension, fileName, fileDirectory };


        List<string> unWantedExtensions = new()
        {
            "zip", "rar", "tar"
        };

        if (unWantedExtensions.Any(unwanted => "." + fileExtension == unwanted))
        {
            DownloadComplete?.Invoke(this, new DownloadCompleteEventArgs
            {
                Error = $"Cant handle compressed files with symlink downloader"
            });
            return null;
        }

        DownloadProgress?.Invoke(this, new DownloadProgressEventArgs
        {
            BytesDone = 0,
            BytesTotal = 0,
            Speed = 0
        });

        FileInfo? file = null;
        var tries = 0;
        while (file == null && tries <= 10)
        {
            _logger.Debug($"Searching {Settings.Get.DownloadClient.RcloneMountPath} for {fileName} ({tries})...");
            file = TryGetFileFromFolders(folders, fileName);
            if (!String.IsNullOrWhiteSpace(Settings.Get.General.RcloneRefreshCommand))
            {
                RefreshRclone();
            }
            await Task.Delay(1000);
            tries++;
        }

        if (file != null)
        {

            List<string> filePaths = new List<string>();

            foreach (var fileSelected in _download.Torrent.Files)
            {
                _logger.Information($"Torrent file: {fileSelected.Path}");
                filePaths.Add(fileSelected.Path);
            }

            var result = await TryCreateSymbolicLink(file.FullName, filePath.FullName);

            if (result)
            {
                if (!String.IsNullOrWhiteSpace(Settings.Get.General.CopyAddedTorrents))
                {
                    try
                    {
                        var baseSymlinkPath = Path.Combine(Settings.Get.General.CopyAddedTorrents);

                        if (!Directory.Exists(baseSymlinkPath))
                        {
                            Directory.CreateDirectory(baseSymlinkPath);
                        }

                        var finalSymlinkPath = Path.Combine(baseSymlinkPath, fileName);

                        if (!fileName.Equals(fileDirectory, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.Debug("Checking for non-equality between fileName and fileDirectory.");

                            var nestedFolderPath = Path.Combine(baseSymlinkPath, fileDirectory);
                            _logger.Debug($"Constructed nested folder path: {nestedFolderPath}");

                            if (!Directory.Exists(nestedFolderPath))
                            {
                                Directory.CreateDirectory(nestedFolderPath);
                                _logger.Debug($"Nested folder created because it did not exist : {nestedFolderPath}");
                            }

                            string? foundPath = null;
                            _logger.Debug("Start searching for the corresponding path in the list of torrent files.");

                            foreach (var fileFoundPath in filePaths)
                            {
                                _logger.Debug($"Checking the file: {filePath}");
                                if (fileFoundPath.Contains(fileName))
                                {
                                    foundPath = fileFoundPath.TrimStart('/');
                                    _logger.Debug($"Matching path found and adjusted (without '/'): {foundPath}");
                                    break;
                                }
                            }

                            if (foundPath != null)
                            {
                                finalSymlinkPath = Path.Combine(nestedFolderPath, foundPath);
                                _logger.Debug($"Final path for symbolic link : {finalSymlinkPath}");
                            }
                            else
                            {
                                finalSymlinkPath = Path.Combine(nestedFolderPath, fileName);
                                _logger.Debug("No matching path found. Using the default behavior to construct finalSymlinkPath.");
                            }
                        }

                        if (TryCreateAdditionalSymbolicLink(file.FullName, finalSymlinkPath))
                        {
                            _logger.Information($"Successfully created both symbolic links for {fileName}");
                        }
                        else
                        {
                            _logger.Warning($"Only the primary symbolic link was created for {fileName}. Additional symlink failed.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"An unexpected error occurred while attempting to create additionnal symbolic links or move the torrent file for {fileDirectory}: {ex.Message}. Error details: {ex.StackTrace}");
                    }
                }

                DownloadComplete?.Invoke(this, new DownloadCompleteEventArgs());

                return file.FullName;
            }
        }

        DownloadComplete?.Invoke(this, new DownloadCompleteEventArgs
        {
            Error = "Could not find file from rclone mount!"
        });

        return null;

    }

    public Task Cancel()
    {
        _logger.Debug($"Cancelling download {_download.RemoteId}");

        _cancellationToken.Cancel(false);

        return Task.CompletedTask;
    }

    public Task Pause()
    {
        return Task.CompletedTask;
    }

    public Task Resume()
    {
        return Task.CompletedTask;
    }

    private async Task<bool> TryCreateSymbolicLink(string sourcePath, string symlinkPath)
    {
        try
        {
            File.CreateSymbolicLink(symlinkPath, sourcePath);
            if (File.Exists(symlinkPath))
            {
                _logger.Information($"Created symbolic link from {sourcePath} to {symlinkPath}");
                return true;
            }
            else
            {
                _logger.Error($"Failed to create symbolic link from {sourcePath} to {symlinkPath}");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error creating symbolic link from {sourcePath} to {symlinkPath}: {ex.Message}");
            return false;
        }
    }


    private bool TryCreateAdditionalSymbolicLink(string sourcePath, string additionalSymlinkPath)
    {
        try
        {
            var directoryPath = Path.GetDirectoryName(additionalSymlinkPath);
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            File.CreateSymbolicLink(additionalSymlinkPath, sourcePath);

            if (File.Exists(additionalSymlinkPath))
            {
                _logger.Information($"Created additional symbolic link from {sourcePath} to {additionalSymlinkPath}");
                return true;
            }
            else
            {
                _logger.Error($"Failed to create additional symbolic link from {sourcePath} to {additionalSymlinkPath}");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error creating additional symbolic link from {sourcePath} to {additionalSymlinkPath}: {ex.Message}");
            return false;
        }
    }

    private void RefreshRclone()
    {
        var processInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/rclone",
            Arguments = Settings.Get.General.RcloneRefreshCommand,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using (var process = Process.Start(processInfo))
        {
            if (process != null)
            {
                process.WaitForExit();
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                _logger.Debug($"rclone refresh output: {output}");
                _logger.Debug($"rclone refresh error: {error}");
            }
        }
    }


    private static FileInfo? TryGetFileFromFolders(string[] Folders, string File)
    {
        var dirInfo = new DirectoryInfo(Settings.Get.DownloadClient.RcloneMountPath);
        return dirInfo.EnumerateDirectories()
            .FirstOrDefault(dir => Folders.Contains(dir.Name), null)?
                .EnumerateFiles()
                .FirstOrDefault(x => x.Name == File, null);
    }
}
