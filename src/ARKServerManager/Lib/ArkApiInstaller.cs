using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace ServerManagerTool.Lib
{
    /// <summary>
    /// Installs, updates and removes ArkApi (the ARK: Survival Evolved server API, "version.dll" build) in a server's ShooterGame\Binaries\Win64
    /// folder. The package (version.dll, msdia140.dll and a default config.json) is a GitHub release of this manager's own repository, so
    /// it matches the build this cluster runs. The plugins in ArkApi\Plugins and the config.json of a server are never touched on an update.
    /// </summary>
    public static class ArkApiInstaller
    {
        private const string ReleaseBase = "https://github.com/jamakenjelly/ServerManagers-source-ase/releases/download/arkapi/";
        private const string PackageUrl = ReleaseBase + "ArkApi-Package.zip";
        private const string VersionUrl = ReleaseBase + "arkapi-version.txt";

        private const string LoaderFile = "version.dll";
        private const string VersionFile = "asm_arkapi_version.txt";

        private static string BinaryDir(string installDirectory) => Path.Combine(installDirectory ?? string.Empty, Config.Default.ServerBinaryRelativePath);

        public static bool IsInstalled(string installDirectory) => File.Exists(Path.Combine(BinaryDir(installDirectory), LoaderFile));

        /// <summary>The version the manager installed (empty when ArkApi was put there some other way).</summary>
        public static string GetInstalledVersion(string installDirectory)
        {
            try
            {
                var file = Path.Combine(BinaryDir(installDirectory), "ArkApi", VersionFile);
                return File.Exists(file) ? File.ReadAllText(file).Trim() : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static WebClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new WebClient();
            client.Headers[HttpRequestHeader.UserAgent] = "ASEARKServerManager";
            client.Headers[HttpRequestHeader.CacheControl] = "no-cache";
            return client;
        }

        /// <summary>The newest ArkApi version the manager can install (from its GitHub release), or empty when it cannot be reached.</summary>
        public static async Task<string> GetLatestVersionAsync()
        {
            try
            {
                using (var client = CreateClient())
                    return (await client.DownloadStringTaskAsync(VersionUrl + "?t=" + DateTime.UtcNow.Ticks)).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>Downloads the ArkApi package and unpacks it into the server. Returns a message describing the result.</summary>
        public static async Task<string> InstallAsync(string installDirectory, Action<string> progress)
        {
            var binaryDir = BinaryDir(installDirectory);
            if (!Directory.Exists(binaryDir))
                throw new DirectoryNotFoundException($"The server is not installed yet ({binaryDir} was not found).");

            progress?.Invoke("Looking up the ArkApi version...");
            var version = await GetLatestVersionAsync();
            if (string.IsNullOrEmpty(version))
                throw new InvalidOperationException("Could not reach the ArkApi package on GitHub. Check the internet connection and try again.");

            var tempZip = Path.Combine(Path.GetTempPath(), $"arkapi_{Guid.NewGuid():N}.zip");
            try
            {
                progress?.Invoke($"Downloading ArkApi {version}...");
                using (var client = CreateClient())
                {
                    client.DownloadProgressChanged += (s, e) => progress?.Invoke($"Downloading ArkApi {version}... {e.ProgressPercentage}%");
                    await client.DownloadFileTaskAsync(PackageUrl, tempZip);
                }

                progress?.Invoke("Installing...");
                var copied = 0;
                using (var archive = ZipFile.OpenRead(tempZip))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name))
                            continue; // folder

                        var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        var target = Path.GetFullPath(Path.Combine(binaryDir, relative));
                        if (!target.StartsWith(Path.GetFullPath(binaryDir), StringComparison.OrdinalIgnoreCase))
                            continue; // never write outside the server's Win64 folder

                        // keep the server's own settings and plugins; only the loader files are replaced on an update
                        var isConfig = string.Equals(relative, "config.json", StringComparison.OrdinalIgnoreCase);
                        var isPlugin = relative.StartsWith(Path.Combine("ArkApi", "Plugins") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                        if ((isConfig || isPlugin) && File.Exists(target))
                            continue;

                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        entry.ExtractToFile(target, true);
                        copied++;
                    }
                }

                Directory.CreateDirectory(Path.Combine(binaryDir, "ArkApi", "Plugins"));
                File.WriteAllText(Path.Combine(binaryDir, "ArkApi", VersionFile), version);
                return $"ArkApi {version} installed ({copied} files). Plugins go in ArkApi\\Plugins.";
            }
            finally
            {
                try { File.Delete(tempZip); } catch { }
            }
        }

        /// <summary>Removes the loader (version.dll). The ArkApi folder with the plugins, config.json and msdia140.dll are left in place.</summary>
        public static string Uninstall(string installDirectory)
        {
            var binaryDir = BinaryDir(installDirectory);
            var removed = 0;
            foreach (var file in new[] { LoaderFile, Path.Combine("ArkApi", VersionFile) })
            {
                var path = Path.Combine(binaryDir, file);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removed++;
                }
            }

            return removed > 0
                ? "ArkApi removed. Your plugins and settings were left in ArkApi\\Plugins."
                : "ArkApi was not installed.";
        }
    }
}
