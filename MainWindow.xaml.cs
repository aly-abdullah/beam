using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Beam.Core;
using QRCoder;
using Forms = System.Windows.Forms;

namespace Beam;

public partial class MainWindow : Window
{
    enum Phase { Idle, Starting, Live, Stopping }

    sealed class ProjectRun
    {
        public string? Folder;
        public AngularInfo? Info;
        public TunnelSession? Session;
        public CancellationTokenSource? Cts;
        public Phase Phase = Phase.Idle;
        public DateTime LiveSince;
        public readonly StringBuilder Log = new();
        public readonly Dictionary<Step, (StepState State, string Detail)> Steps = [];

        public ProjectRun() => ResetSteps();

        public string? Url => Session?.PublicUrl;

        public bool Busy => Phase != Phase.Idle;

        public string Title => Info is not null ? Label(Info.Root, Info.Name)
            : Folder is null ? Loc.I["NewProject"]
            : Label(Folder, null);

        public void ResetSteps()
        {
            foreach (var step in Enum.GetValues<Step>())
                Steps[step] = (StepState.Pending, "");
        }

        public bool Is(string folder) =>
            string.Equals((Info?.Root ?? Folder)?.TrimEnd('\\', '/'), folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "frontend", "front-end", "front", "web", "client", "app", "ui", "src", "site", "website", "portal", "angular",
    };

    static readonly Color[] TilePalette =
    [
        Color.FromRgb(0xFF, 0x9A, 0x55),
        Color.FromRgb(0xA8, 0x94, 0xFF),
        Color.FromRgb(0x4F, 0xD8, 0xB0),
        Color.FromRgb(0xFF, 0x86, 0xA8),
        Color.FromRgb(0x6F, 0xB7, 0xFF),
        Color.FromRgb(0xF2, 0xC9, 0x5C),
    ];

    static string Label(string folder, string? name)
    {
        var trimmed = folder.TrimEnd('\\', '/');
        var leaf = System.IO.Path.GetFileName(trimmed);
        name ??= leaf;
        if (!GenericNames.Contains(name) && !GenericNames.Contains(leaf))
            return name;
        var parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(trimmed) ?? "");
        return string.IsNullOrEmpty(parent) ? name : $"{parent} · {name}";
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    readonly Settings _settings = Settings.Load();
    readonly Dictionary<Step, StepCard> _cards = [];
    readonly List<Rectangle> _links = [];
    readonly List<ProjectRun> _runs = [];
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Forms.NotifyIcon _tray = new();

    ProjectRun _current;
    string? _shownUrl;
    bool _progressRunning;
    bool _trayHintShown;
    bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        Loc.I.SetArabic(_settings.Arabic);
        Loc.I.PropertyChanged += (_, _) => Refresh();

        BuildSteps();
        _clock.Tick += (_, _) => UpdateElapsed();
        StateChanged += (_, _) => OnWindowState();
        HeroCard.SizeChanged += (_, e) => HeroLayer.Clip = new RectangleGeometry(new Rect(e.NewSize), 19, 19);
        SetupTray();

        var saved = _settings.OpenTabs ?? _settings.Recent.Take(1).ToList();
        foreach (var folder in saved.Where(Directory.Exists))
        {
            if (_runs.Any(r => r.Is(folder)))
                continue;
            Load(AddRun(), folder, false);
        }
        if (_runs.Count == 0)
            AddRun();

        _current = (_settings.SelectedTab is { } selected ? _runs.FirstOrDefault(r => r.Is(selected)) : null) ?? _runs[0];
        ShowRun(_current);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var dark = 1;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        var round = 2;
        DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
        var border = 0x004A2624;
        DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
    }

    void OnWindowState()
    {
        var maximized = WindowState == WindowState.Maximized;
        RootGrid.Margin = maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = maximized ? "" : "";
    }

