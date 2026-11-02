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
    private string _sessionName = string.Empty;
    private bool _sawGameRunning;
    private bool _trusted = true;

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
        try
        {
            var result = await Task.Run(() => CheckAsync(ServerBox.Text.Trim(), PasswordBox.Password));
            _plan = result.Plan;
            _profile = result.Profile ?? _profile;
            _sessionName = result.SessionName;
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

    private void OnWatcherTick(object? sender, EventArgs e)
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
        if (_profile is null || !ProfileWriter.HasPendingRestore(_profile))
        {
            return;
        }

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

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        ApplyButton.IsEnabled = _trusted && _plan is not null && _profile is not null
            && _plan.Any(item => item.Action is PlanAction.Enable or PlanAction.Disable);
        RestoreButton.IsEnabled = _profile is not null && ProfileWriter.HasPendingRestore(_profile);
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
            profile);
    }

    private sealed record CheckResult(
        IReadOnlyList<PlanItem> Plan,
        string SessionLine,
        string SessionName,
        string Summary,
        X4Profile? Profile);
}
