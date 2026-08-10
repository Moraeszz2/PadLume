using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace Padlume
{
    public sealed class UpdateInfo
    {
        public string Version { get; init; } = "";
        public string SetupUrl { get; init; } = "";
        public string ChecksumUrl { get; init; } = "";
    }

    public enum UpdateDownloadResult
    {
        Success,
        ChecksumMismatch,
        Failed,
    }

    /// <summary>
    /// Checks GitHub Releases for a newer Padlume version, and downloads/verifies/launches the
    /// installer for the one the user picks. GitHub's REST API is public and needs no authentication
    /// for a public repo's releases — just a User-Agent header, which GitHub requires on every request.
    /// </summary>
    public static class UpdateChecker
    {
        private const string LatestReleaseUrl = "https://api.github.com/repos/Moraeszz2/PadLume/releases/latest";
        private const string UserAgent = "Padlume-App";

        public static async Task<UpdateInfo?> CheckForUpdateAsync()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

                var json = await http.GetStringAsync(LatestReleaseUrl);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var tagName = root.GetProperty("tag_name").GetString();
                if (string.IsNullOrEmpty(tagName))
                    return null;

                var versionText = tagName.TrimStart('v', 'V');
                if (!Version.TryParse(versionText, out var latestVersion))
                    return null;

                var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
                if (latestVersion <= currentVersion)
                    return null;

                string? setupUrl = null;
                string? checksumUrl = null;
                foreach (var asset in root.GetProperty("assets").EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString();
                    var url = asset.GetProperty("browser_download_url").GetString();
                    if (name == "Setup.exe")
                        setupUrl = url;
                    else if (name == "Setup.exe.sha256")
                        checksumUrl = url;
                }

                if (setupUrl == null || checksumUrl == null)
                    return null;

                return new UpdateInfo { Version = versionText, SetupUrl = setupUrl, ChecksumUrl = checksumUrl };
            }
            catch (Exception ex)
            {
                // No network, GitHub unreachable, rate-limited, malformed response, etc. — never worth
                // bothering the user over; the next launch just tries again.
                App.Log("UpdateChecker", $"CheckForUpdateAsync failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Downloads the installer and checks its SHA256 against the checksum GitHub Actions
        /// published alongside it before touching disk with anything executable. ChecksumMismatch is
        /// reported separately from a generic Failed — this is a "fail closed" security boundary, not
        /// just a data-integrity check (what's being verified is about to be run elevated), and the
        /// caller shows the user a more specific message for it than for an ordinary network hiccup.
        /// Streams to disk in chunks (rather than buffering the whole ~50MB in memory first) so
        /// <paramref name="progress"/> can report real 0-100 percentages as bytes actually arrive, and
        /// hashes incrementally alongside the same pass instead of re-reading the file afterward.</summary>
        public static async Task<(UpdateDownloadResult Result, string? SetupPath)> DownloadAndVerifyAsync(UpdateInfo info, IProgress<double>? progress = null)
        {
            string? tempPath = null;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

                var checksumLine = await http.GetStringAsync(info.ChecksumUrl);
                var expectedHash = checksumLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0].Trim();

                using var response = await http.GetAsync(info.SetupUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength;

                tempPath = Path.Combine(Path.GetTempPath(), $"Padlume-Setup-{info.Version}.exe");
                using (var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    long totalRead = 0;
                    int bytesRead;
                    while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                        incrementalHash.AppendData(buffer, 0, bytesRead);
                        totalRead += bytesRead;
                        if (totalBytes is > 0)
                            progress?.Report(100.0 * totalRead / totalBytes.Value);
                    }

                    var actualHash = Convert.ToHexString(incrementalHash.GetHashAndReset());
                    if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        App.Log("UpdateChecker", $"Checksum mismatch for {info.Version}: expected {expectedHash}, got {actualHash}.");
                        fileStream.Close();
                        TryDelete(tempPath);
                        return (UpdateDownloadResult.ChecksumMismatch, null);
                    }
                }

                return (UpdateDownloadResult.Success, tempPath);
            }
            catch (Exception ex)
            {
                App.Log("UpdateChecker", $"DownloadAndVerifyAsync failed: {ex.Message}");
                if (tempPath != null)
                    TryDelete(tempPath);
                return (UpdateDownloadResult.Failed, null);
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* best-effort cleanup of a partial/rejected download */ }
        }

        /// <summary>Launches the (already checksum-verified) installer silently and lets it relaunch
        /// Padlume on its own once done (see installer/Padlume.iss [Run]). Our own process already runs
        /// elevated (app.manifest), so this ShellExecute-based launch of another elevated exe inherits
        /// that without a second UAC prompt — different from the postinstall-launch case in the
        /// installer itself, which explicitly hands off to a de-elevated token (see the error 740 fix in
        /// Padlume.iss for why that one needed a workaround).</summary>
        public static bool LaunchInstallerSilently(string setupPath)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = setupPath,
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                    UseShellExecute = true,
                });
                return true;
            }
            catch (Exception ex)
            {
                App.Log("UpdateChecker", $"LaunchInstallerSilently failed: {ex.Message}");
                return false;
            }
        }
    }
}
