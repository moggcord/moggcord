using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Drawing;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace MoggcordInstaller
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (HasFlag(args, "--verify-bundle"))
            {
                try
                {
                    var report = VerifyBundledBuild();
                    int output = Array.IndexOf(args, "--report");
                    if (output >= 0 && output + 1 < args.Length) File.WriteAllText(args[output + 1], report);
                    Console.WriteLine(report);
                    Environment.ExitCode = 0;
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error.Message);
                    Environment.ExitCode = 1;
                }
                return;
            }

            InstallerLog.Write($"Moggcord Installer {Assembly.GetExecutingAssembly().GetName().Version.ToString(3)} started on {RuntimeInformation.OSDescription}. Administrator: {InstallerLog.IsElevated}.");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (HasFlag(args, "--auto") || HasFlag(args, "-auto"))
            {
                Environment.ExitCode = RunAutoInstall(args).GetAwaiter().GetResult();
                return;
            }

            var readyArgument = Array.IndexOf(args, "--installer-update-ready");
            var readyEvent = readyArgument >= 0 && readyArgument + 1 < args.Length ? args[readyArgument + 1] : null;
            Application.Run(new LauncherForm(!HasFlag(args, "--skip-installer-update"), readyEvent));
        }

        internal static string VerifyBundledBuild()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version.ToString(3);
            using var resource = assembly.GetManifestResourceStream("MoggcordInstaller.moggcord-dist.zip");
            if (resource == null) throw new InvalidDataException("The installer contains no desktop archive.");
            using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
            var marker = archive.GetEntry("build-info.json");
            if (marker == null) throw new InvalidDataException("The desktop archive has no build information.");
            using var stream = marker.Open();
            using var manifest = JsonDocument.Parse(stream);
            if (manifest.RootElement.GetProperty("version").GetString() != version)
                throw new InvalidDataException("The installer and embedded desktop versions do not match.");
            var fingerprints = new Dictionary<string, string>();
            foreach (var file in new[] { "patcher.js", "preload.js", "renderer.js", "renderer.css", "community-service.cjs" })
            {
                var entry = archive.GetEntry(file);
                if (entry == null) throw new InvalidDataException($"Missing desktop file: {file}");
                using var contents = entry.Open();
                var fingerprint = Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant();
                if (fingerprint != manifest.RootElement.GetProperty("files").GetProperty(file).GetString())
                    throw new InvalidDataException($"The embedded desktop file is damaged: {file}");
                fingerprints.Add(file, fingerprint);
            }
            return JsonSerializer.Serialize(new { version, files = fingerprints });
        }

        static bool HasFlag(string[] args, string flag)
        {
            foreach (var arg in args)
                if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        static async Task<int> RunAutoInstall(string[] args)
        {
            var backend = new MoggcordBackend(null, null);
            try
            {
                Console.WriteLine("[Moggcord] Detecting Discord...");
                var json = await backend.DetectDiscord();
                var installs = JsonSerializer.Deserialize<List<DiscordInstall>>(json) ?? new List<DiscordInstall>();
                if (installs.Count == 0)
                {
                    Console.Error.WriteLine("[Moggcord] No Discord installation found.");
                    return 1;
                }

                var target = PickPreferredInstall(installs);
                Console.WriteLine($"[Moggcord] Target: {target.name} ({target.path})");

                if (await backend.IsInjected(target.path))
                {
                    Console.WriteLine("[Moggcord] Already injected. Starting Discord...");
                    backend.StartDiscordPublic(target.path);
                    return 0;
                }

                Console.WriteLine("[Moggcord] Downloading latest Moggcord and injecting...");
                var resultJson = await backend.Inject(target.path);
                using var doc = JsonDocument.Parse(resultJson);
                if (!doc.RootElement.TryGetProperty("success", out var okProp) || !okProp.GetBoolean())
                {
                    var err = doc.RootElement.TryGetProperty("error", out var errProp) ? errProp.GetString() : "Unknown error";
                    Console.Error.WriteLine("[Moggcord] Injection failed: " + err);
                    return 1;
                }

                Console.WriteLine("[Moggcord] Done.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[Moggcord] Auto install failed: " + ex.Message);
                return 1;
            }
        }

        static DiscordInstall PickPreferredInstall(List<DiscordInstall> installs)
        {
            var order = new[] { "Discord", "Discord PTB", "Discord Canary", "Discord Dev" };
            foreach (var name in order)
            {
                var match = installs.Find(i => string.Equals(i.name, name, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            return installs[0];
        }

        class DiscordInstall
        {
            public string name { get; set; }
            public string path { get; set; }
        }
    }

    public class LauncherForm : Form
    {
        private WebView2 _webView;
        private MoggcordBackend _backend;
        private readonly bool _checkInstallerUpdates;
        private readonly string _updateReadyEvent;

        public LauncherForm(bool checkInstallerUpdates = true, string updateReadyEvent = null)
        {
            _checkInstallerUpdates = checkInstallerUpdates;
            _updateReadyEvent = updateReadyEvent;
            this.Text = "Moggcord Installer";
            this.Size = new Size(960, 720); // Enlarged to prevent text clipping
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(9, 10, 16); // matching HTML root
            var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MoggcordInstaller.icon.ico");
            if (iconStream != null) this.Icon = new Icon(iconStream, 256, 256);

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.Transparent
            };
            this.Controls.Add(_webView);

            // Drag window from HTML
            _webView.NavigationCompleted += (s, e) => {
                RunOnUiThread(() => {
                    if (_webView.CoreWebView2 == null) return;
                    _webView.CoreWebView2.WebMessageReceived += (sender, args) => {
                        if (args.TryGetWebMessageAsString() == "drag") {
                            ReleaseCapture();
                            SendMessage(this.Handle, 0xA1, 0x2, 0);
                        }
                    };
                    if (e.IsSuccess && _updateReadyEvent?.StartsWith("Local\\MoggcordInstallerUpdate-", StringComparison.Ordinal) == true)
                    {
                        try
                        {
                            using var ready = EventWaitHandle.OpenExisting(_updateReadyEvent);
                            ready.Set();
                        }
                        catch (WaitHandleCannotBeOpenedException error) { Debug.WriteLine(error.Message); }
                    }
                });
            };

            _ = InitializeWebViewAsync();
        }

        private void RunOnUiThread(Action action)
        {
            if (InvokeRequired)
                BeginInvoke(action);
            else
                action();
        }

        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        private async Task InitializeWebViewAsync()
        {
            var userDataFolder = Path.Combine(Path.GetTempPath(), "MoggcordInstaller_WebView2");
            CoreWebView2Environment env;
            try
            {
                env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await RunOnUiThreadAsync(() => _webView.EnsureCoreWebView2Async(env));
            }
            catch (Exception ex) when (
                ex is System.Runtime.InteropServices.COMException ||
                ex.HResult == unchecked((int)0x80070002) ||
                ex.Message.Contains("WebView2") ||
                ex.Message.Contains("0x80070002")
            )
            {
                RunOnUiThread(() =>
                {
                    MessageBox.Show(
                        "Microsoft Edge WebView2 Runtime is required to run the Moggcord Installer but was not found on your system.\n\n" +
                        "Please download and install it from:\nhttps://aka.ms/webview2\n\n" +
                        "After installing, restart the Moggcord Installer.",
                        "WebView2 Runtime Required",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                    Application.Exit();
                });
                return;
            }

            await RunOnUiThreadAsync(CompleteWebViewSetupAsync);
        }

        private Task RunOnUiThreadAsync(Func<Task> action)
        {
            var tcs = new TaskCompletionSource<object>();
            RunOnUiThread(async () =>
            {
                try
                {
                    await action();
                    tcs.SetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task;
        }

        private async Task CompleteWebViewSetupAsync()
        {
            _backend = new MoggcordBackend(this, _webView, _checkInstallerUpdates);
            _webView.CoreWebView2.AddHostObjectToScript("backend", _backend);

            // Wrap COM proxy into exactly what index.html expects, and add drag support
            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                window.moggcord = {
                    detectDiscord: async () => JSON.parse(await chrome.webview.hostObjects.backend.DetectDiscord()),
                    isInjected: async (path) => await chrome.webview.hostObjects.backend.IsInjected(path),
                    hasThirdPartyMod: async (path) => await chrome.webview.hostObjects.backend.HasThirdPartyMod(path),
                    resolveDiscord: async (path) => JSON.parse(await chrome.webview.hostObjects.backend.ResolveDiscord(path)),
                    browseDiscord: async () => await chrome.webview.hostObjects.backend.BrowseDiscord(),
                    inject: async (path) => JSON.parse(await chrome.webview.hostObjects.backend.Inject(path)),
                    startInject: (path) => JSON.parse(chrome.webview.hostObjects.sync.backend.StartInject(path)),
                    startOperation: (action, path, replaceOtherMod) => JSON.parse(chrome.webview.hostObjects.sync.backend.StartOperation(action, path, replaceOtherMod)),
                    getInjectStatus: () => JSON.parse(chrome.webview.hostObjects.sync.backend.GetInjectStatus()),
                    getInstallerVersion: () => chrome.webview.hostObjects.sync.backend.GetInstallerVersion(),
                    startInstallerUpdate: () => chrome.webview.hostObjects.sync.backend.StartInstallerUpdate(),
                    getInstallerUpdateStatus: () => JSON.parse(chrome.webview.hostObjects.sync.backend.GetInstallerUpdateStatus()),
                    openLog: () => chrome.webview.hostObjects.backend.OpenLog(),
                    uninject: async (path) => JSON.parse(await chrome.webview.hostObjects.backend.Uninject(path)),
                    minimizeApp: () => chrome.webview.hostObjects.backend.MinimizeApp(),
                    closeApp: () => chrome.webview.hostObjects.backend.CloseApp(),
                    openUrl: (url) => chrome.webview.hostObjects.backend.OpenUrl(url)
                };

                // Add drag support to titlebar
                document.addEventListener('DOMContentLoaded', () => {
                    const titlebar = document.querySelector('.titlebar');
                    if (titlebar) {
                        titlebar.addEventListener('mousedown', (e) => {
                            if (!e.target.closest('button, a')) {
                                window.chrome.webview.postMessage('drag');
                            }
                        });
                    }
                });
            ");

            // Disable context menu and dev tools
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

            // Load HTML from embedded resource and inject Icon
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MoggcordInstaller.index.html"))
            using (var reader = new StreamReader(stream))
            {
                var html = reader.ReadToEnd();
                using (var font = Assembly.GetExecutingAssembly().GetManifestResourceStream("MoggcordInstaller.fonts.GeistVariable.woff2"))
                using (var bytes = new MemoryStream())
                {
                    font.CopyTo(bytes);
                    html = html.Replace("{{FONT_BASE64}}", Convert.ToBase64String(bytes.ToArray()));
                }

                // Convert Icon to Base64 PNG for HTML
                foreach (var (file, placeholder, prefix) in new[] {
                    ("moggcord-pfp.png", "{{ICON_BASE64}}", "data:image/png;base64,"),
                    ("discord-icon.png", "{{DISCORD_BASE64}}", ""),
                    ("discord-canary-icon.png", "{{CANARY_BASE64}}", "")
                })
                {
                    using var image = Assembly.GetExecutingAssembly().GetManifestResourceStream("MoggcordInstaller." + file);
                    if (image == null) throw new InvalidDataException("The installer logo is missing.");
                    using var bytes = new MemoryStream();
                    image.CopyTo(bytes);
                    html = html.Replace(placeholder, prefix + Convert.ToBase64String(bytes.ToArray()));
                }

                _webView.NavigateToString(html);
            }
        }
    }

    [ComVisible(true)]
    public class MoggcordBackend
    {
        private LauncherForm _form;
        private WebView2 _webView;
        private HttpClient _http;
        private string _distDir;
        private string _exeDir;
        private readonly bool _checkInstallerUpdates;
        private readonly CancellationTokenSource _installerUpdateCancellation = new CancellationTokenSource();
        private volatile InstallerUpdateStatus _installerUpdateStatus = new InstallerUpdateStatus("checking", "Checking for a newer installer…");
        private int _installerUpdateStarted;

        const string GITHUB_REPO = InstallerSelfUpdater.Repository;
        const string DIST_ZIP = "moggcord-dist.zip";

        public MoggcordBackend(LauncherForm form, WebView2 webView, bool checkInstallerUpdates = false)
        {
            _form = form;
            _webView = webView;
            _checkInstallerUpdates = checkInstallerUpdates;
            if (form != null) form.FormClosed += (_, _) => _installerUpdateCancellation.Cancel();
            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(30);
            _exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            _distDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Moggcord", "dist");
        }

        public void StartDiscordPublic(string resPath) => StartDiscord(resPath);

        public string GetInstallerVersion() => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        public bool StartInstallerUpdate()
        {
            if (!_checkInstallerUpdates || Interlocked.CompareExchange(ref _installerUpdateStarted, 1, 0) != 0) return false;
            _ = Task.Run(async () =>
            {
                using var http = new HttpClient();
                var updater = new InstallerSelfUpdater(http, Version.Parse(GetInstallerVersion()),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoggcordInstaller", "updates"),
                    status => _installerUpdateStatus = status, StartUpdatedInstaller);
                if (await updater.TryUpdateAsync(_installerUpdateCancellation.Token) && !_form.IsDisposed)
                    _form.BeginInvoke(new Action(() => _form.Close()));
            });
            return true;
        }

        public string GetInstallerUpdateStatus() => JsonSerializer.Serialize(_installerUpdateStatus);

        private bool StartUpdatedInstaller(string path)
        {
            var token = _installerUpdateCancellation.Token;
            token.ThrowIfCancellationRequested();
            var eventName = "Local\\MoggcordInstallerUpdate-" + Guid.NewGuid().ToString("N");
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
            var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(path) };
            start.ArgumentList.Add("--skip-installer-update");
            start.ArgumentList.Add("--installer-update-ready");
            start.ArgumentList.Add(eventName);
            start.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
            using var process = Process.Start(start);
            if (process == null) return false;
            if (WaitHandle.WaitAny(new WaitHandle[] { ready, token.WaitHandle }, TimeSpan.FromSeconds(20)) == 0 && !process.HasExited)
                return true;
            if (!process.HasExited) process.Kill();
            return false;
        }

        public void MinimizeApp()
        {
            if (_form == null) return;
            _form.Invoke(new Action(() => _form.WindowState = FormWindowState.Minimized));
        }

        public void CloseApp()
        {
            if (_form == null) { Environment.Exit(0); return; }
            _form.Invoke(new Action(() => Application.Exit()));
        }
        public void OpenUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        public void OpenLog()
        {
            var target = File.Exists(InstallerLog.FilePath) ? InstallerLog.FilePath : Path.GetDirectoryName(InstallerLog.FilePath);
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException)
            {
                InstallerLog.Write("The installer log could not be opened: " + error.Message);
            }
        }

        public void SetStatus(string type, string text)
        {
            if (_form == null || _webView == null)
            {
                Console.WriteLine("[Moggcord] " + text);
                return;
            }

            RunOnUiThread(() =>
            {
                if (_webView.CoreWebView2 == null) return;
                _ = _webView.CoreWebView2.ExecuteScriptAsync($"if(typeof setStatus === 'function') setStatus({JsonSerializer.Serialize(type)}, {JsonSerializer.Serialize(text)});");
            });
        }

        public void SetProgress(double percent, string text, double mbDownloaded = -1, double mbTotal = -1)
        {
            if (_form == null || _webView == null)
            {
                Console.WriteLine($"[Moggcord] {text} ({percent:F0}%)");
                return;
            }

            RunOnUiThread(() =>
            {
                if (_webView.CoreWebView2 == null) return;
                var safeText = text.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", " ");
                var percentStr = percent.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                if (mbDownloaded >= 0 && mbTotal >= 0)
                {
                    var mbDlStr = mbDownloaded.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                    var mbTotalStr = mbTotal.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                    _ = _webView.CoreWebView2.ExecuteScriptAsync($"if(typeof setLoading === 'function') setLoading(true, '{safeText}', {percentStr}, {mbDlStr}, {mbTotalStr});");
                }
                else
                {
                    _ = _webView.CoreWebView2.ExecuteScriptAsync($"if(typeof setLoading === 'function') setLoading(true, '{safeText}', {percentStr});");
                }
            });
        }

        private void RunOnUiThread(Action action)
        {
            if (_form == null) return;
            if (_form.InvokeRequired)
                _form.BeginInvoke(action);
            else
                action();
        }

        public async Task<bool> IsInjected(string path)
        {
            return await Task.Run(() => IsMoggcordPackage(Path.Combine(path, "app", "package.json")));
        }

        private static bool IsMoggcordPackage(string packagePath)
        {
            if (!File.Exists(packagePath)) return false;
            try
            {
                using var package = JsonDocument.Parse(File.ReadAllText(packagePath));
                return package.RootElement.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && string.Equals(name.GetString(), "moggcord", StringComparison.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// Returns true if a third-party mod (Vencord, Equicord, OpenAsar) is detected
        /// but Moggcord is NOT yet injected.
        /// </summary>
        public async Task<bool> HasThirdPartyMod(string path)
        {
            // Already Moggcord → not a third-party mod
            if (await IsInjected(path)) return false;
            return await Task.Run(() => IsThirdPartyMod(path));
        }

        private static bool IsThirdPartyMod(string path)
        {
            var pkgPath = Path.Combine(path, "app", "package.json");
            var content = File.Exists(pkgPath) ? File.ReadAllText(pkgPath) : "";
            var appAsar = Path.Combine(path, "app.asar");
            if (File.Exists(appAsar) && new FileInfo(appAsar).Length < 2_000_000)
                content += File.ReadAllText(appAsar);
            return content.Contains("vencord", StringComparison.OrdinalIgnoreCase)
                || content.Contains("equicord", StringComparison.OrdinalIgnoreCase)
                || content.Contains("openasar", StringComparison.OrdinalIgnoreCase);
        }

        private static readonly string[] DiscordExecutables = { "Discord.exe", "DiscordPTB.exe", "DiscordCanary.exe", "DiscordDevelopment.exe" };

        private sealed class DiscordInstallation
        {
            public string name { get; set; }
            public string path { get; set; }
            public string exePath { get; set; }
            public string version { get; set; }
            public string icon { get; set; }
        }

        private static string FindDiscordExecutable(string directory) =>
            DiscordExecutables.Select(name => Path.Combine(directory, name)).FirstOrDefault(File.Exists);

        private static DiscordInstallation ResolveInstallation(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) throw new IOException("Enter a Discord folder or the path to Discord.exe.");
            var selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input.Trim().Trim('"')));
            var selectedFile = File.Exists(selected);
            if (selectedFile && !DiscordExecutables.Contains(Path.GetFileName(selected), StringComparer.OrdinalIgnoreCase))
                throw new IOException("Choose a Discord executable or its installation folder.");
            if (selectedFile)
                selected = Path.Combine(Path.GetDirectoryName(selected), DiscordExecutables.First(name => string.Equals(name, Path.GetFileName(selected), StringComparison.OrdinalIgnoreCase)));
            var directory = selectedFile ? Path.GetDirectoryName(selected) : selected;
            if (!Directory.Exists(directory)) throw new IOException("That folder does not exist. Check the path and try again.");
            if (string.Equals(Path.GetFileName(directory), "resources", StringComparison.OrdinalIgnoreCase))
                directory = Path.GetDirectoryName(directory);
            var candidates = new[] { directory }.Concat(Directory.GetDirectories(directory, "app-*")
                .OrderByDescending(path => Version.TryParse(Path.GetFileName(path).Substring(4), out var version) ? version : new Version(0, 0)));
            foreach (var appDirectory in candidates)
            {
                var executable = selectedFile ? selected : FindDiscordExecutable(appDirectory);
                var resources = Path.Combine(appDirectory, "resources");
                if (executable == null || !string.Equals(Path.GetDirectoryName(executable), appDirectory, StringComparison.OrdinalIgnoreCase)
                    || !Directory.Exists(resources)
                    || (!File.Exists(Path.Combine(resources, "app.asar")) && !File.Exists(Path.Combine(resources, "_app.asar"))))
                    continue;
                var executableName = Path.GetFileNameWithoutExtension(executable);
                var name = executableName switch { "DiscordPTB" => "Discord PTB", "DiscordCanary" => "Discord Canary", "DiscordDevelopment" => "Discord Dev", _ => "Discord" };
                var folder = Path.GetFileName(appDirectory);
                string icon = null;
                try
                {
                    using var associatedIcon = Icon.ExtractAssociatedIcon(executable);
                    if (associatedIcon != null)
                    {
                        using var bitmap = associatedIcon.ToBitmap();
                        using var bytes = new MemoryStream();
                        bitmap.Save(bytes, System.Drawing.Imaging.ImageFormat.Png);
                        icon = "data:image/png;base64," + Convert.ToBase64String(bytes.ToArray());
                    }
                }
                catch (Exception error)
                {
                    Console.WriteLine("[Moggcord] Could not read the Discord icon: " + error.Message);
                }
                return new DiscordInstallation { name = name, path = resources, exePath = executable, version = folder.StartsWith("app-") ? folder.Substring(4) : "Custom", icon = icon };
            }
            throw new IOException("No Discord installation was found there. Choose the Discord folder, an app folder, its resources folder or Discord.exe.");
        }

        public async Task<string> ResolveDiscord(string input)
        {
            try
            {
                var target = await Task.Run(() => ResolveInstallation(input));
                return JsonSerializer.Serialize(new { success = true, target });
            }
            catch (Exception error)
            {
                return JsonSerializer.Serialize(new { success = false, error = error.Message });
            }
        }

        public string BrowseDiscord()
        {
            if (_form == null) return "";
            return (string)_form.Invoke(new Func<string>(() =>
            {
                using var picker = new FolderBrowserDialog { Description = "Choose your Discord installation folder", UseDescriptionForTitle = true };
                return picker.ShowDialog(_form) == DialogResult.OK ? picker.SelectedPath : "";
            }));
        }

        public async Task<string> DetectDiscord()
        {
            return await Task.Run(() =>
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var list = new List<DiscordInstallation>();
                foreach (var channel in new[] { "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment" })
                {
                    var directory = Path.Combine(localAppData, channel);
                    if (!Directory.Exists(directory)) continue;
                    try { list.Add(ResolveInstallation(directory)); }
                    catch (IOException error) { Console.WriteLine("[Moggcord] " + error.Message); }
                }
                return JsonSerializer.Serialize(list);
            });
        }

        // Job state for async inject (avoids WebView2 COM proxy timeout on large downloads)
        private volatile string _injectJobState = "idle"; // idle | running | done | error
        private string _injectJobError = "";
        private string _injectJobResult = "";
        private volatile string _injectJobWarning = "";
        private readonly object _operationLock = new object();

        public string StartInject(string resourcesPath) => StartOperation("install", resourcesPath, false);

        public string StartOperation(string action, string resourcesPath, bool replaceOtherMod)
        {
            lock (_operationLock)
            {
                if (_installerUpdateStarted != 0 && _installerUpdateStatus.state != "current" && _installerUpdateStatus.state != "unavailable")
                    return JsonSerializer.Serialize(new { started = false, error = "Please wait for the installer update to finish." });
                if (_injectJobState == "running")
                    return JsonSerializer.Serialize(new { started = false, error = "An operation is already running." });

                _injectJobState = "running";
                _injectJobError = "";
                _injectJobResult = "";
                _injectJobWarning = "";
                Task.Run(async () =>
                {
                    try
                    {
                        _injectJobResult = await RunOperationAsync(action, resourcesPath, replaceOtherMod);
                        _injectJobState = "done";
                    }
                    catch (Exception ex)
                    {
                        InstallerLog.Write($"{action} failed: {ex}");
                        _injectJobError = ex.Message;
                        _injectJobState = "error";
                    }
                });
                return JsonSerializer.Serialize(new { started = true });
            }
        }

        public string GetInjectStatus()
        {
            var state = _injectJobState;
            return JsonSerializer.Serialize(new
            {
                state,
                error = _injectJobError,
                result = _injectJobResult,
                warning = _injectJobWarning
            });
        }

        // Keep old Inject for compat but it just delegates
        public Task<string> Inject(string resourcesPath) => RunOperationResultAsync("install", resourcesPath);

        public Task<string> Uninject(string resourcesPath) => RunOperationResultAsync("uninstall", resourcesPath);

        private async Task<string> RunOperationResultAsync(string action, string resourcesPath)
        {
            try
            {
                var result = await RunOperationAsync(action, resourcesPath, false);
                return JsonSerializer.Serialize(new { success = true, result });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }

        private async Task<string> RunOperationAsync(string action, string resourcesPath, bool replaceOtherMod)
        {
            if (action != "install" && action != "repair" && action != "uninstall")
                throw new InvalidOperationException("Choose Install, Repair or Uninstall.");
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(resourcesPath));
            if (!Directory.Exists(path) || !string.Equals(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), "resources", StringComparison.OrdinalIgnoreCase)
                || (!File.Exists(Path.Combine(path, "app.asar")) && !File.Exists(Path.Combine(path, "_app.asar"))))
                throw new InvalidOperationException("Choose the resources folder of a Discord installation.");

            var installed = await IsInjected(path);
            if (action == "install" && installed) return "alreadyInstalled";
            if (action != "install" && !installed)
                throw new InvalidOperationException("Moggcord is not installed here. Choose another Discord installation or use Install.");
            if (action == "install" && !replaceOtherMod && await HasThirdPartyMod(path))
                throw new InvalidOperationException("Another mod is installed here. Confirm that you want to replace it first.");
            if (FindDiscordExecutable(Path.GetDirectoryName(path)) == null)
                throw new InvalidOperationException("The Discord executable is missing next to this resources folder. Check the selected path.");

            InstallerLog.Write($"{action} started for {path}");
            if (action == "uninstall")
            {
                await Task.Run(() => RunWithDiscordClosed(path, false, () => DoUninject(path)));
                return "uninstalled";
            }

            await EnsureDistAsync();
            if (action == "install" && await IsInjected(path)) return "alreadyInstalled";
            await Task.Run(() => RunWithDiscordClosed(path, true, () => DoInject(path)));
            InstallerLog.Write($"{action} finished for {path}");
            return action == "repair" ? "repaired" : "installed";
        }

        private void RunWithDiscordClosed(string resPath, bool installing, Action change)
        {
            SetProgress(90, "Closing Discord...");
            KillDiscord(resPath);
            Exception failure = null;
            try
            {
                change();
            }
            catch (Exception error)
            {
                failure = error;
                InstallerLog.Write("The change failed, restoring a startable Discord: " + error);
                RestoreStartableDiscord(resPath);
            }

            SetProgress(97, "Starting Discord...");
            var running = StartDiscord(resPath);
            if (failure == null && installing && running != null && !IsMoggcordPackage(Path.Combine(running, "app", "package.json")))
            {
                InstallerLog.Write("Discord started from a version without Moggcord: " + running);
                SetProgress(98, "Adding Moggcord to the Discord version that started...");
                KillDiscord(resPath);
                try
                {
                    InjectInto(running);
                }
                catch (Exception error)
                {
                    failure = error;
                    InstallerLog.Write("Installing into the started version failed: " + error);
                    RestoreStartableDiscord(running);
                }
                running = StartDiscord(resPath);
            }

            if (running == null)
                _injectJobWarning = "Discord didn’t open by itself. Start it from the Start menu or the taskbar.";
            if (failure != null)
                throw new IOException(failure.Message + (running != null ? " Discord was restored and started again." : " Discord was restored."), failure);
            SetProgress(100, "Done");
        }

        private async Task EnsureDistAsync()
        {
            // Optional dev override: dist/ folder next to the installer exe
            var localDist = Path.Combine(_exeDir, "dist");
            if (Directory.Exists(localDist) && File.Exists(Path.Combine(localDist, "patcher.js")))
            {
                _distDir = localDist;
                SetStatus("loading", "Using local development files...");
                return;
            }

            // Always prefer the latest GitHub release when online. The embedded zip
            // is only a fallback for offline installs and may be older than latest.
            try
            {
                if (await TryDownloadLatestDistAsync())
                    return;
            }
            catch (Exception ex)
            {
                InstallerLog.Write("Online download failed: " + ex);
                SetStatus("loading", $"Online download failed ({ex.Message}). Trying bundled files...");
            }

            InstallerLog.Write("Using the Moggcord files bundled with this installer.");
            if (await TryUseBundledDistAsync())
                return;

            throw new Exception("Could not obtain Moggcord files. Check your internet connection or download a newer installer.");
        }

        private async Task<bool> TryDownloadLatestDistAsync()
        {
            SetProgress(2, "Fetching latest release information...");
            Directory.CreateDirectory(Path.GetDirectoryName(_distDir));

            var apiUrl = $"https://api.github.com/repos/{GITHUB_REPO}/releases/latest";
            _http.DefaultRequestHeaders.Clear();
            _http.DefaultRequestHeaders.Add("User-Agent", "Moggcord-Installer/2.0");
            _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");

            string json;
            try
            {
                json = await _http.GetStringAsync(apiUrl);
            }
            catch (TaskCanceledException)
            {
                throw new Exception("GitHub API timed out (30s). Check your internet connection and try again.");
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"Could not reach GitHub: {ex.Message}. Check your internet connection.");
            }

            var zipUrl = ExtractJsonValue(json, "browser_download_url", DIST_ZIP);
            var releaseTag = ExtractJsonValue(json, "tag_name");

            if (Version.TryParse(releaseTag?.TrimStart('v'), out var releaseVersion)
                && releaseVersion.CompareTo(Version.Parse(GetInstallerVersion())) < 0)
            {
                SetStatus("loading", "This installer includes newer Moggcord files. Loading bundled files...");
                return false;
            }

            if (string.IsNullOrEmpty(zipUrl) && Regex.IsMatch(releaseTag ?? "", @"^v?\d+\.\d+\.\d+$"))
            {
                var updateTag = "updates-v" + releaseTag.TrimStart('v');
                var updatesJson = await _http.GetStringAsync($"https://api.github.com/repos/{GITHUB_REPO}/releases/tags/{updateTag}");
                if (ExtractJsonValue(updatesJson, "tag_name") != updateTag)
                    throw new Exception("The update files do not match the selected release.");
                zipUrl = ExtractJsonValue(updatesJson, "browser_download_url", DIST_ZIP);
            }

            if (string.IsNullOrEmpty(zipUrl))
                throw new Exception($"'{DIST_ZIP}' not found in the GitHub release. The release may not be published yet.");

            InstallerLog.Write($"Downloading Moggcord {releaseTag} from {zipUrl}");
            SetProgress(5, $"Downloading Moggcord {releaseTag ?? "latest"}...");
            var tmpZip = Path.Combine(Path.GetTempPath(), "moggcord-dist.zip");

            using (var response = await _http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? (long)(348.0 * 1024 * 1024);

                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fs = new FileStream(tmpZip, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    long totalRead = 0;
                    int read;
                    double lastReportedPercent = 0;

                    while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, read);
                        totalRead += read;

                        double percent = (double)totalRead / totalBytes * 100.0;
                        if (percent - lastReportedPercent >= 0.5 || percent >= 100.0)
                        {
                            lastReportedPercent = percent;
                            double overallPercent = 5.0 + (percent * 0.70);
                            double totalMB = (double)totalBytes / (1024.0 * 1024.0);
                            double readMB = (double)totalRead / (1024.0 * 1024.0);
                            SetProgress(overallPercent, "Downloading Moggcord...", readMB, totalMB);
                        }
                    }
                }
            }

            SetProgress(75, "Preparing extraction...");
            await Task.Run(() => ExtractDistZip(tmpZip));
            try { File.Delete(tmpZip); } catch { }

            if (!string.IsNullOrEmpty(releaseTag))
            {
                try
                {
                    File.WriteAllText(Path.Combine(_distDir, "version.txt"), releaseTag.Trim());
                }
                catch { }
            }

            return File.Exists(Path.Combine(_distDir, "patcher.js"));
        }

        private async Task<bool> TryUseBundledDistAsync()
        {
            using (var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MoggcordInstaller.moggcord-dist.zip"))
            {
                if (resourceStream == null) return false;

                Program.VerifyBundledBuild();

                SetProgress(2, "Loading bundled Moggcord files...");
                Directory.CreateDirectory(Path.GetDirectoryName(_distDir));

                var tmpZip = Path.Combine(Path.GetTempPath(), "moggcord-dist-bundled.zip");
                using (var fs = new FileStream(tmpZip, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await resourceStream.CopyToAsync(fs);
                }

                SetProgress(75, "Preparing bundled files...");
                await Task.Run(() => ExtractDistZip(tmpZip));
                try { File.Delete(tmpZip); } catch { }

                if (!File.Exists(Path.Combine(_distDir, "patcher.js")))
                    throw new Exception("Bundled Moggcord files are incomplete. Rebuild the installer package and try again.");

                return true;
            }
        }

        private void ExtractDistZip(string zipPath)
        {
            var staging = _distDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".new";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            ExtractInto(zipPath, staging);
            if (!File.Exists(Path.Combine(staging, "patcher.js")))
            {
                Directory.Delete(staging, true);
                throw new InvalidDataException("The Moggcord package is incomplete.");
            }
            ReplaceDist(staging);
        }

        private static readonly string[] CriticalDistFiles = { "patcher.js", "preload.js", "renderer.js", "renderer.css", "package.json", "build-info.json" };

        private void ReplaceDist(string staging)
        {
            if (!Directory.Exists(_distDir))
            {
                Directory.Move(staging, _distDir);
                return;
            }
            var previous = _distDir + ".old-" + DateTime.UtcNow.Ticks;
            try
            {
                Directory.Move(_distDir, previous);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                InstallerLog.Write("The Moggcord folder is in use, updating its files in place: " + error.Message);
                CopyOver(staging, _distDir, true);
                Directory.Delete(staging, true);
                return;
            }
            try
            {
                Directory.Move(staging, _distDir);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Directory.Move(previous, _distDir);
                throw new IOException("The new Moggcord files could not be put in place. " + error.Message, error);
            }
            foreach (var old in Directory.GetDirectories(Path.GetDirectoryName(_distDir), Path.GetFileName(_distDir) + ".old-*"))
            {
                try
                {
                    Directory.Delete(old, true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    InstallerLog.Write("An old Moggcord folder is still in use and was kept: " + old);
                }
            }
        }

        private static void CopyOver(string source, string destination, bool topLevel)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
            {
                var name = Path.GetFileName(file);
                try
                {
                    File.Copy(file, Path.Combine(destination, name), true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    if (topLevel && CriticalDistFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
                        throw new IOException($"{name} is in use by another program. Close Discord completely and try again.", error);
                    InstallerLog.Write($"Skipped {file}: {error.Message}");
                }
            }
            foreach (var directory in Directory.GetDirectories(source))
                CopyOver(directory, Path.Combine(destination, Path.GetFileName(directory)), false);
        }

        private void ExtractInto(string zipPath, string destination)
        {
            // Normalize the destination with a guaranteed trailing separator for safe StartsWith checks
            var normalizedDistDir = destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                    + Path.DirectorySeparatorChar;

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                int totalEntries = archive.Entries.Count;
                int extractedEntries = 0;
                double lastReportedPercent = 0;

                foreach (var entry in archive.Entries)
                {
                    // Normalize the zip entry path: replace forward slashes, strip leading slashes
                    var entryPath = entry.FullName
                        .Replace('/', Path.DirectorySeparatorChar)
                        .TrimStart(Path.DirectorySeparatorChar);

                    // Reject any entry that tries to traverse upward (e.g. ../../evil)
                    if (entryPath.Contains(".."))
                        continue;

                    var fullPath = Path.GetFullPath(Path.Combine(destination, entryPath));

                    // Security check: ensure resolved path stays inside _distDir
                    if (!fullPath.StartsWith(normalizedDistDir, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                    {
                        Directory.CreateDirectory(fullPath);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                        entry.ExtractToFile(fullPath, true);
                    }

                    extractedEntries++;
                    double percent = (double)extractedEntries / totalEntries * 100.0;
                    if (percent - lastReportedPercent >= 1.0 || percent >= 100.0)
                    {
                        lastReportedPercent = percent;
                        // Map 0-100% of extraction to 75%-90% of overall progress
                        double overallPercent = 75.0 + (percent * 0.15);
                        SetProgress(overallPercent, $"Extracting files ({extractedEntries}/{totalEntries})...");
                    }
                }
            }
        }

        private void DoInject(string resPath)
        {
            InjectInto(resPath);
            foreach (var sibling in SiblingResources(resPath))
            {
                if (!IsMoggcordPackage(Path.Combine(sibling, "app", "package.json")) && IsThirdPartyMod(sibling))
                {
                    InstallerLog.Write("Skipped another Discord version with a different mod: " + sibling);
                    continue;
                }
                try
                {
                    InjectInto(sibling);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    InstallerLog.Write($"Could not add Moggcord to {sibling}: {error.Message}");
                    RestoreStartableDiscord(sibling);
                }
            }
            InstallDefaultSettings();
            MarkPostInstallRelaunch();
        }

        private static IEnumerable<string> SiblingResources(string resPath)
        {
            var appDirectory = Path.GetDirectoryName(Path.GetFullPath(resPath));
            var root = GetChannelRoot(appDirectory);
            if (root == appDirectory) return Array.Empty<string>();
            return Directory.GetDirectories(root, "app-*")
                .Where(directory => !string.Equals(directory, appDirectory, StringComparison.OrdinalIgnoreCase) && FindDiscordExecutable(directory) != null)
                .Select(directory => Path.Combine(directory, "resources"))
                .Where(resources => File.Exists(Path.Combine(resources, "app.asar")) || File.Exists(Path.Combine(resources, "_app.asar")))
                .ToList();
        }

        private static bool IsStockArchive(string path) => File.Exists(path) && new FileInfo(path).Length >= 1_000_000;

        private void RestoreStartableDiscord(string resPath)
        {
            var appDir = Path.Combine(resPath, "app");
            var backup = Path.Combine(resPath, "_app.asar");
            var appAsar = Path.Combine(resPath, "app.asar");
            if (File.Exists(Path.Combine(appDir, "index.js")) && IsMoggcordPackage(Path.Combine(appDir, "package.json")) && IsStockArchive(backup)) return;
            try
            {
                if (Directory.Exists(appDir)) Directory.Delete(appDir, true);
                if (!IsStockArchive(appAsar) && IsStockArchive(backup))
                {
                    if (File.Exists(appAsar)) File.Delete(appAsar);
                    File.Move(backup, appAsar);
                }
                InstallerLog.Write("Restored Discord's original files in " + resPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                InstallerLog.Write($"Could not restore {resPath}: {error.Message}");
            }
        }

        private void InjectInto(string resPath)
        {
            var appDir = Path.Combine(resPath, "app");
            var backup = Path.Combine(resPath, "_app.asar");
            var appAsar = Path.Combine(resPath, "app.asar");
            InstallerLog.Write("Adding Moggcord to " + resPath);

            SetProgress(91, "Removing previous mod injection (Vencord / Equicord / OpenAsar)...");
            if (Directory.Exists(appDir)) Directory.Delete(appDir, true);

            if (File.Exists(appAsar) && new FileInfo(appAsar).Length < 2_000_000)
            {
                File.Delete(appAsar);
            }

            string[] thirdPartyBackups = { "_app.asar", "original_app.asar", "app.asar.bak" };
            foreach (var bkName in thirdPartyBackups)
            {
                var bkPath = Path.Combine(resPath, bkName);
                if (File.Exists(bkPath) && new FileInfo(bkPath).Length > 2_000_000)
                {
                    if (!File.Exists(appAsar) || new FileInfo(appAsar).Length < 2_000_000)
                    {
                        if (File.Exists(appAsar)) File.Delete(appAsar);
                        File.Copy(bkPath, appAsar);
                    }
                    break;
                }
            }

            CleanModulePatches(resPath);

            SetProgress(92, "Configuring Moggcord loader...");

            if (!File.Exists(appAsar) && !File.Exists(backup))
            {
                throw new Exception(
                    "Critical error: no valid app.asar found. " +
                    "Please reinstall Discord from discord.com/download and try again."
                );
            }

            if (File.Exists(appAsar))
            {
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(appAsar, backup);
            }

            SetProgress(94, "Writing the Moggcord loader...");
            Directory.CreateDirectory(appDir);
            WriteLoader(appDir);
            WriteLoaderArchive(appAsar);
            CopyAssetsToDiscord(resPath);

            var missing = new List<string>();
            if (!File.Exists(Path.Combine(appDir, "index.js")) || !IsMoggcordPackage(Path.Combine(appDir, "package.json"))) missing.Add("the Moggcord loader");
            if (!IsStockArchive(backup)) missing.Add("Discord's original app.asar");
            if (!File.Exists(Path.Combine(_distDir, "patcher.js"))) missing.Add("the Moggcord files");
            if (missing.Count > 0) throw new IOException("Setup could not verify " + string.Join(" and ", missing) + ".");
            InstallerLog.Write("Moggcord is in place in " + resPath);
        }

        /// <summary>Removes Vencord/Equicord patches from Discord native modules.</summary>
        private void CleanModulePatches(string resPath)
        {
            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var appBase = Path.GetDirectoryName(resPath);

                string[] modulesSearchPaths = {
                    Path.Combine(appBase, "modules"),
                    Path.Combine(resPath, "modules")
                };

                foreach (var modulesDir in modulesSearchPaths)
                {
                    if (!Directory.Exists(modulesDir)) continue;

                    foreach (var coreParent in Directory.GetDirectories(modulesDir, "discord_desktop_core*"))
                    {
                        var corePath = Path.Combine(coreParent, "discord_desktop_core");
                        if (!Directory.Exists(corePath)) continue;

                        string[] patchedFiles = {
                            Path.Combine(corePath, "index.js"),
                            Path.Combine(corePath, "app", "app_bootstrap", "splashScreen.js"),
                            Path.Combine(corePath, "app", "app_bootstrap", "index.js"),
                        };

                        foreach (var pf in patchedFiles)
                        {
                            if (!File.Exists(pf)) continue;
                            var content = File.ReadAllText(pf);

                            bool isPatched = content.Contains("vencord", StringComparison.OrdinalIgnoreCase)
                                         || content.Contains("equicord", StringComparison.OrdinalIgnoreCase)
                                         || content.Contains("require(\"vencord")
                                         || content.Contains("require('vencord")
                                         || content.Contains("VencordNative")
                                         || content.Contains("equilotl");

                            if (!isPatched) continue;

                            string[] backupExts = { ".orig", ".bak", ".vanilla" };
                            bool restored = false;
                            foreach (var ext in backupExts)
                            {
                                var bk = pf + ext;
                                if (File.Exists(bk))
                                {
                                    File.Copy(bk, pf, true);
                                    File.Delete(bk);
                                    restored = true;
                                    break;
                                }
                            }

                            if (!restored && pf == patchedFiles[0] && File.Exists(Path.Combine(corePath, "core.asar")))
                                File.WriteAllText(pf, "module.exports = require('./core.asar');");
                            else if (!restored)
                                InstallerLog.Write("Left a modified Discord file unchanged: " + pf);
                        }

                        var innerAppDir = Path.Combine(corePath, "app");
                        if (Directory.Exists(innerAppDir))
                        {
                            var innerPkg = Path.Combine(innerAppDir, "package.json");
                            if (File.Exists(innerPkg))
                            {
                                var pkgContent = File.ReadAllText(innerPkg);
                                bool isModInjection = pkgContent.Contains("vencord", StringComparison.OrdinalIgnoreCase)
                                                   || pkgContent.Contains("equicord", StringComparison.OrdinalIgnoreCase)
                                                   || pkgContent.Contains("openasar", StringComparison.OrdinalIgnoreCase);
                                if (isModInjection)
                                {
                                    try { Directory.Delete(innerAppDir, true); } catch { }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Moggcord] CleanModulePatches warning: {ex.Message}");
            }
        }

        private void DoUninject(string resPath)
        {
            UninjectFrom(resPath);
            foreach (var sibling in SiblingResources(resPath).Where(path => IsMoggcordPackage(Path.Combine(path, "app", "package.json"))))
            {
                try
                {
                    UninjectFrom(sibling);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    InstallerLog.Write($"Could not remove Moggcord from {sibling}: {error.Message}");
                    RestoreStartableDiscord(sibling);
                }
            }
        }

        private void UninjectFrom(string resPath)
        {
            var appDir = Path.Combine(resPath, "app");
            var backup = Path.Combine(resPath, "_app.asar");
            var appAsar = Path.Combine(resPath, "app.asar");
            InstallerLog.Write("Removing Moggcord from " + resPath);

            SetProgress(92, "Removing injected folder...");
            // 1. Remove the injected 'app' folder
            if (Directory.Exists(appDir))
            {
                var pkg = Path.Combine(appDir, "package.json");
                if (IsMoggcordPackage(pkg))
                {
                    Directory.Delete(appDir, true);
                }
            }

            SetProgress(94, "Restoring original files...");
            // Nettoyage des faux app.asar avant de restaurer
            if (File.Exists(appAsar) && new FileInfo(appAsar).Length < 1000000) {
                File.Delete(appAsar);
            }

            // 2. Restore app.asar backup
            if (File.Exists(backup))
            {
                if (!File.Exists(appAsar)) {
                    File.Move(backup, appAsar);
                } else {
                    // S'il y a un vrai app.asar (taille > 1Mo), Discord s'est mis à jour, l'ancien backup est obsolète
                    File.Delete(backup); 
                }
            }

            SetProgress(95, "Cleaning up assets...");
            // 3. Clean up Moggcord-specific assets (folders we added)
            // We DON'T delete ffmpeg.dll because it's a native Discord file!
            var appBase = Path.GetDirectoryName(resPath);
            
            // Revert build_info.json patch
            var buildInfoPath = Path.Combine(resPath, "build_info.json");
            if (File.Exists(buildInfoPath)) {
                try {
                    var json = File.ReadAllText(buildInfoPath);
                    if (json.Contains("\"localModulesRoot\"")) {
                        // Simple regex to remove the line
                        json = Regex.Replace(json, @",\s*""localModulesRoot""\s*:\s*""modules""\s*", "");
                        File.WriteAllText(buildInfoPath, json);
                    }
                } catch { }
            }

            string[] filesToClean = { "node.exe", "yt-dlp.exe", "ffmpeg.exe" }; // safe to delete as Discord doesn't have these
            foreach (var f in filesToClean) { 
                var p = Path.Combine(appBase, f); 
                if (File.Exists(p)) try { File.Delete(p); } catch { } 
            }

            string[] dirsToClean = { "mac", "multi-instance-icons", "ghost-server" };
            foreach (var dir in dirsToClean) { 
                var p = Path.Combine(appBase, dir); 
                if (Directory.Exists(p)) try { Directory.Delete(p, true); } catch { }
            }
        }

        private string LoaderScript()
        {
            var candidates = new[] { Path.Combine(_distDir, "patcher.js"), Path.Combine(_distDir, "desktop", "patcher.js") }
                .Select(path => "    " + JsonEscape(path.Replace("\\", "/")) + ",\n");
            return "// Moggcord Injector\n" +
                "\"use strict\";\n" +
                "const fs = require(\"fs\");\n" +
                "const path = require(\"path\");\n" +
                "const candidates = [\n" + string.Concat(candidates) +
                "    path.join(path.dirname(process.execPath), \"resources\", \"dist\", \"patcher.js\")\n" +
                "];\n" +
                "const patcher = candidates.find(file => { try { return fs.existsSync(file); } catch { return false; } });\n" +
                "if (!patcher) throw new Error(\"[Moggcord] patcher.js not found. Open the Moggcord Installer and choose Repair.\");\n" +
                "process.env.MOGGCORD_PATCHER_DIR = path.dirname(patcher);\n" +
                "require(patcher);\n";
        }

        private void WriteLoader(string appDir)
        {
            File.WriteAllText(Path.Combine(appDir, "package.json"), "{\"name\":\"moggcord\",\"main\":\"index.js\"}");
            File.WriteAllText(Path.Combine(appDir, "index.js"), LoaderScript());
        }

        private void WriteLoaderArchive(string archivePath)
        {
            var index = Encoding.UTF8.GetBytes(LoaderScript());
            var package = Encoding.UTF8.GetBytes("{\"name\":\"discord\",\"main\":\"index.js\"}");
            var header = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                files = new Dictionary<string, object>
                {
                    ["index.js"] = new { size = index.Length, offset = "0" },
                    ["package.json"] = new { size = package.Length, offset = index.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                }
            }));
            var aligned = (header.Length + 3) & ~3;
            using var writer = new BinaryWriter(File.Create(archivePath));
            writer.Write(4);
            writer.Write(aligned + 8);
            writer.Write(aligned + 4);
            writer.Write(header.Length);
            writer.Write(header);
            writer.Write(new byte[aligned - header.Length]);
            writer.Write(index);
            writer.Write(package);
        }

        private string JsonEscape(string s)
        {
            // Produce a JSON string literal: "value" with proper escaping
            return System.Text.Json.JsonSerializer.Serialize(s);
        }

        private void InstallDefaultSettings()
        {
            string json;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MoggcordInstaller.default-settings.json"))
            {
                if (stream == null) return;
                using (var reader = new StreamReader(stream))
                {
                    json = reader.ReadToEnd();
                }
            }

            try
            {
                using (JsonDocument.Parse(json)) { }
            }
            catch
            {
                return;
            }

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var paths = new[]
            {
                Path.Combine(appData, "Moggcord", "settings", "settings.json"),
                Path.Combine(appData, "MoggcordData", "settings", "settings.json"),
                Path.Combine(localAppData, "Moggcord", "settings", "settings.json"),
                Path.Combine(localAppData, "MoggcordData", "settings", "settings.json"),
            };

            foreach (var path in paths)
            {
                TryWriteDefaultSettings(path, json);
            }
        }

        private void TryWriteDefaultSettings(string settingsPath, string json)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));

                if (File.Exists(settingsPath))
                {
                    var existing = File.ReadAllText(settingsPath);
                    if (!string.IsNullOrWhiteSpace(existing))
                    {
                        try
                        {
                            using (JsonDocument.Parse(existing)) { }
                            return;
                        }
                        catch
                        {
                            var backupPath = settingsPath + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                            File.Copy(settingsPath, backupPath, true);
                        }
                    }
                }

                File.WriteAllText(settingsPath, json);
            }
            catch { }
        }

        private void CopyAssetsToDiscord(string resPath)
        {
            SetProgress(95, "Copying binaries...");
            var appBase = Path.GetDirectoryName(resPath);

            foreach (var name in new[] { "ffmpeg.exe", "node.exe", "yt-dlp.exe", "mac", "multi-instance-icons", "ghost-server" })
            {
                var src = Path.Combine(_distDir, name);
                try
                {
                    if (File.Exists(src)) File.Copy(src, Path.Combine(appBase, name), true);
                    else if (Directory.Exists(src)) CopyDirectory(src, Path.Combine(appBase, name));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    InstallerLog.Write($"Skipped the optional helper {name}: {error.Message}");
                }
            }

            SetProgress(98, "Finishing setup...");
        }

        private void CopyDirectory(string sourceDir, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);
            foreach (var file in Directory.GetFiles(sourceDir))
                File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), true);
            foreach (var directory in Directory.GetDirectories(sourceDir))
                CopyDirectory(directory, Path.Combine(destinationDir, Path.GetFileName(directory)));
        }

        private void MarkPostInstallRelaunch()
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Moggcord");
                Directory.CreateDirectory(dir);
                File.WriteAllText(
                    Path.Combine(dir, ".pending-relaunch"),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            }
            catch { }
        }

        private static string GetDiscordProcessName(string resPath)
        {
            var executable = FindDiscordExecutable(Path.GetDirectoryName(Path.GetFullPath(resPath)));
            if (executable == null) throw new IOException("The selected Discord executable could not be found.");
            return Path.GetFileNameWithoutExtension(executable);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder filename, ref int size);

        private static string GetProcessExecutablePath(int processId)
        {
            const uint QueryLimitedInformation = 0x1000;
            using var handle = OpenProcess(QueryLimitedInformation, false, processId);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var path = new StringBuilder(32768);
            var size = path.Capacity;
            if (!QueryFullProcessImageName(handle, 0, path, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return path.ToString();
        }

        private static readonly string[] ChannelProcessNames = DiscordExecutables.Select(Path.GetFileNameWithoutExtension)
            .Concat(new[] { "Update", "updater_launcher", "updater_bootstrap", "node", "ffmpeg", "yt-dlp" }).ToArray();

        private static string GetChannelRoot(string appDirectory)
        {
            var folder = Path.GetFileName(appDirectory);
            return folder.StartsWith("app-") && Version.TryParse(folder.Substring(4), out _) ? Directory.GetParent(appDirectory).FullName : appDirectory;
        }

        private static List<(Process process, string path)> FindChannelProcesses(string channelRoot)
        {
            var found = new List<(Process, string)>();
            foreach (var process in Process.GetProcesses())
            {
                string path = null;
                if (ChannelProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        path = Path.GetFullPath(GetProcessExecutablePath(process.Id));
                    }
                    catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                    {
                        Debug.WriteLine($"Skipped process {process.Id}: {error.Message}");
                    }
                }
                if (path != null && path.StartsWith(channelRoot, StringComparison.OrdinalIgnoreCase)) found.Add((process, path));
                else process.Dispose();
            }
            return found;
        }

        private void KillDiscord(string resPath)
        {
            SetStatus("loading", "Closing Discord...");
            var channelRoot = GetChannelRoot(Path.GetDirectoryName(Path.GetFullPath(resPath))) + Path.DirectorySeparatorChar;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var running = FindChannelProcesses(channelRoot);
                if (running.Count == 0) return;
                foreach (var (process, path) in running)
                {
                    using (process)
                    {
                        try
                        {
                            InstallerLog.Write($"Closing {path} (process {process.Id})");
                            process.Kill(true);
                            process.WaitForExit(8000);
                        }
                        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
                        {
                            InstallerLog.Write($"Could not close process {process.Id}: {error.Message}");
                        }
                    }
                }
                Thread.Sleep(400);
            }

            var left = FindChannelProcesses(channelRoot);
            var blocking = left.FirstOrDefault(entry => DiscordExecutables.Contains(Path.GetFileName(entry.path), StringComparer.OrdinalIgnoreCase));
            foreach (var (process, path) in left)
            {
                InstallerLog.Write($"Still running after shutdown: {path} (process {process.Id})");
                process.Dispose();
            }
            if (blocking.path != null)
                throw new IOException($"{Path.GetFileNameWithoutExtension(blocking.path)} could not be closed. If Discord runs as administrator, quit it from the tray icon and try again.");
        }

        private string StartDiscord(string resPath)
        {
            var appDirectory = Path.GetDirectoryName(Path.GetFullPath(resPath));
            var root = GetChannelRoot(appDirectory);
            var procName = GetDiscordProcessName(resPath) + ".exe";
            var attempts = new List<(string file, string arguments, string directory, int seconds)>();
            if (root != appDirectory)
            {
                attempts.Add((Path.Combine(root, procName), "--processStart " + procName, root, 25));
                attempts.Add((Path.Combine(root, "Update.exe"), "--processStart " + procName, root, 15));
            }
            attempts.Add((Path.Combine(appDirectory, procName), "", appDirectory, 15));

            foreach (var (file, arguments, directory, seconds) in attempts)
            {
                if (!File.Exists(file)) continue;
                try
                {
                    InstallerLog.Write($"Starting Discord: {file} {arguments}".TrimEnd());
                    if (InstallerLog.IsElevated)
                        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + file + "\"") { UseShellExecute = true })?.Dispose();
                    else
                        Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true, WorkingDirectory = directory })?.Dispose();
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
                {
                    InstallerLog.Write("Discord could not be started this way: " + error.Message);
                    continue;
                }

                var started = WaitForDiscord(root, appDirectory, Path.GetFileNameWithoutExtension(procName), TimeSpan.FromSeconds(seconds));
                if (started != null)
                {
                    InstallerLog.Write("Discord is running from " + started);
                    return Path.Combine(started, "resources");
                }
                InstallerLog.Write($"Discord did not open within {seconds} seconds.");
            }
            return null;
        }

        private static string WaitForDiscord(string root, string appDirectory, string processName, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    string folder = null;
                    try
                    {
                        folder = Path.GetDirectoryName(Path.GetFullPath(GetProcessExecutablePath(process.Id)));
                    }
                    catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                    {
                        Debug.WriteLine($"Skipped process {process.Id}: {error.Message}");
                    }
                    finally
                    {
                        process.Dispose();
                    }
                    if (folder == null) continue;
                    if (string.Equals(folder, appDirectory, StringComparison.OrdinalIgnoreCase)) return folder;
                    if (root != appDirectory && Path.GetFileName(folder).StartsWith("app-")
                        && string.Equals(Path.GetDirectoryName(folder), root, StringComparison.OrdinalIgnoreCase)) return folder;
                }
                Thread.Sleep(300);
            }
            return null;
        }

        private string ExtractJsonValue(string json, string key, string matchPattern = null)
        {
            var matches = Regex.Matches(json, $"\"{key}\"\\s*:\\s*\"([^\"]+)\"");
            foreach (Match m in matches)
            {
                var val = m.Groups[1].Value;
                if (matchPattern == null || val.EndsWith(matchPattern)) return val;
            }
            return null;
        }
    }
}
