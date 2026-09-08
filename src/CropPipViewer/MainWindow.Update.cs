using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CropPipViewer;

public partial class MainWindow
{
    private const string GitHubReleasesApiUrl = "https://api.github.com/repos/hossea123456789-design/MaplePipManager/releases?per_page=20";
    private static readonly HttpClient UpdateHttpClient = CreateUpdateHttpClient();

    private bool _updateFeatureInitialized;
    private bool _updateCheckInProgress;
    private Button? _updateButton;
    private UpdateReleaseInfo? _availableUpdate;

    private sealed record UpdateReleaseInfo(
        string Version,
        string Tag,
        string ZipName,
        string ZipUrl,
        string? ChecksumUrl,
        string ReleaseUrl);

    private static HttpClient CreateUpdateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MaplePipManager-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private void InitializeUpdateFeature()
    {
        if (_updateFeatureInitialized) return;
        _updateFeatureInitialized = true;

        AddUpdateButtonToUi();

        // Startup check is intentionally silent when the current version is already latest.
        // When a newer release exists the button changes to an update call-to-action and the
        // status line informs the user without interrupting gameplay with a modal popup.
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            await Task.Delay(1200);
            await CheckForUpdatesAsync(showUpToDateMessage: false);
        }));
    }

    private void AddUpdateButtonToUi()
    {
        if (_updateButton != null) return;
        if (WindowCombo?.Parent is not Panel panel) return;

        _updateButton = new Button
        {
            Content = "업데이트 확인",
            Width = 112,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 6),
            ToolTip = $"현재 버전: {GetCurrentPublicVersion()}\nGitHub Release에서 새 버전을 확인합니다."
        };
        if (FindResource("SmallButton") is Style style) _updateButton.Style = style;
        _updateButton.Click += UpdateButton_Click;
        panel.Children.Add(_updateButton);
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateCheckInProgress) return;

        if (_availableUpdate == null)
        {
            await CheckForUpdatesAsync(showUpToDateMessage: true);
            return;
        }

        var update = _availableUpdate;
        var answer = MessageBox.Show(
            $"Maple PiP Manager {update.Version} 버전으로 업데이트합니다.\n\n" +
            "최신 ZIP을 자동으로 다운로드하고 압축을 푼 뒤,\n" +
            "현재 프로그램을 종료하여 파일을 교체하고 다시 실행합니다.\n\n" +
            "프리셋과 설정은 AppData에 저장되어 있으므로 그대로 유지됩니다.\n\n" +
            "지금 업데이트할까요?",
            "Maple PiP Manager 업데이트",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (answer != MessageBoxResult.Yes) return;
        await DownloadAndInstallUpdateAsync(update);
    }

    private async Task CheckForUpdatesAsync(bool showUpToDateMessage)
    {
        if (_updateCheckInProgress) return;
        _updateCheckInProgress = true;
        SetUpdateButtonState("확인 중...", enabled: false, highlight: false);

        try
        {
            using var response = await UpdateHttpClient.GetAsync(GitHubReleasesApiUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var latest = ParseNewestApplicableRelease(json, GetCurrentPublicVersion());

            _availableUpdate = latest;
            if (latest != null)
            {
                SetUpdateButtonState($"업데이트 {latest.Version}", enabled: true, highlight: true);
                if (StatusText != null)
                {
                    StatusText.Text = $"새 업데이트가 있습니다: {latest.Version} · 상단 업데이트 버튼을 눌러 적용할 수 있습니다.";
                }
                SettingsService.Log($"update_available | current={GetCurrentPublicVersion()} | latest={latest.Version}");
            }
            else
            {
                SetUpdateButtonState("업데이트 확인", enabled: true, highlight: false);
                if (showUpToDateMessage)
                {
                    MessageBox.Show(
                        $"현재 {GetCurrentPublicVersion()} 버전이 최신입니다.",
                        "업데이트 확인",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
        }
        catch (Exception ex)
        {
            SettingsService.Log("update_check_failed | " + ex);
            SetUpdateButtonState("업데이트 확인", enabled: true, highlight: false);
            if (showUpToDateMessage)
            {
                MessageBox.Show(
                    "업데이트 확인에 실패했습니다.\n인터넷 연결 또는 GitHub 접속 상태를 확인해 주세요.\n\n" + ex.Message,
                    "업데이트 확인 실패",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        finally
        {
            _updateCheckInProgress = false;
        }
    }

    private void SetUpdateButtonState(string text, bool enabled, bool highlight)
    {
        if (_updateButton == null) return;
        _updateButton.Content = text;
        _updateButton.IsEnabled = enabled;
        _updateButton.Width = highlight ? 138 : 112;
        _updateButton.FontWeight = highlight ? FontWeights.SemiBold : FontWeights.Normal;
        _updateButton.Background = highlight
            ? new SolidColorBrush(Color.FromRgb(255, 224, 130))
            : null;
        _updateButton.ToolTip = highlight && _availableUpdate != null
            ? $"현재 {GetCurrentPublicVersion()} → 최신 {_availableUpdate.Version}\n클릭하면 자동 다운로드/압축 해제/교체 후 재실행합니다."
            : $"현재 버전: {GetCurrentPublicVersion()}\nGitHub Release에서 새 버전을 확인합니다.";
    }

    private static UpdateReleaseInfo? ParseNewestApplicableRelease(string json, string currentVersion)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

        UpdateReleaseInfo? best = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            if (!release.TryGetProperty("tag_name", out var tagElement)) continue;

            var tag = tagElement.GetString() ?? string.Empty;
            var version = NormalizeVersionTag(tag);
            if (string.IsNullOrWhiteSpace(version)) continue;
            if (ComparePublicVersions(version, currentVersion) <= 0) continue;

            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;

            string? zipName = null;
            string? zipUrl = null;
            string? checksumUrl = null;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
                var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;

                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                    name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("source", StringComparison.OrdinalIgnoreCase))
                {
                    zipName = name;
                    zipUrl = url;
                }
            }

            if (zipName == null || zipUrl == null) continue;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
                var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() ?? string.Empty : string.Empty;
                if (string.Equals(name, zipName + ".sha256.txt", StringComparison.OrdinalIgnoreCase) ||
                    (name.Contains(zipName, StringComparison.OrdinalIgnoreCase) && name.Contains("sha256", StringComparison.OrdinalIgnoreCase)))
                {
                    checksumUrl = string.IsNullOrWhiteSpace(url) ? null : url;
                    break;
                }
            }

            var releaseUrl = release.TryGetProperty("html_url", out var htmlUrlElement)
                ? htmlUrlElement.GetString() ?? string.Empty
                : string.Empty;

            var candidate = new UpdateReleaseInfo(version, tag, zipName, zipUrl, checksumUrl, releaseUrl);
            if (best == null || ComparePublicVersions(candidate.Version, best.Version) > 0)
            {
                best = candidate;
            }
        }

        return best;
    }

    private static string GetCurrentPublicVersion()
    {
        try
        {
            var informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var withoutBuildMetadata = informational.Split('+', 2)[0];
                return NormalizeVersionTag(withoutBuildMetadata);
            }
        }
        catch
        {
        }

        return "0.9.1-beta";
    }

    private static string NormalizeVersionTag(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return string.Empty;
        var value = version.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        return value.Split('+', 2)[0].Trim();
    }

    private static int ComparePublicVersions(string left, string right)
    {
        var a = ParseVersionParts(left);
        var b = ParseVersionParts(right);
        var max = Math.Max(a.Numbers.Length, b.Numbers.Length);
        for (var i = 0; i < max; i++)
        {
            var av = i < a.Numbers.Length ? a.Numbers[i] : 0;
            var bv = i < b.Numbers.Length ? b.Numbers[i] : 0;
            var cmp = av.CompareTo(bv);
            if (cmp != 0) return cmp;
        }

        if (string.IsNullOrWhiteSpace(a.PreRelease) && string.IsNullOrWhiteSpace(b.PreRelease)) return 0;
        if (string.IsNullOrWhiteSpace(a.PreRelease)) return 1;
        if (string.IsNullOrWhiteSpace(b.PreRelease)) return -1;

        var ap = a.PreRelease.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var bp = b.PreRelease.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var preMax = Math.Max(ap.Length, bp.Length);
        for (var i = 0; i < preMax; i++)
        {
            if (i >= ap.Length) return -1;
            if (i >= bp.Length) return 1;
            var an = int.TryParse(ap[i], out var ai);
            var bn = int.TryParse(bp[i], out var bi);
            int cmp;
            if (an && bn) cmp = ai.CompareTo(bi);
            else if (an != bn) cmp = an ? -1 : 1;
            else cmp = string.Compare(ap[i], bp[i], StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    private static (int[] Numbers, string PreRelease) ParseVersionParts(string version)
    {
        var normalized = NormalizeVersionTag(version);
        var split = normalized.Split('-', 2);
        var numbers = split[0]
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out var value) ? value : 0)
            .ToArray();
        var pre = split.Length > 1 ? split[1] : string.Empty;
        return (numbers, pre);
    }

    private async Task DownloadAndInstallUpdateAsync(UpdateReleaseInfo update)
    {
        if (_updateCheckInProgress) return;
        _updateCheckInProgress = true;

        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
        {
            MessageBox.Show("현재 실행 파일 경로를 확인할 수 없어 자동 업데이트를 진행할 수 없습니다.", "업데이트 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
            _updateCheckInProgress = false;
            return;
        }

        var targetDir = Path.GetDirectoryName(currentExe)!;
        if (!CanWriteToDirectory(targetDir))
        {
            MessageBox.Show(
                "현재 프로그램 폴더에 파일을 쓸 권한이 없어 자동 업데이트를 적용할 수 없습니다.\n\n" +
                "프로그램을 일반 사용자 폴더(예: 바탕화면/문서)에 두고 다시 시도하거나 GitHub Release에서 수동으로 업데이트해 주세요.",
                "업데이트 권한 필요",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            _updateCheckInProgress = false;
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "MaplePipManager", "update-" + Guid.NewGuid().ToString("N"));
        var zipPath = Path.Combine(tempRoot, update.ZipName);
        var extractDir = Path.Combine(tempRoot, "payload");

        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(extractDir);

            SetUpdateButtonState("다운로드 0%", enabled: false, highlight: true);
            if (StatusText != null) StatusText.Text = $"{update.Version} 업데이트 파일을 다운로드하는 중입니다...";
            await DownloadFileWithProgressAsync(update.ZipUrl, zipPath);

            if (!string.IsNullOrWhiteSpace(update.ChecksumUrl))
            {
                SetUpdateButtonState("검증 중...", enabled: false, highlight: true);
                if (StatusText != null) StatusText.Text = "업데이트 파일 SHA-256을 확인하는 중입니다...";
                await VerifyChecksumAsync(zipPath, update.ChecksumUrl!);
            }

            SetUpdateButtonState("압축 해제 중...", enabled: false, highlight: true);
            if (StatusText != null) StatusText.Text = "업데이트 ZIP 압축을 해제하는 중입니다...";
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true));

            var newExe = Directory.GetFiles(extractDir, "CropPipViewer.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(newExe))
            {
                throw new InvalidDataException("업데이트 ZIP에서 CropPipViewer.exe를 찾을 수 없습니다.");
            }

            var payloadRoot = Path.GetDirectoryName(newExe)!;
            var updaterScript = CreateUpdaterScript(tempRoot);
            var targetExe = Path.Combine(targetDir, "CropPipViewer.exe");

            SetUpdateButtonState("재시작 준비...", enabled: false, highlight: true);
            if (StatusText != null) StatusText.Text = "업데이트 준비가 완료되었습니다. 프로그램을 종료한 뒤 자동으로 교체하고 다시 실행합니다.";
            SettingsService.Log($"update_install_start | from={GetCurrentPublicVersion()} | to={update.Version} | zip={update.ZipName}");

            LaunchUpdaterAndExit(updaterScript, payloadRoot, targetDir, targetExe, tempRoot);
        }
        catch (Exception ex)
        {
            SettingsService.Log("update_install_failed | " + ex);
            TryDeleteDirectory(tempRoot);
            SetUpdateButtonState(_availableUpdate != null ? $"업데이트 {_availableUpdate.Version}" : "업데이트 확인", enabled: true, highlight: _availableUpdate != null);
            MessageBox.Show(
                "자동 업데이트 중 문제가 발생했습니다.\n\n" + ex.Message +
                "\n\nGitHub Release 페이지에서 ZIP을 직접 받아 수동으로 교체할 수도 있습니다.",
                "업데이트 실패",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _updateCheckInProgress = false;
        }
    }

    private async Task DownloadFileWithProgressAsync(string url, string destinationPath)
    {
        using var response = await UpdateHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);
        var buffer = new byte[1024 * 128];
        long written = 0;
        var lastPercent = -1;

        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read <= 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read));
            written += read;

            if (total is > 0)
            {
                var percent = (int)Math.Clamp(written * 100L / total.Value, 0, 100);
                if (percent != lastPercent && (percent % 2 == 0 || percent == 100))
                {
                    lastPercent = percent;
                    SetUpdateButtonState($"다운로드 {percent}%", enabled: false, highlight: true);
                }
            }
        }
    }

    private static async Task VerifyChecksumAsync(string zipPath, string checksumUrl)
    {
        var checksumText = await UpdateHttpClient.GetStringAsync(checksumUrl);
        var match = Regex.Match(checksumText, "(?i)\\b[0-9a-f]{64}\\b");
        if (!match.Success)
        {
            throw new InvalidDataException("SHA-256 체크섬 파일 형식을 확인할 수 없습니다.");
        }

        var expected = match.Value.ToUpperInvariant();
        await using var stream = File.OpenRead(zipPath);
        var actualBytes = await SHA256.HashDataAsync(stream);
        var actual = Convert.ToHexString(actualBytes);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("다운로드한 업데이트 ZIP의 SHA-256 값이 Release 체크섬과 일치하지 않습니다.");
        }
    }

    private static bool CanWriteToDirectory(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".maplepip-update-write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string CreateUpdaterScript(string tempRoot)
    {
        var scriptPath = Path.Combine(tempRoot, "apply-update.ps1");
        const string script = """
param(
    [int]$ProcessId,
    [string]$SourceDir,
    [string]$TargetDir,
    [string]$ExecutablePath,
    [string]$CleanupRoot
)

$ErrorActionPreference = 'Stop'
try { Wait-Process -Id $ProcessId -ErrorAction SilentlyContinue } catch { }

$copied = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    try {
        Get-ChildItem -LiteralPath $SourceDir -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $TargetDir -Recurse -Force
        }
        $copied = $true
        break
    }
    catch {
        Start-Sleep -Seconds 1
    }
}

if (-not $copied) { exit 2 }

Start-Process -FilePath $ExecutablePath -WorkingDirectory $TargetDir
Start-Sleep -Milliseconds 800
try { Remove-Item -LiteralPath $CleanupRoot -Recurse -Force -ErrorAction SilentlyContinue } catch { }
""";

        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return scriptPath;
    }

    private static void LaunchUpdaterAndExit(string scriptPath, string sourceDir, string targetDir, string targetExe, string cleanupRoot)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = targetDir
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-WindowStyle");
        psi.ArgumentList.Add("Hidden");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("-ProcessId");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add("-SourceDir");
        psi.ArgumentList.Add(sourceDir);
        psi.ArgumentList.Add("-TargetDir");
        psi.ArgumentList.Add(targetDir);
        psi.ArgumentList.Add("-ExecutablePath");
        psi.ArgumentList.Add(targetExe);
        psi.ArgumentList.Add("-CleanupRoot");
        psi.ArgumentList.Add(cleanupRoot);

        Process.Start(psi);
        Application.Current.Shutdown();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
