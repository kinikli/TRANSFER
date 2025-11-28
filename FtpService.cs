using FluentFTP;
using FileUploadPortal.Models;

namespace FileUploadPortal.Services
{
    public interface IFtpService
    {
        Task<FtpUploadResult> UploadFileAsync(Stream fileStream, string remotePath, string fileName, IProgress<UploadProgress>? progress = null, CancellationToken cancellationToken = default);
        Task<bool> DirectoryExistsAsync(string remotePath);
        Task<bool> CreateDirectoryAsync(string remotePath);
        Task<bool> DeleteFileAsync(string remotePath);
        Task<FtpListItem[]> ListDirectoryAsync(string remotePath);
        Task<long> GetFileSizeAsync(string remotePath);
        Task<bool> TestConnectionAsync();
    }

    public class FtpService : IFtpService, IDisposable
    {
        private readonly FtpSettings _config;
        private readonly ILogger<FtpService> _logger;
        private AsyncFtpClient? _ftpClient;

        public FtpService(IConfiguration configuration, ILogger<FtpService> logger)
        {
            _config = configuration.GetSection("FTP").Get<FtpSettings>() ?? new FtpSettings();
            _logger = logger;
        }

        private async Task<AsyncFtpClient> GetFtpClientAsync()
        {
            if (_ftpClient == null || !_ftpClient.IsConnected)
            {
                _ftpClient?.Dispose();
                _ftpClient = new AsyncFtpClient(_config.Host, _config.Username, _config.Password, _config.Port);
                
                // Configure FTP settings for optimal streaming with resume support
                _ftpClient.Config.EncryptionMode = _config.UseSSL ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None;
                _ftpClient.Config.DataConnectionType = FtpDataConnectionType.PASV;
                _ftpClient.Config.TransferChunkSize = 8388608; // 8MB chunks for 50GB+ files
                _ftpClient.Config.LocalFileBufferSize = 8388608;
                _ftpClient.Config.UploadRateLimit = 0; // No limit
                _ftpClient.Config.RetryAttempts = 5; // More retries for large files
                _ftpClient.Config.ConnectTimeout = 60000; // 60 seconds
                _ftpClient.Config.DataConnectionConnectTimeout = 60000;
                _ftpClient.Config.DataConnectionReadTimeout = 600000; // 10 minutes for large chunks
                _ftpClient.Config.SocketKeepAlive = true; // Keep connection alive
                
                await _ftpClient.Connect();
                
                _logger.LogInformation("Connected to FTP server {Host}:{Port}", _config.Host, _config.Port);
            }

            return _ftpClient;
        }

        private async Task<string> GetUniqueFileNameAsync(AsyncFtpClient client, string remotePath, string fileName)
        {
            var fullPath = $"{remotePath.TrimEnd('/')}/{fileName}";
            
            // Dosya yoksa orijinal ismi kullan
            if (!await client.FileExists(fullPath))
            {
                return fileName;
            }

            // Dosya var - boyutları kontrol et
            var fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            var counter = 1;

            while (true)
            {
                var newFileName = $"{fileNameWithoutExt}_{counter}{extension}";
                var newFullPath = $"{remotePath.TrimEnd('/')}/{newFileName}";
                
                if (!await client.FileExists(newFullPath))
                {
                    _logger.LogInformation("File {OriginalName} already exists. Using new name: {NewName}", fileName, newFileName);
                    return newFileName;
                }
                
                counter++;
                
                // Sonsuz döngüyü önle (max 1000 dosya)
                if (counter > 1000)
                {
                    throw new Exception($"Too many files with name pattern: {fileNameWithoutExt}_*{extension}");
                }
            }
        }