    void BuildSteps()
    {
        var steps = Enum.GetValues<Step>();
        for (var i = 0; i < steps.Length; i++)
        {
            StepsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var card = new StepCard(steps[i]);
            _cards[steps[i]] = card;
            Grid.SetColumn(card.Root, i * 2);
            StepsGrid.Children.Add(card.Root);

            if (i == steps.Length - 1)
                break;

            StepsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var link = new Rectangle
            {
                Height = 1.5,
                MinWidth = 12,
                Margin = new Thickness(12, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = Res("Stroke"),
            };
            Grid.SetColumn(link, i * 2 + 1);
            StepsGrid.Children.Add(link);
            _links.Add(link);
        }
    }

    void SetStep(Step step, StepState state, string detail)
    {
        _cards[step].Set(state, detail);
        var steps = Enum.GetValues<Step>();
        for (var i = 0; i < _links.Count; i++)
            _links[i].Fill = Res(_cards[steps[i]].State == StepState.Done ? "Success" : "Stroke");
    }

    void SetupTray()
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Beam.ico")).Stream;
        _tray.Icon = new System.Drawing.Icon(stream);
        _tray.Text = "Beam";
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => Reveal();
        _tray.ContextMenuStrip = new Forms.ContextMenuStrip();
        _tray.ContextMenuStrip.Opening += (_, _) => BuildTrayMenu();
    }

