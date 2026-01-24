using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace LinuxGate.Services
{
    /// <summary>
    /// Service for downloading files with progress reporting.
    /// </summary>
    public class DownloadService
    {
        /// <summary>
        /// Progress information for download operations.
        /// </summary>
        public class DownloadProgress
        {
            public long BytesDownloaded { get; set; }
            public long TotalBytes { get; set; }
            public int PercentComplete { get; set; }
            public double DownloadedMB => BytesDownloaded / 1024.0 / 1024.0;
            public double TotalMB => TotalBytes / 1024.0 / 1024.0;
        }

        /// <summary>
        /// Downloads a file from URL to destination path.
        /// </summary>
        /// <param name="url">Source URL.</param>
        /// <param name="destinationPath">Destination file path.</param>
        /// <param name="timeoutMinutes">Timeout in minutes (default 5).</param>
        /// <returns>True if successful.</returns>
        public async Task<bool> DownloadFileAsync(string url, string destinationPath, int timeoutMinutes = 5)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromMinutes(timeoutMinutes);
                    var data = await client.GetByteArrayAsync(url);
                    File.WriteAllBytes(destinationPath, data);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Downloads a file with progress reporting.
        /// </summary>
        /// <param name="url">Source URL.</param>
        /// <param name="destinationPath">Destination file path.</param>
        /// <param name="progressCallback">Callback for progress updates.</param>
        /// <param name="timeoutHours">Timeout in hours (default 2).</param>
        /// <param name="bufferSize">Buffer size in bytes (default 8192).</param>
        /// <returns>True if successful.</returns>
        public async Task<bool> DownloadWithProgressAsync(
            string url,
            string destinationPath,
            Action<DownloadProgress> progressCallback,
            int timeoutHours = 2,
            int bufferSize = 8192)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromHours(timeoutHours);

                    using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();

                        var totalBytes = response.Content.Headers.ContentLength ?? 0;

                        using (var contentStream = await response.Content.ReadAsStreamAsync())
                        using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, true))
                        {
                            var buffer = new byte[bufferSize];
                            long totalRead = 0;
                            int bytesRead;
                            var lastProgressUpdate = DateTime.Now;

                            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await fileStream.WriteAsync(buffer, 0, bytesRead);
                                totalRead += bytesRead;

                                // Update progress every 500ms
                                if ((DateTime.Now - lastProgressUpdate).TotalMilliseconds > 500)
                                {
                                    var progress = new DownloadProgress
                                    {
                                        BytesDownloaded = totalRead,
                                        TotalBytes = totalBytes,
                                        PercentComplete = totalBytes > 0 ? (int)(totalRead * 100 / totalBytes) : 0
                                    };
                                    progressCallback?.Invoke(progress);
                                    lastProgressUpdate = DateTime.Now;
                                }
                            }
                        }
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Downloads an ISO file with progress reporting.
        /// </summary>
        /// <param name="url">Source URL.</param>
        /// <param name="destinationPath">Destination file path.</param>
        /// <param name="progressCallback">Callback for progress updates.</param>
        /// <returns>True if successful.</returns>
        public async Task<bool> DownloadIsoAsync(
            string url,
            string destinationPath,
            Action<DownloadProgress> progressCallback)
        {
            return await DownloadWithProgressAsync(url, destinationPath, progressCallback, timeoutHours: 2, bufferSize: 8192);
        }

        /// <summary>
        /// Downloads a large installer ISO with progress reporting.
        /// </summary>
        /// <param name="url">Source URL.</param>
        /// <param name="destinationPath">Destination file path.</param>
        /// <param name="progressCallback">Callback for progress updates.</param>
        /// <returns>True if successful.</returns>
        public async Task<bool> DownloadInstallerIsoAsync(
            string url,
            string destinationPath,
            Action<DownloadProgress> progressCallback)
        {
            // Use larger buffer and longer timeout for large installer ISOs
            return await DownloadWithProgressAsync(url, destinationPath, progressCallback, timeoutHours: 4, bufferSize: 81920);
        }
    }
}
