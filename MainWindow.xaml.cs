using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace Local_Port_Utility;

public sealed partial class MainWindow : Window
{
    private static string SoftwareVersion => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "unknown";
    private static readonly string HistoryFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Local Port Utility", "history.json");
    private static readonly string LegacyExpiryTaskDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Local Port Utility", "ExpiryTasks");
    private static readonly string ExpiryTaskRootDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Local Port Utility");
    private static readonly string ExpiryTaskDirectory = Path.Combine(ExpiryTaskRootDirectory, "ExpiryTasks", GetCurrentUserSid());
    private static readonly string SettingsFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Local Port Utility", "settings.json");
    private static readonly string PowerShellPath = GetPowerShellPath();
    private readonly List<PortEntryControls> _portEntries = new();
    private readonly ObservableCollection<HistoryRecord> _history = new();
    private readonly BoreTunnelService _boreTunnelService = new();
    private readonly PublicPortChecker _publicPortChecker = new();
    private readonly DispatcherQueueTimer _expiryTimer;
    private bool _expiryCheckRunning;
    private bool _isClosing;
    private bool _settingsReady;
    private string? _boreEndpoint;
    private NavigationViewItem? _lastPrimaryNavigationItem;

    private static string GetPowerShellPath()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!Environment.Is64BitProcess && Environment.Is64BitOperatingSystem)
        {
            // A 32-bit app sees the redirected system folder Sysnative reaches native PowerShell.
            var nativePowerShell = Path.Combine(windowsDirectory, "Sysnative", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(nativePowerShell)) return nativePowerShell;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    public MainWindow()
    {
        InitializeComponent();
        ConfigureTitleBar();
        SetInitialWindowSize();
        _lastPrimaryNavigationItem = MainNavigationView.SelectedItem as NavigationViewItem;
        LoadSettings();
        _settingsReady = true;
        RootGrid.RequestedTheme = ThemeComboBox.SelectedIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        SystemBackdrop = TransparencyToggleSwitch.IsOn ? new MicaBackdrop() : null;
        ApplyThemeSurface();
        ConfigureTitleBar();
        SettingsVersionTextBlock.Text = $"Version {SoftwareVersion}";
        HomeVersionTextBlock.Text = $"Version {SoftwareVersion}";
        HistoryListBox.ItemsSource = _history;
        AddPortEntry();
        _ = LoadLocalIpv4AddressesAsync();
        LoadHistory();
        UpdateHistoryEmptyState();
        _ = ScheduleExistingExpirationsAsync();
        _ = RefreshHistoryStatusAsync();
        _expiryTimer = DispatcherQueue.CreateTimer();
        _expiryTimer.Interval = TimeSpan.FromSeconds(15);
        _expiryTimer.Tick += ExpiryTimer_Tick;
        _expiryTimer.Start();
        _boreTunnelService.OutputReceived += BoreTunnelService_OutputReceived;
        _boreTunnelService.EndpointDetected += BoreTunnelService_EndpointDetected;
        _boreTunnelService.Exited += BoreTunnelService_Exited;
        Closed += (_, _) =>
        {
            _isClosing = true;
            _expiryTimer.Stop();
            _boreTunnelService.Dispose();
        };
    }

    private void SetInitialWindowSize()
    {
        ResizeWindow(1000);
    }

    private void ResizeWindow(int width)
    {
        try
        {
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
            AppWindow.GetFromWindowId(windowId).Resize(new SizeInt32(width, 760));
        }
        catch (Exception)
        {
            // keep the system provided size if the windowing API is unavailable
        }
    }

    private void ConfigureTitleBar()
    {
        if (RootGrid is null) return;

        try
        {
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
            var titleBar = AppWindow.GetFromWindowId(windowId).TitleBar;
            var isLightTheme = RootGrid.ActualTheme == ElementTheme.Light;
            var background = isLightTheme ? Color.FromArgb(255, 246, 246, 246) : Color.FromArgb(255, 28, 28, 28);
            var inactiveBackground = isLightTheme ? Color.FromArgb(255, 235, 235, 235) : Color.FromArgb(255, 38, 38, 38);
            var foreground = isLightTheme ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 245, 245, 245);
            var inactiveForeground = isLightTheme ? Color.FromArgb(255, 105, 105, 105) : Color.FromArgb(255, 190, 190, 190);
            var buttonHover = isLightTheme ? Color.FromArgb(255, 225, 225, 225) : Color.FromArgb(255, 58, 58, 58);
            var buttonPressed = isLightTheme ? Color.FromArgb(255, 210, 210, 210) : Color.FromArgb(255, 75, 75, 75);
            titleBar.ExtendsContentIntoTitleBar = false;
            titleBar.BackgroundColor = background;
            titleBar.InactiveBackgroundColor = inactiveBackground;
            titleBar.ForegroundColor = foreground;
            titleBar.InactiveForegroundColor = inactiveForeground;
            titleBar.ButtonBackgroundColor = background;
            titleBar.ButtonInactiveBackgroundColor = inactiveBackground;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = inactiveForeground;
            titleBar.ButtonHoverBackgroundColor = buttonHover;
            titleBar.ButtonPressedBackgroundColor = buttonPressed;
        }
        catch (Exception)
        {
            // keep the platform title bar if native tite bar customization is unavailable
        }
    }

    private void AddPortButton_Click(object sender, RoutedEventArgs e) => AddPortEntry();

    private void AddPortEntry()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var width in new[] { 1.1, 1.1, 1.1, 1.1 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var type = new ComboBox { ItemsSource = new[] { "Single port", "Port range" }, SelectedIndex = 0, Margin = new Thickness(0, 0, 8, 0) };
        var start = new TextBox { PlaceholderText = "Start", MaxLength = 5, Margin = new Thickness(0, 0, 8, 0) };
        var end = new TextBox { PlaceholderText = "End", MaxLength = 5, IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
        var protocol = new ComboBox { ItemsSource = new[] { "TCP", "UDP", "TCP + UDP" }, SelectedIndex = 0, Margin = new Thickness(0, 0, 8, 0) };
        var remove = new Button { Padding = new Thickness(9, 5, 9, 5), Content = new FontIcon { Glyph = "\uE74D" } };
        var controls = new PortEntryControls(type, start, end, protocol);
        _portEntries.Add(controls);
        type.SelectionChanged += (_, _) => end.IsEnabled = type.SelectedIndex == 1;
        remove.Click += (_, _) => { if (_portEntries.Count > 1) { PortEntriesPanel.Children.Remove(grid); _portEntries.Remove(controls); } };
        Grid.SetColumn(type, 0); Grid.SetColumn(start, 1); Grid.SetColumn(end, 2); Grid.SetColumn(protocol, 3); Grid.SetColumn(remove, 4);
        grid.Children.Add(type); grid.Children.Add(start); grid.Children.Add(end); grid.Children.Add(protocol); grid.Children.Add(remove);
        PortEntriesPanel.Children.Add(grid);
    }

    private void AutoDeleteCheckBox_Changed(object sender, RoutedEventArgs e) => AutoDeleteHoursTextBox.IsEnabled = AutoDeleteCheckBox.IsChecked == true;

    private async Task LoadLocalIpv4AddressesAsync()
    {
        var options = await Task.Run(GetLocalIpv4Options);
        if (_isClosing) return;
        LocalIpv4ComboBox.ItemsSource = options;
        if (options.Count > 0) LocalIpv4ComboBox.SelectedIndex = 0;
    }

    private static List<Ipv4TargetOption> GetLocalIpv4Options()
    {
        var options = NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up)
            .SelectMany(item => item.GetIPProperties().UnicastAddresses).Select(item => item.Address.ToString()).Where(IsPrivateIpv4).Distinct().OrderBy(item => item)
            .Select(item => new Ipv4TargetOption(item, $"{item} (This computer)")).ToList();
        foreach (var address in GetLanNeighborAddresses())
        {
            if (options.All(item => item.Address != address)) options.Add(new Ipv4TargetOption(address, $"{address} (LAN neighbor)"));
        }
        return options;
    }

    private void LocalIpv4ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LocalIpv4ComboBox.SelectedItem is Ipv4TargetOption option) TestTargetTextBox.Text = option.Address;
    }

    private async void RefreshNetworkButton_Click(object sender, RoutedEventArgs e) => await LoadLocalIpv4AddressesAsync();

    private async void MainNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var selectedPage = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "Open";
        if (selectedPage == "About")
        {
            await ShowAboutDialogAsync();
            if (_isClosing) return;
            sender.SelectedItem = _lastPrimaryNavigationItem ?? sender.MenuItems[0];
            return;
        }

        if (selectedPage is "Home" or "Open" or "Connectivity" or "Bore" or "History" or "Settings")
        {
            _lastPrimaryNavigationItem = args.SelectedItem as NavigationViewItem;
        }

        OpenPage.Visibility = selectedPage == "Open" ? Visibility.Visible : Visibility.Collapsed;
        HomePage.Visibility = selectedPage == "Home" ? Visibility.Visible : Visibility.Collapsed;
        ConnectivityPage.Visibility = selectedPage == "Connectivity" ? Visibility.Visible : Visibility.Collapsed;
        BorePage.Visibility = selectedPage == "Bore" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = selectedPage == "History" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = selectedPage == "Settings" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HomeActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        var destination = MainNavigationView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal));
        if (destination is not null) MainNavigationView.SelectedItem = destination;
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RootGrid is null || ThemeComboBox is null || !_settingsReady) return;

        RootGrid.RequestedTheme = ThemeComboBox.SelectedIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        ApplyThemeSurface();
        ConfigureTitleBar();
        SaveSettings();
    }

    private void TransparencyToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (TransparencyToggleSwitch is null || !_settingsReady) return;

        SystemBackdrop = TransparencyToggleSwitch.IsOn ? new MicaBackdrop() : null;
        ApplyThemeSurface();
        SaveSettings();
    }

    private void ApplyThemeSurface()
    {
        if (RootGrid is null || TransparencyToggleSwitch is null) return;

        if (TransparencyToggleSwitch.IsOn)
        {
            RootGrid.Background = null;
            return;
        }

        var isLightTheme = RootGrid.ActualTheme == ElementTheme.Light;
        RootGrid.Background = new SolidColorBrush(isLightTheme ? Color.FromArgb(255, 246, 246, 246) : Color.FromArgb(255, 28, 28, 28));
    }

    private async void PrivacyPolicyButton_Click(object sender, RoutedEventArgs e) => await ShowDocumentDialogAsync("Privacy policy", "privacy-policy.md", "The privacy policy could not be loaded.");

    private async void LicenseButton_Click(object sender, RoutedEventArgs e) => await ShowDocumentDialogAsync("License", "LICENSE", "The license could not be loaded.");

    private async Task ShowAboutDialogAsync()
    {
        var aboutPath = Path.Combine(AppContext.BaseDirectory, "about.md");
        var aboutText = "Local Port Utility uses Windows PowerShell to make firewall management easier.";
        try
        {
            if (File.Exists(aboutPath)) aboutText = File.ReadAllText(aboutPath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var dialog = new ContentDialog
        {
            Title = "About Local Port Utility",
            Content = new ScrollViewer
            {
                MaxHeight = 440,
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        RenderMarkdown(aboutText),
                        new TextBlock { Text = $"Version {SoftwareVersion}", FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150)), Margin = new Thickness(0, 12, 0, 0) }
                    }
                }
            },
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private static StackPanel RenderMarkdown(string markdown)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var sourceLine in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = sourceLine.Trim();
            if (line.Length == 0) continue;

            var headingLevel = line.TakeWhile(character => character == '#').Count();
            if (headingLevel > 0 && line.Length > headingLevel && line[headingLevel] == ' ')
            {
                panel.Children.Add(new TextBlock
                {
                    Text = line[(headingLevel + 1)..],
                    FontSize = headingLevel == 1 ? 22 : headingLevel == 2 ? 19 : 16,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Margin = new Thickness(0, headingLevel == 1 ? 8 : 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });
                continue;
            }

            var isBullet = line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal);
            panel.Children.Add(new TextBlock
            {
                Text = isBullet ? $"• {line[2..]}" : line,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(isBullet ? 10 : 0, 0, 0, 0)
            });
        }

        return panel;
    }

    private async Task ShowDocumentDialogAsync(string title, string fileName, string fallback)
    {
        var documentText = fallback;
        try
        {
            var documentPath = Path.Combine(AppContext.BaseDirectory, fileName);
            if (File.Exists(documentPath)) documentText = File.ReadAllText(documentPath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var dialog = new ContentDialog { Title = title, Content = new ScrollViewer { MaxHeight = 360, Content = RenderMarkdown(documentText) }, CloseButtonText = "Close", XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return;
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFilePath));
            if (settings is null) return;
            ThemeComboBox.SelectedIndex = settings.ThemeIndex is >= 0 and <= 2 ? settings.ThemeIndex : 0;
            TransparencyToggleSwitch.IsOn = settings.UseTransparency;
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void SaveSettings()
    {
        if (!_settingsReady || ThemeComboBox is null || TransparencyToggleSwitch is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(new AppSettings(ThemeComboBox.SelectedIndex, TransparencyToggleSwitch.IsOn)));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void MainNavigationView_PaneOpening(NavigationView sender, object args) => ResizeWindow(1000);

    private void MainNavigationView_PaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args) => ResizeWindow(820);

    private static List<string> GetLanNeighborAddresses()
    {
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("Get-NetNeighbor -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty IPAddress"));
            using var process = Process.Start(new ProcessStartInfo { FileName = PowerShellPath, Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            if (process is null) return new();
            if (!process.WaitForExit(5000)) { process.Kill(true); return new(); }
            return process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(IsPrivateIpv4).Distinct().OrderBy(item => item).ToList();
        }
        catch { return new(); }
    }

    private async void TestConnectivityButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IPAddress.TryParse(TestTargetTextBox.Text.Trim(), out var address) || address.AddressFamily != AddressFamily.InterNetwork) { TestResultTextBlock.Text = "Enter a valid IPv4 address."; SetStatus("Enter a valid private IPv4 address.", InfoBarSeverity.Error); return; }
        if (!TryGetPort(TestPortTextBox.Text, out var port)) { TestResultTextBlock.Text = "Enter a port from 1 to 65535."; SetStatus("Enter a test port from 1 to 65535.", InfoBarSeverity.Error); return; }
        TestConnectivityButton.IsEnabled = false; TestConnectivityButton.Content = "Testing...";
        try
        {
            var result = await Task.Run(() => TestConnectivity(address.ToString(), port));
            if (_isClosing) return;
            TestResultTextBlock.Text = result.IsOpen ? $"OPEN  {address}:{port} is reachable." : $"CLOSED  {result.Error}";
            SetStatus(result.IsOpen ? "The service is reachable." : "The service is closed or unreachable.", result.IsOpen ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        finally { if (!_isClosing) { TestConnectivityButton.IsEnabled = true; TestConnectivityButton.Content = "Test"; } }
    }

    private async void CheckFirewallButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetPort(FirewallPortTextBox.Text, out var port)) { FirewallResultTextBlock.Text = "Enter a port from 1 to 65535."; SetStatus("Enter a port from 1 to 65535.", InfoBarSeverity.Error); return; }
        CheckFirewallButton.IsEnabled = false; CheckFirewallButton.Content = "Checking...";
        try
        {
            var result = await Task.Run(() => TestFirewallRule(port));
            if (_isClosing) return;
            FirewallResultInfoBar.IsOpen = true;
            if (!result.QuerySucceeded)
            {
                FirewallResultInfoBar.Title = "Firewall inspection failed";
                FirewallResultInfoBar.Message = result.Details;
                FirewallResultInfoBar.Severity = InfoBarSeverity.Error;
                FirewallResultTextBlock.Text = string.Empty;
            }
            else
            {
                FirewallResultInfoBar.Title = result.AllowsTraffic ? $"Port {port} has an enabled allow rule" : $"Port {port} is not currently allowed";
                FirewallResultInfoBar.Message = result.AllowsTraffic ? "At least one protocol is allowed by Windows Firewall." : "No enabled inbound allow rule was found for TCP or UDP.";
                FirewallResultInfoBar.Severity = result.AllowsTraffic ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
                FirewallResultTextBlock.Text = result.Protocols.Select(item => $"{(item.AllowsTraffic ? "ALLOWED" : "NOT ALLOWED")}  {item.Protocol} {port}  |  {item.Details}").Aggregate((left, right) => left + Environment.NewLine + right);
            }
            SetStatus(FirewallResultInfoBar.Title, FirewallResultInfoBar.Severity);
        }
        finally { if (!_isClosing) { CheckFirewallButton.IsEnabled = true; CheckFirewallButton.Content = "Inspect port"; } }
    }

    private async void ScanPublicPortButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetPort(PublicPortTextBox.Text, out var port))
        {
            PublicPortResultTextBlock.Text = "Enter a port from 1 to 65535.";
            SetStatus("Enter a valid public TCP port.", InfoBarSeverity.Error);
            return;
        }

        ScanPublicPortButton.IsEnabled = false;
        ScanPublicPortButton.Content = "Scanning...";
        PublicPortResultTextBlock.Text = "Finding your public IPv4 address and checking it from an external network...";
        try
        {
            var results = await _publicPortChecker.CheckAsync(port);
            if (_isClosing) return;
            PublicPortResultTextBlock.Text = string.Join(Environment.NewLine, results.Select(FormatPublicPortResult));
            var hasReachablePort = results.Any(result => result.State == "OPEN");
            var hasErrors = results.Any(result => result.Error is not null);
            SetStatus(hasErrors ? "One or more public port checks failed." : hasReachablePort ? "At least one public port was reachable." : "Public port checks completed.",
                hasErrors ? InfoBarSeverity.Warning : hasReachablePort ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            if (_isClosing) return;
            PublicPortResultTextBlock.Text = $"Could not complete the public port check: {exception.Message}";
            SetStatus("Public port check failed.", InfoBarSeverity.Error);
        }
        finally
        {
            if (!_isClosing)
            {
                ScanPublicPortButton.IsEnabled = true;
                ScanPublicPortButton.Content = "Scan public port";
            }
        }
    }

    private void StartBoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_boreTunnelService.IsRunning) return;

        var localHost = BoreLocalHostTextBox.Text.Trim();
        if (!IPAddress.TryParse(localHost, out _))
        {
            SetBoreStatus("Enter a valid local IP address.", true);
            return;
        }
        if (!TryGetPort(BoreLocalPortTextBox.Text, out var localPort))
        {
            SetBoreStatus("Enter a local port from 1 to 65535.", true);
            return;
        }
        if (!int.TryParse(BoreRemotePortTextBox.Text, out var remotePort) || remotePort is < 0 or > 65535)
        {
            SetBoreStatus("Remote port must be 0 (random) or from 1 to 65535.", true);
            return;
        }

        var server = BoreServerTextBox.Text.Trim();
        if (!BoreTunnelService.IsValidServer(server))
        {
            SetBoreStatus("Enter a server host name or IP address, without a URL scheme.", true);
            return;
        }

        try
        {
            BoreOutputTextBox.Text = string.Empty;
            _boreEndpoint = null;
            CopyBoreEndpointButton.IsEnabled = false;
            BoreEndpointTextBlock.Text = remotePort == 0 ? "Waiting for Bore to assign a public port..." : $"Requested endpoint: {server}:{remotePort}";
            SetBoreStatus("Starting Bore...", false);
            _boreTunnelService.Start(localHost, localPort, server, remotePort);
            StartBoreButton.IsEnabled = false;
            StopBoreButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            SetBoreStatus($"Could not start Bore: {exception.Message}", true);
        }
    }

    private async void BoreInfoButton_Click(object sender, RoutedEventArgs e)
    {
        var details = new StackPanel { Spacing = 10 };
        details.Children.Add(new TextBlock
        {
            Text = "Bore is a separate third-party command-line program, and some antivirus tools may flag or quarantine it. A warning does not by itself prove the file is malicious, but do not disable antivirus protection or restore a file unless you have checked it and trust its source.",
            TextWrapping = TextWrapping.Wrap
        });
        details.Children.Add(new TextBlock
        {
            Text = "If Bore is missing, get the Windows x86 or x64 executable from the official Bore release page. Put it in a folder named Tools beside Local Port Utility.exe, using the exact filename below:",
            TextWrapping = TextWrapping.Wrap
        });
        details.Children.Add(new TextBlock
        {
            Text = "x64 app: Tools\\bore-x64.exe\nx86 or ARM64 app: Tools\\bore-x86.exe",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap
        });

        var dialog = new ContentDialog
        {
            Title = "Bore download and antivirus information",
            Content = details,
            PrimaryButtonText = "Open official Bore releases",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/ekzhang/bore/releases/tag/v0.6.0") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetBoreStatus($"Could not open the Bore release page: {exception.Message}", true);
        }
    }

    private void StopBoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_boreTunnelService.IsRunning) return;

        // Bore mora ostati aktivan dok korisnik koristi prosledjeni javni endpoint
        SetBoreStatus("Stopping Bore...", false);
        try
        {
            _boreTunnelService.Stop();
        }
        catch (Exception exception)
        {
            SetBoreStatus($"Could not stop Bore: {exception.Message}", true);
        }
    }

    private void CopyBoreEndpointButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_boreEndpoint)) return;

        try
        {
            var data = new DataPackage();
            data.SetText(_boreEndpoint);
            Clipboard.SetContent(data);
            SetBoreStatus("Public TCP endpoint copied.", false);
        }
        catch (Exception exception)
        {
            SetBoreStatus($"Could not copy the endpoint: {exception.Message}", true);
        }
    }

    private void BoreTunnelService_OutputReceived(string line)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosing) return;
            var outputText = BoreOutputTextBox.Text + line + Environment.NewLine;
            BoreOutputTextBox.Text = outputText.Length > 12000 ? outputText[^9000..] : outputText;
            BoreOutputTextBox.SelectionStart = BoreOutputTextBox.Text.Length;
            BoreOutputTextBox.SelectionLength = 0;
        });
    }

    private void BoreTunnelService_EndpointDetected(string endpoint)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosing) return;
            _boreEndpoint = endpoint;
            BoreEndpointTextBlock.Text = $"Public TCP endpoint: {_boreEndpoint}";
            CopyBoreEndpointButton.IsEnabled = true;
        });
    }

    private void BoreTunnelService_Exited(int exitCode)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosing) return;
            StartBoreButton.IsEnabled = true;
            StopBoreButton.IsEnabled = false;
            _boreEndpoint = null;
            CopyBoreEndpointButton.IsEnabled = false;
            BoreEndpointTextBlock.Text = string.Empty;
            if (exitCode != 0)
            {
                SetBoreStatus($"Bore exited with code {exitCode}. See the output for details.", true);
            }
            else
            {
                SetBoreStatus("Bore has stopped.", false);
            }
        });
    }

    private void SetBoreStatus(string message, bool isError) => BoreStatusTextBlock.Text = isError ? $"Error: {message}" : message;

    private static string FormatPublicPortResult(PublicPortTestResult result)
    {
        if (result.Error is not null) return $"{result.Protocol}: CHECK FAILED  {result.Error}";
        var explanation = result.Protocol == "UDP" && result.State == "OPEN OR FILTERED"
            ? "No ICMP unreachable response was received; UDP cannot be confirmed without an application-specific reply."
            : result.Protocol == "UDP" && result.State == "CLOSED"
                ? "The external probe received an ICMP port unreachable response."
                : result.Protocol == "TCP" && result.State != "OPEN"
                    ? "No external TCP connection was accepted."
                    : result.Protocol == "TCP"
                        ? "An external TCP connection was accepted."
                        : "The external probe received a response.";
        return $"{result.Protocol}: {result.State}  {result.PublicAddress}:{result.Port} — {explanation}";
    }

    private async void OpenRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildCommands(out var commands, out var error)) { SetStatus(error, InfoBarSeverity.Error); await ShowDialogAsync("Check rule details", error); return; }
        OpenRuleButton.IsEnabled = false; OpenRuleButton.Content = "Opening...";
        try
        {
            var names = commands.Select(item => item.Name).ToList();
            var record = new HistoryRecord { RuleName = RuleNameTextBox.Text.Trim(), RuleNames = names, DisplayNames = commands.Select(item => item.DisplayName).ToList(), OpenedPortsText = string.Join(", ", BuildPortSummary()), CreatedAt = DateTime.Now, ExpiresAt = GetExpiryTime() };
            if (record.ExpiresAt is not null)
            {
                record.ExpiryTaskName = $"LocalPortUtility_Expire_{Guid.NewGuid():N}";
                record.ExpiryTaskVersion = 3;
            }
            var operations = BuildCreateCommands(commands).ToList();
            if (record.ExpiresAt is not null) operations.AddRange(BuildExpiryTaskCommands(new[] { record }));
            var result = await Task.Run(() => ExecutePowerShell(operations, names));
            if (_isClosing) return;
            if (!result.Succeeded)
            {
                SetStatus(result.Error, InfoBarSeverity.Error);
                await ShowDialogAsync(record.ExpiresAt is null ? "Could not open rule" : "Could not open or schedule rule", result.Error);
                return;
            }

            _history.Insert(0, record);
            UpdateHistoryEmptyState();
            var historySaved = SaveHistory();
            SetStatus(historySaved ? "Firewall rule opened and added to history." : "Firewall rule opened, but history could not be saved.", historySaved ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            await ShowDialogAsync("Rule opened", record.ExpiresAt is null ? "The firewall rule was opened successfully." : "The firewall rule was opened and scheduled for automatic removal.");
        }
        finally { if (!_isClosing) { OpenRuleButton.IsEnabled = true; OpenRuleButton.Content = "Open rule"; } }
    }

    private async void DeleteHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HistoryRecord record }) return;
        var expiryNote = record.ExpiresAt is DateTime expiry
            ? $"\n\nThis rule is scheduled to auto-delete at {expiry.ToLocalTime():g}. Closing it now also cancels that scheduled task."
            : string.Empty;
        var dialog = new ContentDialog { Title = "Close firewall rule?", Content = $"'{record.RuleName}'\n\n{record.OpenedPortsText}{expiryNote}", PrimaryButtonText = "Close rule", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (_isClosing) return;
        var deleteCommands = BuildDeleteCommands(record).Concat(BuildRemoveExpiryTaskCommands(record));
        var result = await Task.Run(() => ExecutePowerShell(deleteCommands));
        if (_isClosing) return;
        if (!result.Succeeded) { SetStatus(result.Error, InfoBarSeverity.Error); await ShowDialogAsync("Could not close rule", result.Error); return; }
        _history.Remove(record); var historySaved = SaveHistory();
        UpdateHistoryEmptyState();
        SetStatus(historySaved ? "Firewall rule closed and removed from history." : "Firewall rule closed, but history could not be saved.", historySaved ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private void RefreshHistoryButton_Click(object sender, RoutedEventArgs e) => _ = RefreshHistoryStatusAsync();

    private async Task ScheduleExistingExpirationsAsync()
    {
        // Upgrade old tasks previous versions stored their SYSTEM-run script in a user-writable folder
        var pending = _history.Where(item => item.ExpiresAt is not null && item.ExpiryTaskVersion < 3).ToList();
        if (pending.Count == 0) return;

        foreach (var record in pending)
        {
            if (!string.IsNullOrWhiteSpace(record.ExpiryTaskName))
            {
                var oldScriptPath = Path.Combine(LegacyExpiryTaskDirectory, record.ExpiryTaskName + ".ps1");
                try { if (File.Exists(oldScriptPath)) File.Delete(oldScriptPath); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    SetStatus("Could not disable an older auto-delete script. Remove the old task before continuing.", InfoBarSeverity.Error);
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(record.ExpiryTaskName)) record.ExpiryTaskName = $"LocalPortUtility_Expire_{Guid.NewGuid():N}";
            record.ExpiryTaskVersion = 3;
        }
        SaveHistory();
        var result = await Task.Run(() => RegisterExpiryTasks(pending));
        if (_isClosing) return;
        if (!result.Succeeded)
        {
            foreach (var record in pending) record.ExpiryTaskVersion = 0;
            SaveHistory();
            SetStatus("Could not schedule automatic removal for one or more older temporary rules.", InfoBarSeverity.Warning);
        }
    }

    private void ExpiryTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_isClosing) return;
        if (_expiryCheckRunning) return;
        _expiryCheckRunning = true;
        try
        {
            // Task Scheduler edits history.json outside the app so reload it while this window is open
            _history.Clear();
            LoadHistory();
            UpdateHistoryEmptyState();
        }
        finally { _expiryCheckRunning = false; }
    }

    private async Task RefreshHistoryStatusAsync()
    {
        var records = _history.Where(item => item.RuleNames.Count > 0 && !item.Status.StartsWith("Auto-deleted", StringComparison.Ordinal)).ToList();
        if (records.Count == 0) return;
        var result = await Task.Run(() => GetExistingFirewallRuleNames(records.SelectMany(item => item.RuleNames)));
        if (_isClosing) return;
        foreach (var record in records) record.Status = result.Succeeded && record.RuleNames.All(result.ExistingNames.Contains) ? "Active" : result.Succeeded ? "Missing from Windows Firewall" : "Status check unavailable";
        HistoryListBox.ItemsSource = null; HistoryListBox.ItemsSource = _history; SaveHistory();
    }

    private bool TryBuildCommands(out List<FirewallRuleSpec> commands, out string error)
    {
        commands = new(); error = string.Empty; var ruleName = RuleNameTextBox.Text.Trim();
        if (ruleName.Length == 0) { error = "Enter a rule name."; return false; }
        if (AutoDeleteCheckBox.IsChecked == true && (!int.TryParse(AutoDeleteHoursTextBox.Text, out var hours) || hours is < 1 or > 999)) { error = "Auto-delete hours must be from 1 to 999."; return false; }
        foreach (var entry in _portEntries)
        {
            if (!TryGetPort(entry.Start.Text, out var start)) { error = "Each port must be from 1 to 65535."; return false; }
            int? end = null;
            if (entry.Type.SelectedIndex == 1) { if (!TryGetPort(entry.End.Text, out var rangeEnd) || rangeEnd < start) { error = "Port ranges must end at or after their start port."; return false; } end = rangeEnd; }
            foreach (var protocol in GetProtocols(entry.Protocol.SelectedItem?.ToString()))
            {
                commands.Add(new FirewallRuleSpec($"LocalPortUtility_{Guid.NewGuid():N}_{protocol}", $"{ruleName} - {protocol}", start, end, protocol));
            }
        }
        return commands.Count > 0;
    }

    private static bool TryGetPort(string value, out int port) => int.TryParse(value, out port) && port is >= 1 and <= 65535;
    private List<string> BuildPortSummary() => _portEntries.Select(item => $"{(item.Type.SelectedIndex == 1 ? $"{item.Start.Text}-{item.End.Text}" : item.Start.Text)} ({item.Protocol.SelectedItem})").ToList();
    private DateTime? GetExpiryTime() => AutoDeleteCheckBox.IsChecked == true && int.TryParse(AutoDeleteHoursTextBox.Text, out var hours) ? DateTime.Now.AddHours(hours) : null;
    private static IEnumerable<string> GetProtocols(string? selection) => selection == "TCP + UDP" ? new[] { "TCP", "UDP" } : new[] { selection == "UDP" ? "UDP" : "TCP" };
    private static string Escape(string value) => value.Replace("'", "''");
    private static IEnumerable<string> BuildCreateCommands(IEnumerable<FirewallRuleSpec> rules)
    {
        foreach (var rule in rules)
        {
            var variables = rule.End is null
                ? $"$port = '{rule.Start}';"
                : $"$rangeA = {rule.Start}; $rangeB = {rule.End.Value}; $port = [string]$rangeA + '-' + [string]$rangeB;";
            yield return $"{variables} $ruleName = '{Escape(rule.Name)}'; $displayName = '{Escape(rule.DisplayName)}'; $createdRule = New-NetFirewallRule -Name $ruleName -DisplayName $displayName -Direction Inbound -Action Allow -Protocol {rule.Protocol} -LocalPort $port -ErrorAction Stop; if ($null -eq $createdRule) {{ throw 'Windows did not return the created firewall rule.' }}";
        }
    }

    private static List<string> BuildDeleteCommands(HistoryRecord record)
    {
        if (record.RuleNames.Count > 0)
        {
            var ruleNames = "@( " + string.Join(",", record.RuleNames.Distinct().Select(name => $"'{Escape(name)}'")) + " )";
            return new[]
            {
                $"$ruleNames = {ruleNames}",
                "$failedRules = @()",
                "foreach ($name in $ruleNames) {",
                "    try {",
                "        $rules = @(Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.Name -eq $name })",
                "        if ($rules.Count -gt 0) { $rules | Remove-NetFirewallRule -Confirm:$false -ErrorAction Stop }",
                "        if (@(Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.Name -eq $name }).Count -gt 0) { throw 'Rule still exists after removal.' }",
                "    } catch { $failedRules += \"${name}: $($_.Exception.Message)\" }",
                "}",
                "if ($failedRules.Count -gt 0) { throw ('Some firewall rules could not be removed: ' + ($failedRules -join '; ')) }"
            }.ToList();
        }

        var displayNames = "@( " + string.Join(",", record.DisplayNames.Distinct().Select(name => $"'{Escape(name)}'")) + " )";
        return new[]
        {
            $"$displayNames = {displayNames}",
            "$failedRules = @()",
            "foreach ($displayName in $displayNames) {",
            "    try {",
            "        $rules = @(Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.DisplayName -eq $displayName })",
            "        if ($rules.Count -gt 0) { $rules | Remove-NetFirewallRule -Confirm:$false -ErrorAction Stop }",
            "        if (@(Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.DisplayName -eq $displayName }).Count -gt 0) { throw 'Rule still exists after removal.' }",
            "    } catch { $failedRules += \"${displayName}: $($_.Exception.Message)\" }",
            "}",
            "if ($failedRules.Count -gt 0) { throw ('Some firewall rules could not be removed: ' + ($failedRules -join '; ')) }"
        }.ToList();
    }

    private static IEnumerable<string> BuildRemoveExpiryTaskCommands(HistoryRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.ExpiryTaskName)) return Enumerable.Empty<string>();
        var scriptPath = Path.Combine(ExpiryTaskDirectory, record.ExpiryTaskName + ".ps1");
        var legacyScriptPath = Path.Combine(LegacyExpiryTaskDirectory, record.ExpiryTaskName + ".ps1");
        return new[]
        {
            $"Unregister-ScheduledTask -TaskName '{Escape(record.ExpiryTaskName)}' -Confirm:$false -ErrorAction SilentlyContinue; Remove-Item -LiteralPath '{Escape(scriptPath)}' -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath '{Escape(legacyScriptPath)}' -Force -ErrorAction SilentlyContinue"
        };
    }

    private static string GetCurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? Environment.UserName;
    }

    private static PowerShellExecutionResult RegisterExpiryTasks(IEnumerable<HistoryRecord> records)
    {
        var commands = BuildExpiryTaskCommands(records).ToList();
        if (commands.Count == 0) return new(true, string.Empty);
        var errorPath = Path.Combine(Path.GetTempPath(), $"LocalPortUtility-Expiry-{Guid.NewGuid():N}.log");
        return RunElevated(string.Join(Environment.NewLine, commands), "Windows Task Scheduler could not register automatic cleanup.", errorPath);
    }

    private static IEnumerable<string> BuildExpiryTaskCommands(IEnumerable<HistoryRecord> records)
    {
        var tasks = records.Where(item => item.ExpiresAt is not null && !string.IsNullOrWhiteSpace(item.ExpiryTaskName)).ToList();
        if (tasks.Count == 0) return Enumerable.Empty<string>();

        // The task runs as SYSTEM so each directory in its script path must be admin only
        var protectedDirectories = new[]
        {
            ExpiryTaskRootDirectory,
            Path.Combine(ExpiryTaskRootDirectory, "ExpiryTasks"),
            ExpiryTaskDirectory
        };
        var commands = new List<string>();
        foreach (var directory in protectedDirectories)
        {
            var quotedPath = Escape(directory);
            commands.Add($"New-Item -ItemType Directory -Path '{quotedPath}' -Force | Out-Null");
            commands.Add($"$directory = Get-Item -LiteralPath '{quotedPath}' -Force; if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) {{ throw 'The cleanup script directory cannot be a link.' }}");
            commands.Add($"& \"$env:SystemRoot\\System32\\icacls.exe\" '{quotedPath}' /reset | Out-Null; if ($LASTEXITCODE -ne 0) {{ throw 'Could not reset cleanup folder permissions.' }}");
            commands.Add($"& \"$env:SystemRoot\\System32\\icacls.exe\" '{quotedPath}' /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null; if ($LASTEXITCODE -ne 0) {{ throw 'Could not protect the automatic cleanup script folder.' }}");
            commands.Add($"& \"$env:SystemRoot\\System32\\icacls.exe\" '{quotedPath}' /setowner '*S-1-5-18' | Out-Null; if ($LASTEXITCODE -ne 0) {{ throw 'Could not set the cleanup folder owner.' }}");
        }
        foreach (var record in tasks)
        {
            var taskName = record.ExpiryTaskName!;
            var taskScriptPath = Path.Combine(ExpiryTaskDirectory, taskName + ".ps1");
            var legacyTaskScriptPath = Path.Combine(LegacyExpiryTaskDirectory, taskName + ".ps1");
            var temporaryHistoryPath = HistoryFilePath + $".{taskName}.tmp";
            var ruleNames = "@(" + string.Join(",", record.RuleNames.Distinct().Select(name => $"'{Escape(name)}'")) + ")";
            var cleanupScript = string.Join(Environment.NewLine, new[]
            {
                "$ErrorActionPreference = 'Stop'",
                $"$taskName = '{Escape(taskName)}'",
                $"$ruleNames = {ruleNames}",
                $"$historyPath = '{Escape(HistoryFilePath)}'",
                $"$temporaryHistoryPath = '{Escape(temporaryHistoryPath)}'",
                "# Do not remove the history row until every app-created firewall rule is confirmed gone.",
                // Task Scheduler can run this after the user removed a rule manually
                "$failedRules = @()",
                "foreach ($name in $ruleNames) {",
                "    try {",
                "        $rules = @(Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.Name -eq $name })",
                "        if ($rules.Count -gt 0) { $rules | Remove-NetFirewallRule -Confirm:$false -ErrorAction Stop }",
                "        if (@(Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.Name -eq $name }).Count -gt 0) { throw 'Rule still exists after removal.' }",
                "    } catch { $failedRules += \"${name}: $($_.Exception.Message)\" }",
                "}",
                "if ($failedRules.Count -gt 0) { throw ('Some firewall rules could not be removed: ' + ($failedRules -join '; ')) }",
                "$historyMutex = [System.Threading.Mutex]::new($false, 'Global\\LocalPortUtilityHistory')",
                "$historyMutexAcquired = $false",
                "try {",
                "    try { $historyMutexAcquired = $historyMutex.WaitOne([TimeSpan]::FromSeconds(30)) } catch [System.Threading.AbandonedMutexException] { $historyMutexAcquired = $true }",
                "    if (-not $historyMutexAcquired) { throw 'Could not acquire the history file lock.' }",
                "    if (Test-Path -LiteralPath $historyPath) {",
                "        $records = @(Get-Content -LiteralPath $historyPath -Raw | ConvertFrom-Json)",
                "        $remaining = @()",
                "        foreach ($record in $records) {",
                "            $removeRecord = $false",
                "            foreach ($name in @($record.RuleNames)) { if ($ruleNames -contains [string]$name) { $removeRecord = $true; break } }",
                "            if (-not $removeRecord) { $remaining += $record }",
                "        }",
                "        $json = ConvertTo-Json -InputObject $remaining -Depth 10",
                "        Set-Content -LiteralPath $temporaryHistoryPath -Value $json -Encoding UTF8",
                "        Move-Item -LiteralPath $temporaryHistoryPath -Destination $historyPath -Force",
                "    }",
                "} finally { if ($historyMutexAcquired) { $historyMutex.ReleaseMutex() }; $historyMutex.Dispose() }",
                "Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue",
                "Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue"
            });

            commands.Add("$taskScript = @'");
            commands.Add(cleanupScript);
            commands.Add("'@");
            // Disable the old user-writable script before replacing its scheduled action
            commands.Add($"Remove-Item -LiteralPath '{Escape(legacyTaskScriptPath)}' -Force -ErrorAction SilentlyContinue");
            commands.Add($"Remove-Item -LiteralPath '{Escape(taskScriptPath)}' -Force -ErrorAction SilentlyContinue; if (Test-Path -LiteralPath '{Escape(taskScriptPath)}') {{ throw 'Could not replace the cleanup script.' }}");
            commands.Add($"Set-Content -LiteralPath '{Escape(taskScriptPath)}' -Value $taskScript -Encoding Unicode");
            commands.Add($"$action = New-ScheduledTaskAction -Execute '{Escape(PowerShellPath)}' -Argument '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{Escape(taskScriptPath)}\"'");
            var runAt = record.ExpiresAt!.Value <= DateTime.Now ? DateTime.Now.AddSeconds(15) : record.ExpiresAt.Value;
            var formattedRunAt = runAt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            commands.Add($"$trigger = New-ScheduledTaskTrigger -Once -At ([datetime]::ParseExact('{formattedRunAt}', 'yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture))");
            commands.Add("$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest");
            commands.Add("$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 5) -ExecutionTimeLimit (New-TimeSpan -Minutes 10)");
            commands.Add($"Register-ScheduledTask -TaskName '{Escape(taskName)}' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null");
        }
        return commands;
    }

    private static PowerShellExecutionResult ExecutePowerShell(IEnumerable<string> commands, IEnumerable<string>? expected = null)
    {
        var expectedNames = expected?.ToList() ?? new List<string>();
        var errorPath = Path.Combine(Path.GetTempPath(), $"LocalPortUtility-{Guid.NewGuid():N}.log");
        var rollback = expectedNames.Count == 0 ? string.Empty : string.Join(Environment.NewLine, expectedNames.Select(name => $"Remove-NetFirewallRule -Name '{Escape(name)}' -Confirm:$false -ErrorAction SilentlyContinue"));
        var errorCapture = $"$_ | Out-File -LiteralPath '{Escape(errorPath)}' -Encoding UTF8;";
        var script = "$ErrorActionPreference='Stop'; try {" + Environment.NewLine + string.Join(Environment.NewLine, commands) + Environment.NewLine + $"}} catch {{ {errorCapture} {rollback} exit 1 }}";
        return RunElevated(script, "PowerShell reported an error. Check the details below; some changes may already have been applied.", errorPath);
    }

    private static PowerShellExecutionResult RunElevated(string script, string failure, string? errorPath = null)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"LocalPortUtility-{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(scriptPath, script, Encoding.Unicode);
            var quotedScriptPath = $"\"{scriptPath.Replace("\"", "\\\"")}\"";
            using var process = new Process { StartInfo = new ProcessStartInfo { FileName = PowerShellPath, Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File {quotedScriptPath}", UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden } };
            if (!process.Start()) return new(false, failure);
            if (!process.WaitForExit(30000)) { process.Kill(true); return new(false, "PowerShell timed out after 30 seconds."); }
            if (process.ExitCode == 0) return new(true, string.Empty);

            var details = errorPath is not null && File.Exists(errorPath) ? File.ReadAllText(errorPath).Trim() : string.Empty;
            return new(false, string.IsNullOrWhiteSpace(details) ? failure : $"{failure}\n\n{details}");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223) { return new(false, "Administrator approval was canceled. No firewall changes were recorded."); }
        catch (Exception exception) { return new(false, exception.Message); }
        finally
        {
            try { if (File.Exists(scriptPath)) File.Delete(scriptPath); }
            catch (IOException) { }
            if (errorPath is not null)
            {
                try { if (File.Exists(errorPath)) File.Delete(errorPath); }
                catch (IOException) { }
            }
        }
    }

    private static ConnectivityTestResult TestConnectivity(string target, int port) => RunQuery($"$r=Test-NetConnection -ComputerName '{Escape(target)}' -Port {port} -WarningAction SilentlyContinue; if ($r.TcpTestSucceeded) {{ exit 0 }} else {{ exit 1 }}", 10000) ? new(true, string.Empty) : new(false, $"{target}:{port} is closed or unreachable.");
    private static bool RunQuery(string script, int timeout) { try { var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script)); using var process = Process.Start(new ProcessStartInfo { FileName = PowerShellPath, Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}", UseShellExecute = false, CreateNoWindow = true }); if (process is null) return false; if (!process.WaitForExit(timeout)) { process.Kill(true); return false; } return process.ExitCode == 0; } catch { return false; } }

    private static FirewallTestResult TestFirewallRule(int port)
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"LocalPortUtility-Firewall-{Guid.NewGuid():N}.json");
        var script = $"$results=@('TCP','UDP') | ForEach-Object {{ $protocol=$_; $filters=@(Get-NetFirewallPortFilter -ErrorAction Stop | Where-Object LocalPort -eq {port} | Where-Object Protocol -eq $protocol); $matches=@($filters | ForEach-Object {{ $filter=$_; $rules=@(Get-NetFirewallRule -AssociatedNetFirewallPortFilter $filter -ErrorAction Stop | Where-Object Direction -eq 'Inbound'); foreach($rule in $rules) {{ [PSCustomObject]@{{Name=$rule.DisplayName;Enabled=[string]$rule.Enabled;Action=[string]$rule.Action;Protocol=$protocol;Port=[string]$filter.LocalPort}} }} }}); $allowed=@($matches | Where-Object {{ $_.Enabled -eq 'True' -and $_.Action -eq 'Allow' }}); [PSCustomObject]@{{Protocol=$protocol;RuleFound=($matches.Count -gt 0);AllowsTraffic=($allowed.Count -gt 0);Rules=@($matches)}} }}; $results|ConvertTo-Json -Compress -Depth 5 | Set-Content -LiteralPath '{Escape(outputPath)}' -Encoding UTF8";
        try
        {
            var result = RunElevated(script, "PowerShell could not inspect Windows Firewall.", Path.Combine(Path.GetTempPath(), $"LocalPortUtility-FirewallError-{Guid.NewGuid():N}.log"));
            if (!result.Succeeded) return new(false, false, false, result.Error, new());
            if (!File.Exists(outputPath)) return new(false, false, false, "PowerShell returned no firewall inspection data.", new());
            var output = File.ReadAllText(outputPath);
            using var document = JsonDocument.Parse(output);
            var protocols = document.RootElement.EnumerateArray().Select(item => new FirewallProtocolResult(item.GetProperty("Protocol").GetString() ?? "", item.GetProperty("RuleFound").GetBoolean(), item.GetProperty("AllowsTraffic").GetBoolean(), FormatFirewallRules(item.GetProperty("Rules")))).ToList();
            return new(true, protocols.Any(item => item.RuleFound), protocols.Any(item => item.AllowsTraffic), string.Empty, protocols);
        }
        catch (Exception exception) { return new(false, false, false, exception.Message, new()); }
        finally
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); }
            catch (IOException) { }
        }
    }

    private static string FormatFirewallRules(JsonElement rules)
    {
        if (rules.ValueKind == JsonValueKind.Null || (rules.ValueKind == JsonValueKind.Array && rules.GetArrayLength() == 0)) return "no matching inbound rule";
        var items = rules.ValueKind == JsonValueKind.Array ? rules.EnumerateArray() : new[] { rules }.AsEnumerable();
        return string.Join("; ", items.Select(item => $"{item.GetProperty("Name").GetString()}: {item.GetProperty("Action").GetString()}, {item.GetProperty("Enabled").GetString()}, protocol {item.GetProperty("Protocol").GetString()}, port {item.GetProperty("Port").GetString()}"));
    }
    private static FirewallStatusResult GetExistingFirewallRuleNames(IEnumerable<string> names)
    {
        var output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var script = "$ErrorActionPreference='Stop'; try { $n=@(" + string.Join(",", names.Select(name => $"'{Escape(name)}'")) + "); foreach($x in $n){Get-NetFirewallRule -Name $x -ErrorAction Stop | ForEach-Object { Write-Output $_.Name }}; exit 0 } catch { exit 1 }";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            using var process = Process.Start(new ProcessStartInfo { FileName = PowerShellPath, Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            if (process is null) return new(false, output);
            if (!process.WaitForExit(10000)) { process.Kill(true); return new(false, output); }
            foreach (var line in process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) output.Add(line);
            return new(true, output);
        }
        catch { return new(false, output); }
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryFilePath)) return;
            foreach (var item in JsonSerializer.Deserialize<List<HistoryRecord>>(File.ReadAllText(HistoryFilePath)) ?? new())
            {
                if (item is null) continue;
                item.RuleName ??= string.Empty;
                item.RuleNames ??= new();
                item.DisplayNames ??= new();
                item.OpenedPortsText ??= "Port details unavailable";
                item.Status ??= "Active";
                _history.Add(item);
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private bool SaveHistory()
    {
        var temporaryPath = HistoryFilePath + ".tmp";
        // PowerShell cleanup and the app both replace this file so they share one lock
        using var historyMutex = new System.Threading.Mutex(false, @"Global\LocalPortUtilityHistory");
        var mutexAcquired = false;
        try
        {
            try { mutexAcquired = historyMutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (System.Threading.AbandonedMutexException) { mutexAcquired = true; }
            if (!mutexAcquired) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(HistoryFilePath)!);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_history, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, HistoryFilePath, true);
            return true;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            if (mutexAcquired) historyMutex.ReleaseMutex();
        }
        return false;
    }
    private void UpdateHistoryEmptyState() => HistoryEmptyState.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    private async Task ShowDialogAsync(string title, string message)
    {
        if (_isClosing) return;
        var dialog = new ContentDialog { Title = title, Content = message, CloseButtonText = "Close", XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
    }
    private void SetStatus(string message, InfoBarSeverity severity) { StatusInfoBar.Message = message; StatusInfoBar.Severity = severity; StatusInfoBar.IsOpen = true; }
    private static bool IsPrivateIpv4(string address) => IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && (ip.GetAddressBytes()[0] == 10 || ip.GetAddressBytes()[0] == 192 && ip.GetAddressBytes()[1] == 168 || ip.GetAddressBytes()[0] == 172 && ip.GetAddressBytes()[1] is >= 16 and <= 31);

    private sealed record PortEntryControls(ComboBox Type, TextBox Start, TextBox End, ComboBox Protocol);
    private sealed record FirewallRuleSpec(string Name, string DisplayName, int Start, int? End, string Protocol);
    private sealed record Ipv4TargetOption(string Address, string Label);
    private sealed record ConnectivityTestResult(bool IsOpen, string Error);
    private sealed record FirewallTestResult(bool QuerySucceeded, bool RuleFound, bool AllowsTraffic, string Details, List<FirewallProtocolResult> Protocols);
    private sealed record FirewallProtocolResult(string Protocol, bool RuleFound, bool AllowsTraffic, string Details);
    private sealed record FirewallStatusResult(bool Succeeded, HashSet<string> ExistingNames);
    private sealed record PowerShellExecutionResult(bool Succeeded, string Error);
    private sealed record AppSettings(int ThemeIndex, bool UseTransparency);
}

public sealed class HistoryRecord : INotifyPropertyChanged
{
    public string RuleName { get; set; } = string.Empty;
    public List<string> RuleNames { get; set; } = new();
    public List<string> DisplayNames { get; set; } = new();
    public string OpenedPortsText { get; set; } = "Port details unavailable";
    private string _status = "Active";
    public string Status { get => _status; set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); } }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? ExpiryTaskName { get; set; }
    public int ExpiryTaskVersion { get; set; }
    [JsonIgnore]
    public string RuleLifetimeLabel => ExpiresAt is DateTime expiry
        ? $"AUTO-DELETE · {expiry.ToLocalTime():g}"
        : "No auto-delete scheduled";
    public event PropertyChangedEventHandler? PropertyChanged;
}