        public async Task<FtpUploadResult> UploadFileAsync(Stream fileStream, string remotePath, string fileName, IProgress<UploadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var client = await GetFtpClientAsync();
            
            // Benzersiz dosya ismi al (aynı isimde dosya varsa numara ekle)
            var uniqueFileName = await GetUniqueFileNameAsync(client, remotePath, fileName);
            
            var result = new FtpUploadResult
            {
                FileName = uniqueFileName,
                RemotePath = remotePath,
                StartTime = DateTime.Now
            };

            try
            {
                var fullRemotePath = $"{remotePath.TrimEnd('/')}/{uniqueFileName}";

                // Ensure directory exists
                var directoryPath = remotePath.TrimEnd('/');
                if (!await client.DirectoryExists(directoryPath))
                {
                    await client.CreateDirectory(directoryPath);
                    _logger.LogInformation("Created directory: {Directory}", directoryPath);
                }

                // Get file size for progress calculation
                var totalBytes = fileStream.Length;

                // Check if file partially exists (resume support)
                long existingSize = 0;
                FtpRemoteExists existsMode = FtpRemoteExists.Overwrite;
                
                if (await client.FileExists(fullRemotePath))
                {
                    existingSize = await client.GetFileSize(fullRemotePath);
                    
                    if (existingSize > 0 && existingSize < totalBytes)
                    {
                        // Resume from existing position
                        _logger.LogInformation("Resuming upload of {FileName} from byte {Position} of {Total}", 
                            uniqueFileName, existingSize, totalBytes);
                        
                        // Seek stream to resume position
                        if (fileStream.CanSeek)
                        {
                            fileStream.Seek(existingSize, SeekOrigin.Begin);
                            existsMode = FtpRemoteExists.Resume;
                        }
                        else
                        {
                            _logger.LogWarning("Stream is not seekable, cannot resume. Starting fresh upload.");
                            existsMode = FtpRemoteExists.Overwrite;
                        }
                    }
                    else if (existingSize >= totalBytes)
                    {
                        // File already complete
                        _logger.LogInformation("File {FileName} already exists with same or larger size, skipping", uniqueFileName);
                        result.Success = true;
                        result.EndTime = DateTime.UtcNow;
                        result.BytesTransferred = totalBytes;
                        return result;
                    }
                }

                // Upload with streaming - this pipes directly from HTTP request to FTP
                FtpStatus uploadStatus;
                
                // Monitor progress in background task
                var progressTask = MonitorProgressAsync(fullRemotePath, totalBytes, progress, cancellationToken);
                
                try
                {
                    // Upload with resume support
                    uploadStatus = await client.UploadStream(fileStream, fullRemotePath, 
                        existsMode, createRemoteDir: false, 
                        token: cancellationToken);
                }
                finally
                {
                    // Stop progress monitoring
                    progressTask.Wait(1000); // Wait max 1 second
                }

                result.Success = uploadStatus == FtpStatus.Success;
                result.EndTime = DateTime.Now;
                result.BytesTransferred = totalBytes;
                
                // Calculate average speed
                var duration = result.Duration.TotalSeconds;
                result.TransferSpeed = duration > 0 ? totalBytes / duration : 0;

                if (result.Success)
                {
                    // Verify upload
                    var uploadedSize = await client.GetFileSize(fullRemotePath);
                    result.Success = uploadedSize == totalBytes;
                    
                    if (result.Success)
                    {
                        _logger.LogInformation("Successfully uploaded {FileName} ({Size} bytes) in {Duration}ms at {Speed} KB/s", 
                            uniqueFileName, totalBytes, result.Duration.TotalMilliseconds, result.TransferSpeed / 1024);
                    }
                    else
                    {
                        result.ErrorMessage = $"Size mismatch: expected {totalBytes}, got {uploadedSize}";
                        _logger.LogError("Upload verification failed: {Error}", result.ErrorMessage);
                    }
                }
                else
                {
                    result.ErrorMessage = $"Upload failed with status: {uploadStatus}";
                    _logger.LogError("Upload failed: {Status}", uploadStatus);
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.EndTime = DateTime.Now;
                
                _logger.LogError(ex, "Error uploading file {FileName} to {RemotePath}", fileName, remotePath);
            }

            return result;
        }

        public async Task<bool> DirectoryExistsAsync(string remotePath)
        {
            try
            {
                var client = await GetFtpClientAsync();
                return await client.DirectoryExists(remotePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking directory existence: {Path}", remotePath);
                return false;
            }
        }

        public async Task<bool> CreateDirectoryAsync(string remotePath)
        {
            try
            {
                var client = await GetFtpClientAsync();
                await client.CreateDirectory(remotePath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating directory: {Path}", remotePath);
                return false;
            }
        }

        public async Task<bool> DeleteFileAsync(string remotePath)
        {
            try
            {
                var client = await GetFtpClientAsync();
                await client.DeleteFile(remotePath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting file: {Path}", remotePath);
                return false;
            }
        }

        public async Task<FtpListItem[]> ListDirectoryAsync(string remotePath)
        {
            try
            {
                var client = await GetFtpClientAsync();
                return await client.GetListing(remotePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing directory: {Path}", remotePath);
                return Array.Empty<FtpListItem>();
            }
        }

        public async Task<long> GetFileSizeAsync(string remotePath)
        {
            try
            {
                var client = await GetFtpClientAsync();
                return await client.GetFileSize(remotePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting file size: {Path}", remotePath);
                return -1;
            }
        }

        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                var client = await GetFtpClientAsync();
                return client.IsConnected;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FTP connection test failed");
                return false;
            }
        }

        private async Task MonitorProgressAsync(string filePath, long totalBytes, IProgress<UploadProgress>? progress, CancellationToken cancellationToken)
        {
            if (progress == null) return;

            var startTime = DateTime.Now;
            var lastReportTime = startTime;
            
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(1000, cancellationToken); // Check every second
                    
                    try
                    {
                        var client = await GetFtpClientAsync();
                        var currentSize = await client.GetFileSize(filePath);
                        
                        if (currentSize >= 0)
                        {
                            var elapsed = DateTime.Now - startTime;
                            var speed = elapsed.TotalSeconds > 0 ? currentSize / elapsed.TotalSeconds : 0;
                            var progressPercent = totalBytes > 0 ? (int)((double)currentSize / totalBytes * 100) : 0;
                            var eta = speed > 0 ? TimeSpan.FromSeconds((totalBytes - currentSize) / speed) : TimeSpan.Zero;
                            
                            progress.Report(new UploadProgress
                            {
                                Progress = Math.Min(progressPercent, 100),
                                TransferredBytes = currentSize,
                                TransferSpeed = speed,
                                ETA = eta
                            });
                            
                            // Exit if upload is complete
                            if (currentSize >= totalBytes)
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error monitoring upload progress");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
        }

        public void Dispose()
        {
            _ftpClient?.Dispose();
        }
    }
}