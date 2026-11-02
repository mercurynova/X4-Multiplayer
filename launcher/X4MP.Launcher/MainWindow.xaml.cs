using System.IO;
using System.Net.Http;
using System.Windows;
using X4MP.Launcher.Core;

namespace X4MP.Launcher;

/// <summary>
/// The whole launcher for now: read the server's manifest and the local game, and show the plan. M6-X3 adds Apply,
/// M6-X4 the trust prompts; keeping the work in X4MP.Launcher.Core is what makes those testable without a display.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnCheck(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        SessionBlock.Text = string.Empty;
        StatusBlock.Text = "Checking...";
        try
        {
            var result = await Task.Run(() => CheckAsync(ServerBox.Text.Trim(), PasswordBox.Password));
            PlanList.ItemsSource = result.Plan;
            SessionBlock.Text = result.Session;
            StatusBlock.Text = result.Summary;
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
        }

        void Show(string text)
        {
            PlanList.ItemsSource = null;
            StatusBlock.Text = text;
        }
    }

    private static async Task<CheckResult> CheckAsync(string address, string password)
    {
        var install = X4InstallFinder.Find(SteamLocator.Roots())
            ?? throw new InvalidOperationException(
                "X4: Foundations was not found. Start it once from Steam, then check again.");

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var installed = ExtensionScanner.Scan(install, documents);
        var profiles = X4Profiles.Find(documents);
        var profile = profiles.Count > 0
            ? ProfileContent.Load(profiles[0].ContentXmlPath)
            : ProfileContent.Load(Path.Combine(X4Profiles.BaseDirectory(documents), "content.xml"));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var manifest = await new JoinManifestClient(http).FetchAsync(address, password, CancellationToken.None);
        var plan = ModPlanner.Plan(manifest, installed, profile);

        var note = profiles.Count switch
        {
            0 => "No X4 profile was found yet: the extension defaults were used. Start the game once.",
            > 1 => $"Several X4 profiles were found; the newest one ({profiles[0].SteamId}) was used.",
            _ => string.Empty,
        };

        var summary = plan.Count == 0
            ? "Nothing to do: the installed mods already match this session."
            : $"{plan.Count} change(s) are needed before this session will accept you.";
        if (note.Length > 0) summary += " " + note;

        return new CheckResult(plan, $"Session \"{manifest.Session}\", policy v{manifest.PolicyVersion}, game build {manifest.GameBuild}", summary);
    }

    private sealed record CheckResult(IReadOnlyList<PlanItem> Plan, string Session, string Summary);
}
