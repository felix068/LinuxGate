using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LinuxGate.Services
{
    /// <summary>
    /// Service for disk operations using diskpart.
    /// </summary>
    public class DiskService
    {
        public class PartitionInfo
        {
            public int Number { get; set; }
            public string Type { get; set; }
            public double SizeMB { get; set; }
            public double OffsetMB { get; set; }
        }

        public class DiskpartResult
        {
            public bool Success { get; set; }
            public string Output { get; set; }
        }

        /// <summary>
        /// Runs a diskpart script and returns the output.
        /// </summary>
        public async Task<string> RunDiskpartAsync(string script)
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), $"diskpart_{Guid.NewGuid()}.txt");

            try
            {
                File.WriteAllText(scriptPath, script);
                return await RunDiskpartFromFileAsync(scriptPath);
            }
            finally
            {
                if (File.Exists(scriptPath))
                    File.Delete(scriptPath);
            }
        }

        /// <summary>
        /// Runs a diskpart script from a file path.
        /// </summary>
        public async Task<string> RunDiskpartFromFileAsync(string scriptPath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "diskpart.exe",
                        Arguments = $"/s \"{scriptPath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using (var process = Process.Start(psi))
                    {
                        string output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit();
                        return output;
                    }
                }
                catch (Exception ex)
                {
                    return $"Error: {ex.Message}";
                }
            });
        }

        /// <summary>
        /// Runs diskpart and returns a result with success status.
        /// </summary>
        public async Task<DiskpartResult> RunDiskpartWithResultAsync(string script)
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), $"diskpart_{Guid.NewGuid()}.txt");

            try
            {
                File.WriteAllText(scriptPath, script);

                return await Task.Run(() =>
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "diskpart.exe",
                            Arguments = $"/s \"{scriptPath}\"",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };

                        using (var process = Process.Start(psi))
                        {
                            string output = process.StandardOutput.ReadToEnd();
                            string error = process.StandardError.ReadToEnd();
                            process.WaitForExit();

                            bool hasError = output.ToLower().Contains("introuvable") ||
                                           output.ToLower().Contains("erreur") ||
                                           output.ToLower().Contains("error") ||
                                           output.ToLower().Contains("failed") ||
                                           output.ToLower().Contains("impossible") ||
                                           output.ToLower().Contains("insuffisant");

                            return new DiskpartResult
                            {
                                Success = process.ExitCode == 0 && !hasError,
                                Output = output + (string.IsNullOrEmpty(error) ? "" : $"\nERROR: {error}")
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        return new DiskpartResult
                        {
                            Success = false,
                            Output = $"Exception: {ex.Message}"
                        };
                    }
                });
            }
            finally
            {
                if (File.Exists(scriptPath))
                    File.Delete(scriptPath);
            }
        }

        /// <summary>
        /// Queries the maximum shrinkable space for the system volume.
        /// </summary>
        /// <returns>Maximum shrinkable space in MB.</returns>
        public async Task<double> QueryShrinkSpaceAsync()
        {
            string systemDrive = Path.GetPathRoot(Environment.SystemDirectory).TrimEnd('\\');

            string script = $@"rescan
select volume {systemDrive[0]}
shrink querymax
exit";

            string output = await RunDiskpartAsync(script);

            // French: "Le nombre maximal d'octets récupérables est :   12 GB (12445 Mo)"
            // English: "The maximum number of reclaimable bytes is: 12 GB"
            var match = Regex.Match(output, @"(\d+)\s*(?:GB|Go|G)\s*\((\d+)\s*Mo\)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return double.Parse(match.Groups[2].Value);
            }

            match = Regex.Match(output, @"(\d+)\s*(?:MB|Mo|M)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return double.Parse(match.Groups[1].Value);
            }

            return 0;
        }

        /// <summary>
        /// Shrinks the Windows partition by the specified amount.
        /// </summary>
        /// <param name="shrinkSizeMB">Size to shrink in MB.</param>
        /// <returns>True if successful.</returns>
        public async Task<bool> ShrinkPartitionAsync(double shrinkSizeMB)
        {
            string systemDrive = Path.GetPathRoot(Environment.SystemDirectory).TrimEnd('\\');

            string script = $@"rescan
list volume
select volume {systemDrive[0]}
shrink desired={shrinkSizeMB:F0}
exit";

            var result = await RunDiskpartWithResultAsync(script);

            if (result.Output.Contains("réduit") || result.Output.Contains("shrunk") || result.Output.Contains("reduced"))
            {
                return true;
            }

            if (result.Output.Contains("insuffisant") || result.Output.Contains("pas assez") || result.Output.Contains("not enough"))
            {
                return false;
            }

            return result.Success;
        }

        /// <summary>
        /// Creates a FAT32 partition with the specified size.
        /// </summary>
        /// <param name="sizeMB">Size in MB (default 2048 = 2GB).</param>
        /// <param name="label">Volume label.</param>
        /// <param name="driveLetter">Drive letter to assign.</param>
        /// <returns>True if successful.</returns>
        public async Task<bool> CreateFat32PartitionAsync(double sizeMB = 2048, string label = "LINUXGATE", char driveLetter = 'Z')
        {
            string script = $@"rescan
select disk 0
create partition primary size={sizeMB:F0}
format fs=fat32 quick label={label}
assign letter={driveLetter}
exit";

            var result = await RunDiskpartWithResultAsync(script);

            if (result.Output.Contains("créé") || result.Output.Contains("created") ||
                result.Output.Contains("formaté") || result.Output.Contains("formatted"))
            {
                return true;
            }

            return result.Success;
        }

        /// <summary>
        /// Validates the partition layout for Linux installation.
        /// </summary>
        /// <returns>Tuple of (isValid, warnings list).</returns>
        public async Task<(bool isValid, List<string> warnings)> ValidatePartitionLayoutAsync()
        {
            var warnings = new List<string>();

            string script = @"select disk 0
list partition
exit";

            string output = await RunDiskpartAsync(script);
            var partitions = ParsePartitionList(output);

            // Check 1: Should have exactly 3 partitions
            if (partitions.Count > 3)
            {
                warnings.Add($"Expected 3 partitions, found {partitions.Count}");
            }
            else if (partitions.Count < 3)
            {
                warnings.Add($"Expected 3 partitions, found only {partitions.Count}");
            }

            // Check 2: First partition should be between 40-150MB (EFI/System)
            if (partitions.Count > 0)
            {
                var firstPartition = partitions[0];
                if (firstPartition.SizeMB < 40 || firstPartition.SizeMB > 150)
                {
                    warnings.Add($"First partition size is {firstPartition.SizeMB:F0}MB (expected 40-150MB for System)");
                }
            }

            // Check 3: Last partition should be between 400-700MB (Recovery)
            if (partitions.Count > 0)
            {
                var lastPartition = partitions[partitions.Count - 1];
                if (lastPartition.SizeMB < 400 || lastPartition.SizeMB > 700)
                {
                    warnings.Add($"Last partition size is {lastPartition.SizeMB:F0}MB (expected 400-700MB for Recovery)");
                }
            }

            return (warnings.Count == 0, warnings);
        }

        /// <summary>
        /// Gets information about free space between partitions.
        /// </summary>
        /// <returns>Tuple of (freeSpaceSizeMB, recoveryOffsetMB).</returns>
        public async Task<(double freeSpaceSizeMB, double recoveryOffsetMB)> GetFreeSpaceInfoAsync()
        {
            string script = @"select disk 0
list partition
exit";

            string output = await RunDiskpartAsync(script);

            var partitions = new List<(int number, double offsetMB, double sizeMB)>();
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var partitionMatch = Regex.Match(line, @"Partition\s+(\d+)", RegexOptions.IgnoreCase);
                if (!partitionMatch.Success)
                    continue;

                int partitionNumber = int.Parse(partitionMatch.Groups[1].Value);
                var sizeMatches = Regex.Matches(line, @"(\d+)\s*(G|M|K)\s*o?", RegexOptions.IgnoreCase);

                if (sizeMatches.Count >= 2)
                {
                    double sizeMB = ParseSizeToMB(sizeMatches[0]);
                    double offsetMB = ParseSizeToMB(sizeMatches[1]);
                    partitions.Add((partitionNumber, offsetMB, sizeMB));
                }
            }

            if (partitions.Count < 2)
            {
                return (0, 0);
            }

            partitions.Sort((a, b) => a.offsetMB.CompareTo(b.offsetMB));

            var windowsPartition = partitions[1];
            double windowsEndMB = windowsPartition.offsetMB + windowsPartition.sizeMB;

            var recoveryPartition = partitions[partitions.Count - 1];
            double recoveryOffsetMB = recoveryPartition.offsetMB;

            double freeSpaceSizeMB = recoveryOffsetMB - windowsEndMB;

            return (freeSpaceSizeMB, recoveryOffsetMB);
        }

        /// <summary>
        /// Parses a diskpart partition list output.
        /// </summary>
        public List<PartitionInfo> ParsePartitionList(string output)
        {
            var partitions = new List<PartitionInfo>();
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var partitionMatch = Regex.Match(line, @"Partition\s+(\d+)", RegexOptions.IgnoreCase);
                if (!partitionMatch.Success)
                    continue;

                int partitionNumber = int.Parse(partitionMatch.Groups[1].Value);
                var sizeMatches = Regex.Matches(line, @"(\d+)\s*(G|M|K)\s*o?", RegexOptions.IgnoreCase);

                if (sizeMatches.Count > 0)
                {
                    var sizeMatch = sizeMatches[0];
                    double sizeMB = ParseSizeToMB(sizeMatch);

                    string type = "Unknown";
                    var typeMatch = Regex.Match(line, @"Partition\s+\d+\s+(\w+)", RegexOptions.IgnoreCase);
                    if (typeMatch.Success)
                    {
                        type = typeMatch.Groups[1].Value;
                    }

                    double offsetMB = 0;
                    if (sizeMatches.Count >= 2)
                    {
                        offsetMB = ParseSizeToMB(sizeMatches[1]);
                    }

                    partitions.Add(new PartitionInfo
                    {
                        Number = partitionNumber,
                        Type = type,
                        SizeMB = sizeMB,
                        OffsetMB = offsetMB
                    });
                }
            }

            return partitions;
        }

        private double ParseSizeToMB(Match match)
        {
            double size = double.Parse(match.Groups[1].Value);
            string unit = match.Groups[2].Value.ToUpper();

            switch (unit)
            {
                case "G":
                    return size * 1024;
                case "K":
                    return size / 1024;
                default:
                    return size;
            }
        }
    }
}
