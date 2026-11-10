using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using X4MP.Launcher.Core;

namespace X4MP.Launcher;

/// <summary>
/// The launcher window: read the server's manifest and the local game, show the plan, write it when the player asks,
/// and put the profile back when X4 exits. The decisions and the file format live in X4MP.Launcher.Core, so the tests
/// drive them without a display.
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _watcher = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly TrustStore _trust = TrustStore.Load(TrustStore.DefaultPath);
    private IReadOnlyList<PlanItem>? _plan;
    private X4Profile? _profile;
    private GameInstall? _install;
    private LocalServerHost? _localServer;
    private LocalServerPlan? _localPlan;
    private string _sessionName = string.Empty;
    private bool _sawGameRunning;
    private bool _trusted = true;
    private bool _readyToStart;
    private bool _hostChanging;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        _watcher.Tick += OnWatcherTick;
        _watcher.Start();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _profile = FindProfile();
        _sawGameRunning = X4Process.IsRunning();
        NameBox.Text = Environment.UserName;
        if (_profile is not null && ProfileWriter.HasPendingRestore(_profile))
        {
            StatusBlock.Text = "The mod set of a previous session is still pending: X4's exit restores it automatically, or press Restore now.";
        }
        UpdateButtons();
    }

    private async void OnCheck(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        CheckAgainButton.IsEnabled = false;
        SessionBlock.Text = string.Empty;
        StatusBlock.Text = "Checking...";
        // Read the controls here, on the UI thread: inside Task.Run they would be touched from a pool thread and WPF
        // throws "The calling thread cannot access this object because a different thread owns it".
        var address = ServerBox.Text.Trim();
        var password = PasswordBox.Password;
        try
        {
            var result = await Task.Run(() => CheckAsync(address, password));
            _plan = result.Plan;
            _profile = result.Profile ?? _profile;
            _install = result.Install;
            _sessionName = result.SessionName;
            _readyToStart = _plan.Count == 0;
            PlanList.ItemsSource = _plan;
            SessionBlock.Text = result.SessionLine;
            StatusBlock.Text = result.Summary;
            _trusted = EnsureTrusted(ServerBox.Text.Trim());
        }
        catch (JoinClientException error)
        {
            Show(error.Message);
        }
        catch (HttpRequestException error)
        {
            Show("The server could not be reached: " + error.Message);
        }
        catch (TaskCanceledException)
        {
            Show("The server did not answer in time. Check the address and that the server is running.");
        }
        catch (IOException error)
        {
            Show("A game file could not be read: " + error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            Show("Windows denied access to a game file: " + error.Message);
        }
        catch (InvalidOperationException error)
        {
            Show(error.Message);
        }
        finally
        {
            CheckButton.IsEnabled = true;
            CheckAgainButton.IsEnabled = true;
            UpdateButtons();
        }

        void Show(string text)
        {
            _plan = null;
            _trusted = true;
            _readyToStart = false;
            PlanList.ItemsSource = null;
            StatusBlock.Text = text;
        }
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (_plan is null || _profile is null)
        {
            return;
        }

        if (!_trusted && !EnsureTrusted(ServerBox.Text.Trim()))
        {
            return;
        }

        var risky = _plan
            .Where(item => item.Action == PlanAction.Enable && (item.HasNativeDll || item.ReplacesBaseGame))
            .ToList();
        if (risky.Count > 0 && !ConfirmRisky(risky))
        {
            StatusBlock.Text = "Nothing was changed.";
            return;
        }

        try
        {
            var result = ProfileWriter.Apply(_profile, _sessionName, _plan, X4Process.IsRunning(), DateTimeOffset.Now);
            if (!result.Changed)
            {
                StatusBlock.Text = "This plan has nothing the launcher can write: there is no mod to switch on or off.";
                return;
            }

            var text = $"Changed {result.Edits} entry(ies). The original is kept as {Path.GetFileName(result.BackupFile)}.";
            if (KeepCheckBox.IsChecked == true)
            {
                ProfileWriter.Discard(_profile);
                text += " They stay this way after X4 exits.";
            }
            else
            {
                text += " They are restored when X4 exits, or on the next start of this launcher.";
            }

            StatusBlock.Text = text;
            _plan = null;
            _readyToStart = true;
            PlanList.ItemsSource = null;
        }
        catch (InvalidOperationException error)
        {
            StatusBlock.Text = error.Message;
        }
        catch (IOException error)
        {
            StatusBlock.Text = "The profile could not be written: " + error.Message;
        }
        catch (UnauthorizedAccessException error)
        {
            StatusBlock.Text = "Windows denied access to the profile: " + error.Message;
        }
        finally
        {
            UpdateButtons();
        }
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_profile is null)
        {
            return;
        }

        try
        {
            var result = ProfileWriter.Restore(_profile);
            if (!result.Restored)
            {
                StatusBlock.Text = "There was nothing to restore.";
                return;
            }

            var text = result.FileRemoved
                ? "The profile had no content.xml before the launcher created one, so it was removed again."
                : "Your original mod set is back.";
            if (result.Skipped.Count > 0)
            {
                text += " Left as they are now because they were changed in game: " + string.Join(", ", result.Skipped) + ".";
            }

            StatusBlock.Text = text;
            _plan = null;
            PlanList.ItemsSource = null;
        }
        catch (IOException error)
        {
            StatusBlock.Text = "The profile could not be restored: " + error.Message;
        }
        catch (UnauthorizedAccessException error)
        {
            StatusBlock.Text = "Windows denied access to the profile: " + error.Message;
        }
        finally
        {
            UpdateButtons();
        }
    }

    private void OnPlanSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = PlanList.SelectedItem as PlanItem;
        WorkshopButton.IsEnabled = item is { Action: PlanAction.Missing, WorkshopId: > 0 };
        NexusButton.IsEnabled = item is { Action: PlanAction.Missing } && !string.IsNullOrEmpty(item.NexusUrl);
    }

    private void OnOpenWorkshop(object sender, RoutedEventArgs e)
    {
        if (PlanList.SelectedItem is PlanItem { Action: PlanAction.Missing } item
            && Open(ExternalLinks.WorkshopOpen(item.WorkshopId)))
        {
            StatusBlock.Text = "Subscribe to the mod in Steam, then press Check again.";
        }
    }

    private void OnOpenNexus(object sender, RoutedEventArgs e)
    {
        if (PlanList.SelectedItem is PlanItem { Action: PlanAction.Missing } item
            && Open(ExternalLinks.NexusPage(item.NexusUrl)))
        {
            StatusBlock.Text = "Install the mod from Nexus as it says on the page, then press Check again.";
        }
    }

    /// <summary>Opens a page only when it passes the whitelist in <see cref="ExternalLinks"/>.</summary>
    private bool Open(string? url)
    {
        if (!ExternalLinks.IsAllowed(url))
        {
            StatusBlock.Text = "The launcher opens only Steam Workshop and Nexus Mods pages.";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true });
            return true;
        }
        catch (Win32Exception error)
        {
            StatusBlock.Text = "Windows could not open the page: " + error.Message;
            return false;
        }
    }

    /// <summary>
    /// First contact with a server: say exactly what it wants to change, and remember the answer per address. A plan
    /// without a toggle changes nothing, so it never needs a prompt.
    /// </summary>
    private bool EnsureTrusted(string address)
    {
        if (_plan is null || !_plan.Any(item => item.Action is PlanAction.Enable or PlanAction.Disable))
        {
            return true;
        }

        if (_trust.IsTrusted(address))
        {
            return true;
        }

        var answer = MessageBox.Show(
            this,
            "This server wants to change which of your installed mods are enabled.\n\n"
            + $"Server: {_sessionName}\nAddress: {ServerAddress.Key(address)}\n\n"
            + "Nothing is downloaded. The launcher edits only your profile's content.xml and puts it back when X4 exits.\n\n"
            + "Trust this server from now on?",
            "First contact with this server",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            StatusBlock.Text += " The plan is shown, but it will not be applied until you trust this server.";
            return false;
        }

        _trust.Trust(address);
        return true;
    }

    /// <summary>A mod that runs code or replaces game files gets a second, explicit yes.</summary>
    private bool ConfirmRisky(IReadOnlyList<PlanItem> risky)
    {
        var lines = risky.Select(item =>
            $"  - {item.Name}: {Why(item)}").ToList();
        var text = "This session needs mods that can run code or change the game itself:\n\n"
            + string.Join("\n", lines)
            + "\n\nEnable them anyway?";
        return MessageBox.Show(this, text, "Mods that change the game", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            == MessageBoxResult.Yes;

        static string Why(PlanItem item) => (item.HasNativeDll, item.ReplacesBaseGame) switch
        {
            (true, true) => "contains native code and replaces game files",
            (true, false) => "contains native code",
            _ => "replaces part of the game's interface",
        };
    }

    private async void OnWatcherTick(object? sender, EventArgs e)
    {
        if (X4Process.IsRunning())
        {
            _sawGameRunning = true;
            return;
        }

        if (!_sawGameRunning)
        {
            return;
        }

        _sawGameRunning = false;

        if (_localServer is not null)
        {
            var external = _localServer.Status.External;
            await StopLocalServerAsync();
            if (HostCheckBox.IsChecked == true)
            {
                _hostChanging = true;
                HostCheckBox.IsChecked = false;
                _hostChanging = false;
            }

            StatusBlock.Text = external
                ? "X4 has exited. The local server was left running because it was started outside the launcher."
                : "X4 has exited: the local server was stopped.";
        }

        if (_profile is not null && ProfileWriter.HasPendingRestore(_profile))
        {
            try
            {
                var result = ProfileWriter.Restore(_profile);
                StatusBlock.Text = result.Restored
                    ? "X4 has exited: your original mod set was restored."
                    : "X4 has exited, but the mod set could not be restored.";
            }
            catch (IOException error)
            {
                StatusBlock.Text = "X4 has exited, but the profile could not be restored: " + error.Message;
            }
            catch (UnauthorizedAccessException error)
            {
                StatusBlock.Text = "X4 has exited, but Windows denied access to the profile: " + error.Message;
            }
        }

        UpdateButtons();
    }

    /// <summary>
    /// Starts X4 with one join request: the mod reads <c>launch.json</c> at startup, joins, and deletes the file, so a
    /// second start without this button joins nothing. The request is written before Steam is asked, because the game
    /// can reach the loading screen before the window would get a chance to write anything else.
    /// </summary>
    private async void OnStart(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            StatusBlock.Text = "Enter the name other players will see, then press Start X4.";
            NameBox.Focus();
            return;
        }

        if (HostCheckBox.IsChecked == true)
        {
            await StartHostAsync(name);
            return;
        }

        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var directory = LaunchFile.DirectoryFor(_install, localAppData);
            var file = LaunchFile.Write(
                directory,
                ServerBox.Text.Trim(),
                name,
                PasswordBox.Password,
                DateTimeOffset.UtcNow.AddMinutes(LaunchFile.LifetimeMinutes));

            if (!Open(ExternalLinks.SteamGame))
            {
                return;
            }

            StatusBlock.Text = $"Wrote {file}. Steam is starting X4; the mod joins {ServerAddress.HostAndPort(ServerBox.Text)} "
                + $"as {name} and deletes the request once it has read it. It is valid for {LaunchFile.LifetimeMinutes} minutes.";
        }
        catch (ArgumentException error)
        {
            StatusBlock.Text = error.Message;
        }
        catch (IOException error)
        {
            StatusBlock.Text = "The join request could not be written: " + error.Message;
        }
        catch (UnauthorizedAccessException error)
        {
            StatusBlock.Text = "Windows denied access to the join request: " + error.Message;
        }

        UpdateButtons();
    }

    /// <summary>
    /// Host mode: the sidecar is already running (or is adopted), so the only thing left is an authority join request
    /// that carries the node password the server was started with. The mod reads it once, at X4 startup.
    /// </summary>
    private async Task StartHostAsync(string name)
    {
        if (_localPlan is null || _localServer is null)
        {
            StatusBlock.Text = "The local server is not ready. Untick and tick Host this session again.";
            return;
        }

        if (!_localServer.Status.IsRunning)
        {
            StatusBlock.Text = "Starting the local server...";
            var status = await _localServer.StartAsync(_localPlan);
            if (!status.IsRunning)
            {
                StatusBlock.Text = status.Error ?? "The local server did not start.";
                return;
            }
        }

        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var directory = LaunchFile.DirectoryFor(_install, localAppData);
            var file = LaunchFile.WriteHost(
                directory,
                name,
                PasswordBox.Password,
                _localPlan.AdminPassword,
                DateTimeOffset.UtcNow.AddMinutes(LaunchFile.LifetimeMinutes),
                _localPlan.NodePort);

            if (!Open(ExternalLinks.SteamGame))
            {
                await StopLocalServerAsync();
                return;
            }

            StatusBlock.Text = $"Wrote {file}. The local server is at {_localPlan.AdminAddress}; X4 joins "
                + $"{LocalServerPlan.HostAddress(_localPlan.NodePort)} as {name} with the authority role. "
                + $"The request is valid for {LaunchFile.LifetimeMinutes} minutes.";
        }
        catch (ArgumentException error)
        {
            StatusBlock.Text = error.Message;
        }
        catch (IOException error)
        {
            StatusBlock.Text = "The join request could not be written: " + error.Message;
        }
        catch (UnauthorizedAccessException error)
        {
            StatusBlock.Text = "Windows denied access to the join request: " + error.Message;
        }
        finally
        {
            UpdateButtons();
        }
    }

    /// <summary>Checks the box: start the sidecar now, so Check can read its manifest and Start has only to launch X4.</summary>
    private async void OnHostChanged(object sender, RoutedEventArgs e)
    {
        if (_hostChanging)
        {
            return;
        }

        if (HostCheckBox.IsChecked != true)
        {
            if (X4Process.IsRunning())
            {
                var answer = MessageBox.Show(
                    this,
                    "X4 is still running. Stopping the local server disconnects it. Stop the server anyway?",
                    "Stop the local server",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    RevertHostCheck();
                    return;
                }
            }

            await StopLocalServerAsync();
            _readyToStart = false;
            SessionBlock.Text = string.Empty;
            StatusBlock.Text = "The local server is stopped.";
            UpdateButtons();
            return;
        }

        if (X4Process.IsRunning())
        {
            StatusBlock.Text = "Close X4 before starting the local server.";
            RevertHostCheck();
            return;
        }

        HostCheckBox.IsEnabled = false;
        StatusBlock.Text = "Starting the local server...";
        try
        {
            var install = _install ?? X4InstallFinder.Find(SteamLocator.Roots());
            if (install is null)
            {
                StatusBlock.Text = "X4: Foundations was not found. Start it once from Steam, then try again.";
                RevertHostCheck();
                return;
            }

            _install = install;
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _localPlan = LocalServerPlan.Create(install, localAppData, AppContext.BaseDirectory);
            _localServer ??= new LocalServerHost(new WindowsServerProcess());
            var status = await _localServer.StartAsync(_localPlan);
            if (!status.IsRunning)
            {
                StatusBlock.Text = status.Error ?? "The local server did not start.";
                _localPlan = null;
                RevertHostCheck();
                return;
            }

            ServerBox.Text = _localPlan.AdminAddress;
            _readyToStart = false;
            _trusted = true;
            _plan = null;
            PlanList.ItemsSource = null;
            SessionBlock.Text = status.External
                ? "A local server is already running; the launcher will use it."
                : "The local server is running.";
            StatusBlock.Text = $"Admin console: {_localPlan.AdminAddress}. On its first run the admin password is in "
                + Path.Combine(_localPlan.DataDirectory, "initial-admin-password.txt")
                + ". Press Check to compare your installed mods with this session, then Start X4.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                                          or InvalidOperationException or Win32Exception)
        {
            StatusBlock.Text = "The local server could not be started: " + error.Message;
            _localPlan = null;
            RevertHostCheck();
        }
        finally
        {
            HostCheckBox.IsEnabled = true;
            UpdateButtons();
        }
    }

    private async Task StopLocalServerAsync()
    {
        var server = _localServer;
        _localPlan = null;
        if (server is null)
        {
            return;
        }

        try
        {
            await Task.Run(server.Stop);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or Win32Exception)
        {
            StatusBlock.Text = "The local server could not be stopped cleanly: " + error.Message;
        }
    }

    private void RevertHostCheck()
    {
        _hostChanging = true;
        HostCheckBox.IsChecked = false;
        _hostChanging = false;
    }

    /// <summary>
    /// A host server is a child of this window: closing the launcher closes its Job Object, which kills the server.
    /// Ask first while X4 is still connected, and give the server the same graceful stop the mod uses.
    /// </summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing || _localServer is null)
        {
            return;
        }

        var status = _localServer.Status;
        if (!status.IsRunning)
        {
            _localServer.Dispose();
            _localServer = null;
            return;
        }

        if (!status.External && X4Process.IsRunning())
        {
            var answer = MessageBox.Show(
                this,
                "X4 is still running. Closing the launcher stops the local server and disconnects the game. Close anyway?",
                "Close the launcher",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        e.Cancel = true;
        _closing = true;
        StatusBlock.Text = "Stopping the local server...";
        try
        {
            await StopLocalServerAsync();
        }
        finally
        {
            _localServer.Dispose();
            _localServer = null;
            Close();
        }
    }

    private void UpdateButtons()
    {
        ApplyButton.IsEnabled = _trusted && _plan is not null && _profile is not null
            && _plan.Any(item => item.Action is PlanAction.Enable or PlanAction.Disable);
        RestoreButton.IsEnabled = _profile is not null && ProfileWriter.HasPendingRestore(_profile);
        StartButton.IsEnabled = _readyToStart;
    }

    private static X4Profile? FindProfile()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var profiles = X4Profiles.Find(documents);
        return profiles.Count > 0 ? profiles[0] : null;
    }

    private static async Task<CheckResult> CheckAsync(string address, string password)
    {
        var install = X4InstallFinder.Find(SteamLocator.Roots())
            ?? throw new InvalidOperationException(
                "X4: Foundations was not found. Start it once from Steam, then check again.");

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var installed = ExtensionScanner.Scan(install, documents);
        var profiles = X4Profiles.Find(documents);
        var profile = profiles.Count > 0 ? profiles[0] : null;
        var content = profile is not null
            ? ProfileContent.Load(profile.ContentXmlPath)
            : ProfileContent.Load(Path.Combine(X4Profiles.BaseDirectory(documents), "content.xml"));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var manifest = await new JoinManifestClient(http).FetchAsync(address, password, CancellationToken.None);
        var plan = ModPlanner.Plan(manifest, installed, content);

        var note = profiles.Count switch
        {
            0 => "No X4 profile was found yet: the extension defaults were used. Start the game once before applying a plan.",
            > 1 => $"Several X4 profiles were found; the newest one ({profiles[0].SteamId}) was used.",
            _ => string.Empty,
        };

        var summary = plan.Count == 0
            ? "Nothing to do: the installed mods already match this session."
            : $"{plan.Count} change(s) are needed before this session will accept you.";
        if (note.Length > 0)
        {
            summary += " " + note;
        }

        return new CheckResult(
            plan,
            $"Session \"{manifest.Session}\", policy v{manifest.PolicyVersion}, game build {manifest.GameBuild}",
            manifest.Session,
            summary,
            profile,
            install);
    }

    private sealed record CheckResult(
        IReadOnlyList<PlanItem> Plan,
        string SessionLine,
        string SessionName,
        string Summary,
        X4Profile? Profile,
        GameInstall Install);
}
