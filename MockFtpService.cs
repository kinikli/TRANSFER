using FileUploadPortal.Models;

namespace FileUploadPortal.Services
{
    public class MockFtpService : IFtpService
    {
        private readonly ILogger<MockFtpService> _logger;
        private readonly string _mockDirectory;

        public MockFtpService(ILogger<MockFtpService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _mockDirectory = Path.Combine(Directory.GetCurrentDirectory(), "MockFtpUploads");
            Directory.CreateDirectory(_mockDirectory);
        }

        private string GetUniqueFileName(string directoryPath, string fileName)
        {
            var fullPath = Path.Combine(directoryPath, fileName);
            
            // Dosya yoksa orijinal ismi kullan
            if (!File.Exists(fullPath))
            {
                return fileName;
            }

            // Dosya var - yeni numara ekle
            var fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            var counter = 1;

            while (true)
            {
                var newFileName = $"{fileNameWithoutExt}_{counter}{extension}";
                var newFullPath = Path.Combine(directoryPath, newFileName);
                
                if (!File.Exists(newFullPath))
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
            try
            {
                // Create company directory
                var companyDir = Path.Combine(_mockDirectory, remotePath.Trim('/').Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(companyDir);

                // Benzersiz dosya ismi al (aynı isimde dosya varsa numara ekle)
                var uniqueFileName = GetUniqueFileName(companyDir, fileName);
                var filePath = Path.Combine(companyDir, uniqueFileName);
                
                var result = new FtpUploadResult
                {
                    FileName = uniqueFileName,
                    RemotePath = remotePath,
                    StartTime = DateTime.UtcNow
                };
                var totalBytes = fileStream.Length;
                var bytesWritten = 0L;

                // Progress reporting will be done inline during write

                // Copy stream to file with progress tracking
                // Using 8MB buffer for 50GB+ .mxf files - high performance streaming
                using var fileStreamOut = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8388608, useAsync: true);
                var buffer = new byte[8388608]; // 8MB buffer - optimal balance between speed and RAM
                int bytesRead;
                var lastProgressReport = DateTime.UtcNow;

                while ((bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None)) > 0)
                {
                    await fileStreamOut.WriteAsync(buffer, 0, bytesRead, CancellationToken.None);
                    bytesWritten += bytesRead;

                    // Report progress every 500ms to avoid too many reports
                    var now = DateTime.UtcNow;
                    if ((now - lastProgressReport).TotalMilliseconds >= 500)
                    {
                        var currentProgress = Math.Min((double)bytesWritten / totalBytes * 100, 100);
                        var speed = bytesWritten / (now - result.StartTime).TotalSeconds;
                        
                        progress?.Report(new UploadProgress
                        {
                            Progress = (int)currentProgress,
                            TransferredBytes = bytesWritten,
                            TransferSpeed = speed,
                            ETA = speed > 0 ? TimeSpan.FromSeconds((totalBytes - bytesWritten) / speed) : TimeSpan.Zero
                        });
                        
                        lastProgressReport = now;
                    }
                }

                await fileStreamOut.FlushAsync(CancellationToken.None);
                
                result.Success = true;
                result.EndTime = DateTime.Now;
                result.BytesTransferred = bytesWritten;
                result.TransferSpeed = result.Duration.TotalSeconds > 0 ? bytesWritten / result.Duration.TotalSeconds : 0;

                // Final progress report
                progress?.Report(new UploadProgress
                {
                    Progress = 100,
                    TransferredBytes = bytesWritten,
                    TransferSpeed = result.TransferSpeed,
                    ETA = TimeSpan.Zero
                });

                _logger.LogInformation("Mock FTP: Successfully saved {FileName} to {FilePath} ({Size} bytes)", 
                    uniqueFileName, filePath, bytesWritten);
                
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mock FTP: Error saving file {FileName}", fileName);
                
                return new FtpUploadResult
                {
                    FileName = fileName,
                    RemotePath = remotePath,
                    StartTime = DateTime.Now,
                    EndTime = DateTime.Now,
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        public Task<bool> DirectoryExistsAsync(string remotePath)
        {
            var localPath = Path.Combine(_mockDirectory, remotePath.Trim('/').Replace('/', Path.DirectorySeparatorChar));
            return Task.FromResult(Directory.Exists(localPath));
        }

        public Task<bool> CreateDirectoryAsync(string remotePath)
        {
            try
            {
                var localPath = Path.Combine(_mockDirectory, remotePath.Trim('/').Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(localPath);
                return Task.FromResult(true);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        public Task<bool> DeleteFileAsync(string remotePath)
        {
            try
            {
                var localPath = Path.Combine(_mockDirectory, remotePath.Trim('/').Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(localPath))
                {
                    File.Delete(localPath);
                    return Task.FromResult(true);
                }
                return Task.FromResult(false);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        public Task<FluentFTP.FtpListItem[]> ListDirectoryAsync(string remotePath)
        {
            try
            {
                var localPath = Path.Combine(_mockDirectory, remotePath.Trim('/').Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(localPath))
                    return Task.FromResult(Array.Empty<FluentFTP.FtpListItem>());

                var files = Directory.GetFiles(localPath);
                var items = files.Select(f => new FluentFTP.FtpListItem
                {
                    Name = Path.GetFileName(f),
                    FullName = f,
                    Type = FluentFTP.FtpObjectType.File,
                    Size = new FileInfo(f).Length,
                    Modified = File.GetLastWriteTime(f)
                }).ToArray();

                return Task.FromResult(items);
            }
            catch
            {
                return Task.FromResult(Array.Empty<FluentFTP.FtpListItem>());
            }
        }

        public Task<long> GetFileSizeAsync(string remotePath)
        {
            try
            {
                var localPath = Path.Combine(_mockDirectory, remotePath.Trim('/').Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(localPath))
                {
                    return Task.FromResult(new FileInfo(localPath).Length);
                }
                return Task.FromResult(-1L);
            }
            catch
            {
                return Task.FromResult(-1L);
            }
        }

        public Task<bool> TestConnectionAsync()
        {
            return Task.FromResult(true);
        }
    }
}