    void BuildTrayMenu()
    {
        var menu = _tray.ContextMenuStrip!;
        menu.Items.Clear();
        menu.RightToLeft = Loc.I.Arabic ? Forms.RightToLeft.Yes : Forms.RightToLeft.No;
        menu.Items.Add(Loc.I["TrayOpen"], null, (_, _) => Reveal());

        var live = _runs.Where(r => r.Phase == Phase.Live).ToList();
        if (live.Count > 0)
            menu.Items.Add(new Forms.ToolStripSeparator());

        foreach (var run in live)
        {
            var item = new Forms.ToolStripMenuItem(run.Title);
            item.DropDownItems.Add(Loc.I["TrayCopy"], null, (_, _) => CopyLink(run));
            item.DropDownItems.Add(Loc.I["Open"], null, (_, _) => OpenLink(run));
            item.DropDownItems.Add(Loc.I["TrayStop"], null, async (_, _) => await StopAsync(run));
            menu.Items.Add(item);
        }

        if (live.Count > 1)
            menu.Items.Add(Loc.I["StopAll"], null, async (_, _) => await StopAllAsync());

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.I["TrayExit"], null, async (_, _) => await ExitAsync());
    }

    public void Reveal()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    void Load(ProjectRun run, string folder, bool remember)
    {
        run.Folder = folder;
        try
        {
            run.Info = AngularProject.Inspect(folder);
        }
        catch (Exception ex)
        {
            run.Info = null;
            AppendLog(run, ex.Message);
        }

        if (run.Info is not null && remember)
            _settings.Remember(folder);
    }

    void OpenProject(string folder)
    {
        var existing = _runs.FirstOrDefault(r => r.Is(folder));
        if (existing is not null)
        {
            ShowRun(existing);
            return;
        }

        var target = _current.Busy ? AddRun() : _current;
        Load(target, folder, true);
        ShowRun(target);
    }

    ProjectRun AddRun()
    {
        var run = new ProjectRun();
        _runs.Add(run);
        return run;
    }

    void RemoveRun(ProjectRun run)
    {
        if (run.Busy || _runs.Count < 2)
            return;
        var index = _runs.IndexOf(run);
        _runs.Remove(run);
        if (_current == run)
        {
            ShowRun(_runs[Math.Min(index, _runs.Count - 1)]);
        }
        else
        {
            SaveTabs();
            Refresh();
        }
    }

    void SaveTabs()
    {
        _settings.OpenTabs = _runs
            .Select(r => r.Info?.Root ?? r.Folder)
            .OfType<string>()
            .ToList();
        _settings.SelectedTab = _current.Info?.Root ?? _current.Folder;
        _settings.Save();
    }

    void ShowRun(ProjectRun run)
    {
        if (_current != run && IsLoaded)
            PlayContentTransition();
        _current = run;
        foreach (var (step, value) in run.Steps)
            SetStep(step, value.State, value.Detail);
        LogBox.Text = run.Log.ToString();
        LogBox.ScrollToEnd();
        _shownUrl = null;
        SaveTabs();
        Refresh();
    }

    void PlayContentTransition()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(220);
        ProjectView.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        ContentShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, duration) { EasingFunction = ease });
    }

    void Refresh()
    {
        var loc = Loc.I;
        var run = _current;
        FlowDirection = loc.Arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        ProjectsLabel.Text = loc["Projects"].ToUpperInvariant();
        RecentLabel.Text = loc["Recent"].ToUpperInvariant();
        LangText.Text = loc.Arabic ? "English" : "العربية";
        AddButton.ToolTip = loc["AddProject"];
        OpenFolderButton.ToolTip = loc["OpenFolder"];
        ChangeButton.Content = loc["Change"];
        CopyLogButton.ToolTip = loc["CopyLog"];
        ClearLogButton.ToolTip = loc["ClearLogs"];
        LogsTitle.Text = loc["Logs"];
        LogEmpty.Text = loc["LogEmpty"];
        OpenText.Text = loc["Open"];
        if (CopyText.Text != loc["Copied"])
            CopyText.Text = loc["Copy"];
        LinkHint.Text = loc["LinkHint"];
        LiveBadgeText.Text = loc["Live"];
        EmptyTitle.Text = loc["EmptyTitle"];
        EmptyBody.Text = loc["EmptyBody"];
        EmptyButtonText.Text = loc["ChooseFolder"];

        BuildSidebar();
        UpdateGlobalStatus();

        var empty = run.Folder is null;
        EmptyView.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ProjectView.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        BuildEmptyRecent();

        if (!empty)
        {
            ProjectName.Text = run.Title;
            ProjectPath.Text = run.Info?.Root ?? run.Folder;
            ProjectPath.ToolTip = ProjectPath.Text;
            ChangeButton.IsEnabled = !run.Busy;
            BuildChips(run);
            UpdateActions(run);
            UpdateHero(run);
        }

        foreach (var card in _cards.Values)
            card.Translate();

        LogEmpty.Visibility = run.Log.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateTray();
        UpdateElapsed();
    }

    void BuildSidebar()
    {
        ProjectList.Children.Clear();
        foreach (var run in _runs.Where(r => r.Folder is not null || r == _current))
            ProjectList.Children.Add(SidebarItem(run));

        RecentList.Children.Clear();
        foreach (var path in _settings.Recent.Where(p => Directory.Exists(p) && !_runs.Any(r => r.Is(p))).Take(5))
            RecentList.Children.Add(RecentItem(path));
        RecentSection.Visibility = RecentList.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    Border SidebarItem(ProjectRun run)
    {
        var selected = run == _current;
        var title = run.Title;

        var hue = TileColor(run.Info?.Root ?? run.Folder ?? title);
        var tile = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(38, hue.R, hue.G, hue.B)),
            Child = new TextBlock
            {
                Text = run.Folder is null ? "+" : title[..1].ToUpperInvariant(),
                FontFamily = (FontFamily)FindResource("DisplayFont"),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(hue),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var texts = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Res(selected ? "Text" : "Muted"),
        });
        texts.Children.Add(new TextBlock
        {
            Text = Subtitle(run),
            FontSize = 11.5,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Res(run.Phase == Phase.Live ? "Success" : run.Busy ? "Accent" : "Faint"),
        });

        var trailing = new Grid { Width = 26, VerticalAlignment = VerticalAlignment.Center };
        Button? remove = null;
        if (run.Phase == Phase.Live)
        {
            trailing.Children.Add(PulseDot(Res("Success"), true));
        }
        else if (run.Busy)
        {
            trailing.Children.Add(PulseDot(Res("Accent"), true));
        }
        else if (_runs.Count > 1)
        {
            remove = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Content = "",
                FontSize = 9,
                Width = 24,
                Height = 24,
                ToolTip = Loc.I["Remove"],
                Visibility = Visibility.Hidden,
            };
            remove.Click += (_, _) => RemoveRun(run);
            trailing.Children.Add(remove);
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(tile);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        Grid.SetColumn(trailing, 2);
        grid.Children.Add(trailing);

        if (selected)
        {
            var bar = new Border
            {
                Width = 3,
                Height = 18,
                CornerRadius = new CornerRadius(1.5),
                Background = Res("AccentGradient"),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(-9, 0, 0, 0),
            };
            Grid.SetColumnSpan(bar, 3);
            grid.Children.Add(bar);
        }

        var idleBackground = selected ? Res("SurfaceHi") : Brushes.Transparent;
        var item = new Border
        {
            Background = idleBackground,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(9, 8, 6, 8),
            Margin = new Thickness(0, 0, 0, 4),
            Cursor = Cursors.Hand,
            ToolTip = run.Info?.Root ?? run.Folder,
            Child = grid,
        };
        item.MouseEnter += (_, _) =>
        {
            if (!selected)
                item.Background = new SolidColorBrush(Color.FromArgb(14, 255, 255, 255));
            if (remove is not null)
                remove.Visibility = Visibility.Visible;
        };
        item.MouseLeave += (_, _) =>
        {
            item.Background = idleBackground;
            if (remove is not null)
                remove.Visibility = Visibility.Hidden;
        };
        item.MouseLeftButtonUp += (_, _) =>
        {
            if (run != _current)
                ShowRun(run);
        };
        return item;
    }

    Border RecentItem(string path)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Icon"),
            Text = "",
            FontSize = 12,
            Foreground = Res("Faint"),
        });
        row.Children.Add(new TextBlock
        {
            Text = Label(path, null),
            FontSize = 12.5,
            Margin = new Thickness(11, 0, 0, 0),
            Foreground = Res("Muted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var item = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 2),
            Cursor = Cursors.Hand,
            ToolTip = path,
            Child = row,
        };
        item.MouseEnter += (_, _) => item.Background = new SolidColorBrush(Color.FromArgb(14, 255, 255, 255));
        item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
        item.MouseLeftButtonUp += (_, _) => OpenProject(path);
        return item;
    }

    void BuildEmptyRecent()
    {
        EmptyRecent.Children.Clear();
        foreach (var path in _settings.Recent.Where(p => Directory.Exists(p) && !_runs.Any(r => r.Is(p))).Take(4))
        {
            var button = new Button
            {
                Style = (Style)FindResource("SoftButton"),
                Margin = new Thickness(4),
                ToolTip = path,
                FontWeight = FontWeights.Normal,
                FontSize = 12.5,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock { Style = (Style)FindResource("Icon"), Text = "", FontSize = 12, Foreground = Res("Faint") },
                        new TextBlock { Text = Label(path, null), Margin = new Thickness(9, 0, 0, 0), Foreground = Res("Muted"), VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
            button.Click += (_, _) => OpenProject(path);
            EmptyRecent.Children.Add(button);
        }
    }

    string Subtitle(ProjectRun run)
    {
        var loc = Loc.I;
        if (run.Folder is null)
            return loc["SubEmpty"];
        if (run.Info is null)
            return loc["SubNotAngular"];
        var port = run.Session?.Port > 0 ? run.Session.Port : run.Info.Port;
        return run.Phase switch
        {
            Phase.Live => loc.F("SubLive", port),
            Phase.Starting => loc["Starting"],
            Phase.Stopping => loc["Stopping"],
            _ => loc.F("SubIdle", port),
        };
    }

    void BuildChips(ProjectRun run)
    {
        var loc = Loc.I;
        Chips.Children.Clear();
        if (run.Info is not { } info)
        {
            Chips.Children.Add(Chip("", loc["NotAngular"], Res("Danger")));
            return;
        }
        if (!string.IsNullOrEmpty(info.Version))
            Chips.Children.Add(Chip("", $"{loc["Angular"]} {info.Version}", null));
        Chips.Children.Add(Chip("",$"{loc["Port"]} {(run.Session?.Port > 0 ? run.Session.Port : info.Port)}", null));
        Chips.Children.Add(info.HostsReady
            ? Chip("", loc["HostsOk"], Res("Success"))
            : Chip("", loc["HostsFix"], Res("Accent")));
        if (!info.HasNodeModules)
            Chips.Children.Add(Chip("", loc["NoModules"], Res("Accent")));
    }

    Border Chip(string icon, string text, Brush? accent)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Icon"),
            Text = icon,
            FontSize = 11,
            Foreground = accent ?? Res("Faint"),
        });
        row.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = accent ?? Res("Muted"),
        });
        return new Border
        {
            Background = accent is null ? Res("SurfaceHi") : new SolidColorBrush(Color.FromArgb(24, ((SolidColorBrush)accent).Color.R, ((SolidColorBrush)accent).Color.G, ((SolidColorBrush)accent).Color.B)),
            BorderBrush = accent is null ? Res("Stroke") : new SolidColorBrush(Color.FromArgb(80, ((SolidColorBrush)accent).Color.R, ((SolidColorBrush)accent).Color.G, ((SolidColorBrush)accent).Color.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 5, 11, 6),
            Margin = new Thickness(0, 0, 8, 8),
            Child = row,
        };
    }

    void UpdateActions(ProjectRun run)
    {
        var loc = Loc.I;
        GoLiveText.Text = loc["GoLive"];
        GoLiveButton.Visibility = run.Phase == Phase.Idle && run.Info is not null ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = run.Busy ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = run.Phase != Phase.Stopping;
        StopText.Text = run.Phase == Phase.Stopping ? loc["Stopping"] : loc["Stop"];
    }

    void UpdateHero(ProjectRun run)
    {
        var loc = Loc.I;
        var live = run.Phase == Phase.Live && run.Url is not null;
        HeroLive.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        HeroIdle.Visibility = live ? Visibility.Collapsed : Visibility.Visible;

        if (live)
        {
            HeroCard.Background = Res("LiveGradient");
            HeroCard.BorderBrush = new SolidColorBrush(Color.FromArgb(70, 0x3D, 0xD6, 0x8C));
            if (LiveDotHost.Children.Count == 0)
                LiveDotHost.Children.Add(PulseDot(Res("Success"), true));
            if (_shownUrl != run.Url)
                ShowLink(run.Url!);
            SetProgress(false);
            HeroBeam.Opacity = 1;
            return;
        }

        _shownUrl = null;
        HeroCard.Background = run.Info is null ? Res("Surface") : Res("HeroGradient");
        HeroCard.BorderBrush = Res("Stroke");
        HeroTitle.Foreground = Res(run.Info is null ? "Danger" : "Text");

        if (run.Info is null)
        {
            HeroTitle.Text = loc["NotAngular"];
            HeroBody.Text = loc["PickPrompt"];
        }
        else if (run.Phase == Phase.Starting)
        {
            HeroTitle.Text = loc["HeroStartingTitle"];
            HeroBody.Text = loc["HeroStartingBody"];
        }
        else if (run.Phase == Phase.Stopping)
        {
            HeroTitle.Text = loc["Stopping"];
            HeroBody.Text = "";
        }
        else
        {
            HeroTitle.Text = loc["HeroIdleTitle"];
            HeroBody.Text = loc["HeroIdleBody"];
        }
        SetProgress(run.Phase is Phase.Starting or Phase.Stopping);
        if (!_progressRunning)
            HeroBeam.Opacity = run.Info is null ? 0 : 0.5;
    }

    void SetProgress(bool on)
    {
        ProgressTrack.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on == _progressRunning)
            return;
        _progressRunning = on;
        ProgressShift.BeginAnimation(TranslateTransform.XProperty, on
            ? new DoubleAnimation(-140, 440, TimeSpan.FromSeconds(1.3))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            }
            : null);
        HeroBeam.BeginAnimation(OpacityProperty, on
            ? new DoubleAnimation(0.35, 1, TimeSpan.FromSeconds(0.9))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            }
            : null);
    }

    void UpdateGlobalStatus()
    {
        var loc = Loc.I;
        var liveCount = _runs.Count(r => r.Phase == Phase.Live);
        var starting = _runs.Any(r => r.Phase == Phase.Starting);
        GlobalDotHost.Children.Clear();
        if (liveCount > 0)
        {
            GlobalPill.Background = Res("SuccessSoft");
            GlobalDotHost.Children.Add(PulseDot(Res("Success"), true));
            GlobalText.Foreground = Res("Success");
            GlobalText.Text = liveCount > 1 ? loc.F("LiveCount", liveCount) : loc["Live"];
        }
        else if (starting)
        {
            GlobalPill.Background = Res("AccentSoft");
            GlobalDotHost.Children.Add(PulseDot(Res("Accent"), true));
            GlobalText.Foreground = Res("Accent");
            GlobalText.Text = loc["Starting"];
        }
        else
        {
            GlobalPill.Background = Brushes.Transparent;
            GlobalDotHost.Children.Add(PulseDot(Res("Faint"), false));
            GlobalText.Foreground = Res("Faint");
            GlobalText.Text = loc["AllIdle"];
        }
    }

    static Grid PulseDot(Brush brush, bool pulse)
    {
        var host = new Grid { Width = 8, Height = 8 };
        if (pulse)
        {
            var scale = new ScaleTransform(1, 1);
            var ring = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = brush,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale,
            };
            var duration = TimeSpan.FromSeconds(1.6);
            var grow = new DoubleAnimation(1, 2.6, duration) { RepeatBehavior = RepeatBehavior.Forever };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            ring.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 0, duration) { RepeatBehavior = RepeatBehavior.Forever });
            host.Children.Add(ring);
        }
        host.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = brush });
        return host;
    }

    static Color TileColor(string key)
    {
        var hash = 2166136261u;
        foreach (var c in key.ToLowerInvariant())
            hash = (hash ^ c) * 16777619u;
        return TilePalette[hash % (uint)TilePalette.Length];
    }

    Brush Res(string key) => (Brush)FindResource(key);

    void UpdateTray()
    {
        var live = _runs.Where(r => r.Phase == Phase.Live).ToList();
        var text = live.Count switch
        {
            0 => "Beam",
            1 => "Beam · " + (live[0].Url ?? "").Replace("https://", ""),
            _ => "Beam · " + Loc.I.F("LiveCount", live.Count),
        };
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    async void OnMain(object sender, RoutedEventArgs e)
    {
        var run = _current;
        if (run.Phase == Phase.Idle)
            await StartAsync(run);
        else if (run.Phase is Phase.Live or Phase.Starting)
            await StopAsync(run);
    }

    void ResetCards(ProjectRun run)
    {
        run.ResetSteps();
        if (run != _current)
            return;
        foreach (var step in _cards.Keys.ToList())
            SetStep(step, StepState.Pending, "");
    }

    async Task StartAsync(ProjectRun run)
    {
        if (run.Info is null)
            return;

        run.Info = AngularProject.Inspect(run.Info.Root) ?? run.Info;
        ResetCards(run);

        run.Cts = new CancellationTokenSource();
        var session = new TunnelSession();
        run.Session = session;
        session.Log += line => Dispatcher.BeginInvoke(() => AppendLog(run, line));
        session.StepChanged += update => Dispatcher.BeginInvoke(() =>
        {
            run.Steps[update.Step] = (update.State, update.Detail);
            if (run == _current)
                SetStep(update.Step, update.State, update.Detail);
        });
        session.Ended += message => Dispatcher.BeginInvoke(async () =>
        {
            if (run.Phase != Phase.Live)
                return;
            AppendLog(run, message);
            ShowBalloon($"{run.Title}: {message}");
            await StopAsync(run);
        });

        run.Phase = Phase.Starting;
        Refresh();

        try
        {
            await session.StartAsync(run.Info, run.Cts.Token);
            run.Info = AngularProject.Inspect(run.Info.Root) ?? run.Info;
            run.Phase = Phase.Live;
            run.LiveSince = DateTime.Now;
            _clock.Start();
            Refresh();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (run.Phase == Phase.Stopping)
                return;
            foreach (var (step, value) in run.Steps.ToList())
            {
                if (value.State != StepState.Active)
                    continue;
                run.Steps[step] = (StepState.Failed, Loc.I["Failed"]);
                if (run == _current)
                    SetStep(step, StepState.Failed, Loc.I["Failed"]);
            }
            AppendLog(run, ex.Message);
            session.Dispose();
            run.Session = null;
            run.Phase = Phase.Idle;
            Refresh();
        }
    }

    async Task StopAsync(ProjectRun run)
    {
        if (run.Session is not { } session || run.Phase is Phase.Stopping or Phase.Idle)
            return;

        run.Phase = Phase.Stopping;
        Refresh();
        run.Cts?.Cancel();
        await Task.Run(session.Dispose);
        run.Session = null;
        ResetCards(run);
        run.Phase = Phase.Idle;
        if (!_runs.Any(r => r.Phase == Phase.Live))
            _clock.Stop();
        Refresh();
    }

    Task StopAllAsync() => Task.WhenAll(_runs.ToList().Select(StopAsync));

    void ShowLink(string url)
    {
        _shownUrl = url;
        LinkText.Text = url;
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8, [10, 11, 20], [255, 255, 255], false);
        var image = new BitmapImage();
        using (var stream = new MemoryStream(png))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
        }
        image.Freeze();
        QrImage.Source = image;
        HeroLive.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360)));
    }

    void UpdateElapsed()
    {
        if (_current.Phase != Phase.Live)
        {
            ElapsedText.Text = "";
            return;
        }
        var span = DateTime.Now - _current.LiveSince;
        ElapsedText.Text = Loc.I.F("Elapsed", span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss"));
    }

    void CopyLink(ProjectRun run)
    {
        if (run.Url is not { } url)
            return;
        try
        {
            Clipboard.SetText(url);
            if (run != _current)
                return;
            CopyText.Text = Loc.I["Copied"];
            CopyIcon.Text = "";
            var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            reset.Tick += (_, _) =>
            {
                CopyText.Text = Loc.I["Copy"];
                CopyIcon.Text = "";
                reset.Stop();
            };
            reset.Start();
        }
        catch
        {
        }
    }

    static void OpenLink(ProjectRun run)
    {
        if (run.Url is { } url)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    void OnCopy(object sender, RoutedEventArgs e) => CopyLink(_current);

    void OnOpenLink(object sender, RoutedEventArgs e) => OpenLink(_current);

    void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var folder = _current.Info?.Root ?? _current.Folder;
        if (folder is not null && Directory.Exists(folder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    string? PickFolder()
    {
        var start = _current.Info?.Root ?? _current.Folder ?? _settings.Recent.FirstOrDefault(Directory.Exists);
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.I["PickPrompt"],
            InitialDirectory = start is null ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : System.IO.Path.GetDirectoryName(start) ?? start,
        };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (PickFolder() is { } folder)
            OpenProject(folder);
    }

    void OnAddProject(object sender, RoutedEventArgs e)
    {
        if (PickFolder() is not { } folder)
            return;

        var existing = _runs.FirstOrDefault(r => r.Is(folder));
        if (existing is not null)
        {
            ShowRun(existing);
            return;
        }

        var target = _current.Folder is null && !_current.Busy ? _current : AddRun();
        Load(target, folder, true);
        ShowRun(target);
    }

    void AppendLog(ProjectRun run, string line)
    {
        run.Log.AppendLine(line);
        if (run.Log.Length > 60000)
            run.Log.Remove(0, run.Log.Length - 40000);
        if (run != _current)
            return;
        LogBox.Text = run.Log.ToString();
        LogBox.ScrollToEnd();
        LogEmpty.Visibility = Visibility.Collapsed;
    }

    void OnCopyLog(object sender, RoutedEventArgs e)
    {
        if (_current.Log.Length == 0)
            return;
        try
        {
            Clipboard.SetText(_current.Log.ToString());
        }
        catch
        {
        }
    }

    void OnClearLogs(object sender, RoutedEventArgs e)
    {
        _current.Log.Clear();
        LogBox.Clear();
        LogEmpty.Visibility = Visibility.Visible;
    }

    void OnToggleLanguage(object sender, RoutedEventArgs e)
    {
        _settings.Arabic = !Loc.I.Arabic;
        _settings.Save();
        Loc.I.SetArabic(_settings.Arabic);
    }

    void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    async void OnClose(object sender, RoutedEventArgs e)
    {
        if (_runs.Any(r => r.Phase is Phase.Live or Phase.Starting))
        {
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                ShowBalloon(Loc.I["StillRunning"]);
            }
            return;
        }
        await ExitAsync();
    }

    void ShowBalloon(string text)
    {
        _tray.BalloonTipTitle = "Beam";
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(3000);
    }

    async Task ExitAsync()
    {
        if (_exiting)
            return;
        _exiting = true;
        await StopAllAsync();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Current.Shutdown();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            OnClose(this, new RoutedEventArgs());
        }
        base.OnClosing(e);
    }

    sealed class StepCard
    {
        static readonly Dictionary<Step, string> Glyphs = new()
        {
            [Step.Cloudflared] = "\uE753",
            [Step.Hosts] = "\uEA18",
            [Step.Server] = "\uE7F4",
            [Step.Tunnel] = "\uE774",
        };

        readonly Step _step;
        readonly Border _circle = new() { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(1) };
        readonly TextBlock _glyph = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        readonly System.Windows.Shapes.Path _spinner;
        readonly TextBlock _title = new() { FontSize = 12.5, FontWeight = FontWeights.SemiBold };
        readonly TextBlock _detail = new() { FontSize = 11, MaxWidth = 124, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };

        public StackPanel Root { get; } = new() { Orientation = Orientation.Horizontal };
        public StepState State { get; private set; } = StepState.Pending;

        public StepCard(Step step)
        {
            _step = step;
            var app = Application.Current;
            _glyph.FontFamily = (FontFamily)app.FindResource("IconFont");
            _glyph.Text = Glyphs[step];
            _spinner = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 15,1 A 14,14 0 0 1 29,15"),
                StrokeThickness = 2,
                Stroke = (Brush)app.FindResource("Accent"),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 30,
                Height = 30,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(),
            };

            var icon = new Grid { Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Center };
            _circle.Child = _glyph;
            icon.Children.Add(_circle);
            icon.Children.Add(_spinner);

            var texts = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(_title);
            texts.Children.Add(_detail);

            Root.Children.Add(icon);
            Root.Children.Add(texts);

            Translate();
            Set(StepState.Pending, "");
        }

        public void Translate() => _title.Text = Loc.I["Step" + _step];

        public void Set(StepState state, string detail)
        {
            State = state;
            _detail.Text = detail;
            _detail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
            _detail.ToolTip = string.IsNullOrEmpty(detail) ? null : detail;
            var app = Application.Current;
            Brush R(string key) => (Brush)app.FindResource(key);

            _glyph.Text = state switch
            {
                StepState.Done => "\uE73E",
                StepState.Failed => "\uE711",
                _ => Glyphs[_step],
            };
            _circle.Background = state switch
            {
                StepState.Done => R("SuccessSoft"),
                StepState.Failed => R("DangerSoft"),
                StepState.Active => R("AccentSoft"),
                _ => R("SurfaceHi"),
            };
            _circle.BorderBrush = state switch
            {
                StepState.Done => new SolidColorBrush(Color.FromArgb(90, 0x3D, 0xD6, 0x8C)),
                StepState.Failed => new SolidColorBrush(Color.FromArgb(90, 0xFB, 0x71, 0x85)),
                StepState.Active => Brushes.Transparent,
                _ => R("Stroke"),
            };
            _glyph.Foreground = state switch
            {
                StepState.Done => R("Success"),
                StepState.Failed => R("Danger"),
                StepState.Active => R("Accent"),
                _ => R("Faint"),
            };
            _title.Foreground = state == StepState.Pending ? R("Muted") : R("Text");
            _detail.Foreground = state switch
            {
                StepState.Done => R("Success"),
                StepState.Failed => R("Danger"),
                StepState.Active => R("Accent"),
                _ => R("Faint"),
            };

            _spinner.Visibility = state == StepState.Active ? Visibility.Visible : Visibility.Collapsed;
            var rotate = (RotateTransform)_spinner.RenderTransform;
            if (state == StepState.Active)
                rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
            else
                rotate.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }
}
