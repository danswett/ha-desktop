using System.Diagnostics;
using System.Net.Http.Headers;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// Downloads a release's installer and hands the installation to a helper that outlives
/// this process.
///
/// The handover is the whole point. An MSI upgrade replaces the running executable, so
/// something has to still be alive once the app has gone: the helper waits for it to
/// exit, runs the installer, and starts the new build. Doing it the other way round -
/// letting the installer close the app - does not work here, because the app treats a
/// close as "hide to the notification area" and would keep its files locked.
/// </summary>
public sealed class UpdateInstaller
{
    private readonly Func<string?> _tokenProvider;

    public UpdateInstaller(Func<string?> tokenProvider)
    {
        _tokenProvider = tokenProvider;
    }

    /// <summary>Where the MSI puts the app, and so where the helper restarts it.</summary>
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "HomeAssistantDesktop");

    public static string InstalledExecutable =>
        Path.Combine(InstallDirectory, "HomeAssistant.Desktop.exe");

    /// <summary>
    /// Whether this build is the installed one. A copy run from a build folder must not
    /// update itself: the MSI would replace the installed app instead, and the helper
    /// would then start that one, silently swapping which build is running.
    /// </summary>
    public static bool IsInstalledBuild()
    {
        var running = Environment.ProcessPath;
        if (string.IsNullOrEmpty(running))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(running),
            Path.GetFullPath(InstalledExecutable),
            StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string?> DownloadAsync(
        ReleaseInfo release, Action<int> onProgress, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(release.InstallerUrl))
        {
            Log.Warn("update", $"{release.Tag} has no installer attached");
            return null;
        }

        var target = Path.Combine(
            Path.GetTempPath(), $"HomeAssistantDesktop-{release.Version}.msi");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("HomeAssistantDesktop", ReleaseChecker.Current.ToString()));

            if (_tokenProvider() is { Length: > 0 } credential)
            {
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", credential);
                // A private repository serves assets only to this media type.
                http.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            }

            using var response = await http.GetAsync(
                release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, token);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("update", $"the download failed ({(int)response.StatusCode})");
                return null;
            }

            var total = response.Content.Headers.ContentLength ?? release.InstallerSize;
            var copied = 0L;
            var lastReported = -1;

            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var file = File.Create(target))
            {
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), token);
                    copied += read;

                    if (total <= 0)
                    {
                        continue;
                    }

                    // Only on change: every report is an MQTT publish and a state write.
                    var percent = (int)(copied * 100 / total);
                    if (percent != lastReported)
                    {
                        lastReported = percent;
                        onProgress(percent);
                    }
                }
            }

            // A truncated download still produces a file, and msiexec's complaint about
            // it would arrive long after the app had exited and taken its log with it.
            if (release.InstallerSize > 0 && copied != release.InstallerSize)
            {
                Log.Warn("update", $"the download was {copied} bytes, expected {release.InstallerSize}");
                TryDelete(target);
                return null;
            }

            Log.Info("update", $"downloaded {release.Tag} ({copied / 1024 / 1024} MB)");
            return target;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            Log.Warn("update", $"could not download {release.Tag}: {ex.Message}");
            TryDelete(target);
            return null;
        }
    }

    /// <summary>
    /// Starts the helper and returns. The caller is expected to exit immediately
    /// afterwards; the helper waits for exactly that.
    /// </summary>
    public static bool BeginInstall(string msiPath)
    {
        var helper = Path.Combine(
            Path.GetTempPath(), $"ha-desktop-update-{Guid.NewGuid():N}.ps1");

        try
        {
            File.WriteAllText(helper, HelperScript);

            var info = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in new[]
            {
                "-NoLogo", "-NoProfile", "-NonInteractive",
                "-ExecutionPolicy", "Bypass",
                "-File", helper,
                "-WaitPid", Environment.ProcessId.ToString(),
                "-Msi", msiPath,
                "-Exe", InstalledExecutable,
            })
            {
                info.ArgumentList.Add(argument);
            }

            var process = Process.Start(info);
            if (process is null)
            {
                Log.Warn("update", "could not start the installer helper");
                return false;
            }

            Log.Info("update", $"installer helper started (pid {process.Id}); exiting to let it work");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Warn("update", $"could not start the installer helper: {ex.Message}");
            TryDelete(helper);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover file in the temp directory is not worth reporting.
        }
    }

    /// <summary>
    /// Runs after this process is gone, so it cannot report anything to the app's log.
    /// It writes its own next to it instead, which is the only account of what happened
    /// if the new build fails to start.
    /// </summary>
    private const string HelperScript = """
        param(
            [Parameter(Mandatory)][int]$WaitPid,
            [Parameter(Mandatory)][string]$Msi,
            [Parameter(Mandatory)][string]$Exe
        )

        $log = Join-Path $env:LOCALAPPDATA 'HomeAssistantDesktop\update.log'
        function Write-Step([string]$Message) {
            $line = '[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
            try { Add-Content -LiteralPath $log -Value $line -ErrorAction Stop } catch { }
        }

        Write-Step "waiting for pid $WaitPid to exit"
        for ($i = 0; $i -lt 60; $i++) {
            if (-not (Get-Process -Id $WaitPid -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Seconds 1
        }

        $stuck = Get-Process -Id $WaitPid -ErrorAction SilentlyContinue
        if ($stuck) {
            # Nothing else can free the files, and leaving them locked fails the install.
            Write-Step 'it did not exit; ending it'
            try { $stuck.Kill() } catch { }
            Start-Sleep -Seconds 3
        }

        Write-Step "installing $Msi"
        $install = Start-Process msiexec.exe -ArgumentList '/i', "`"$Msi`"", '/qn' -Wait -PassThru
        Write-Step "msiexec exit code $($install.ExitCode)"

        if ($install.ExitCode -eq 0 -or $install.ExitCode -eq 3010) {
            Remove-Item -LiteralPath $Msi -Force -ErrorAction SilentlyContinue
        }

        if (Test-Path -LiteralPath $Exe) {
            Write-Step "starting $Exe"
            Start-Process -FilePath $Exe
        }
        else {
            Write-Step "the app is not where it was expected: $Exe"
        }

        Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        """;
}
