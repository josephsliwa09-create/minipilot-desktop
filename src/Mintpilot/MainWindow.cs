using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace Mintpilot;

public sealed class MainWindow : Window
{
    private readonly Engine _engine;
    private readonly bool _smoke;
    private readonly ContentControl _content = new();
    private readonly TextBlock _status = new();
    private readonly Dictionary<string, Button> _nav = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private string _page = "Overview";
    private bool _working;
    private static readonly SolidColorBrush BackgroundBrush = Brush("#0B1116");
    private static readonly SolidColorBrush PanelBrush = Brush("#121C23");
    private static readonly SolidColorBrush Accent = Brush("#9DEAC9");
    private static readonly SolidColorBrush Muted = Brush("#8B9FAE");
    private static readonly SolidColorBrush Line = Brush("#26343E");
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    public MainWindow(Engine engine, bool smoke)
    {
        _engine = engine; _smoke = smoke;
        Title = "Mintpilot"; Width = 1280; Height = 850; MinWidth = 1040; MinHeight = 700;
        Background = BackgroundBrush; Foreground = Brushes.White; FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetStyles();
        var root = new Grid(); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) }); root.ColumnDefinitions.Add(new ColumnDefinition());
        var rail = new DockPanel { Background = Brush("#0F181F"), LastChildFill = true };
        var brand = Text("◈  Mintpilot", 26, Brushes.White, FontWeights.SemiBold); brand.Margin = new Thickness(23, 30, 0, 28); DockPanel.SetDock(brand, Dock.Top); rail.Children.Add(brand);
        var footer = Stack(Text("● SOLANA MAINNET", 11, Accent), Text("Wallet protected by Windows", 11, Muted));
        footer.Margin = new Thickness(22, 20, 10, 24); DockPanel.SetDock(footer, Dock.Bottom); rail.Children.Add(footer);
        var navigation = new StackPanel { Margin = new Thickness(12, 0, 12, 0) };
        foreach (var (page, icon) in new[] { ("Overview", "▦"), ("Launches", "◇"), ("Promotions", "◉"), ("Wallet", "▤"), ("Settings", "⚙") })
        {
            string selected = page;
            var button = Button(icon + "   " + page, () => { ShowPage(selected); return Task.CompletedTask; });
            button.HorizontalContentAlignment = HorizontalAlignment.Left; button.Height = 45; button.Margin = new Thickness(0, 4, 0, 4);
            navigation.Children.Add(button); _nav[page] = button;
        }
        rail.Children.Add(navigation); root.Children.Add(rail);
        var right = new DockPanel { Margin = new Thickness(30, 22, 30, 20) };
        var crumb = Text("WORKSPACE  /  MAINNET", 11, Muted); crumb.Margin = new Thickness(0, 0, 0, 14); DockPanel.SetDock(crumb, Dock.Top); right.Children.Add(crumb);
        _status.Foreground = Muted; _status.FontSize = 12; _status.TextWrapping = TextWrapping.Wrap; _status.Margin = new Thickness(0, 14, 0, 0); DockPanel.SetDock(_status, Dock.Bottom); right.Children.Add(_status);
        right.Children.Add(new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetColumn(right, 1); root.Children.Add(right); Content = root;
        _engine.Changed += () =>
        {
            _status.Text = _engine.Status + (_engine.Running && _engine.NextLaunch is not null ? " · Next launch " + _engine.NextLaunch.Value.ToLocalTime().ToString("t") : "");
            if (_page != "Settings") ShowPage(_page);
        };
        _timer.Tick += async (_, _) => await _engine.Poll();
        Loaded += async (_, _) => { if (!_smoke) { _timer.Start(); await _engine.Poll(); } };
        Closing += (_, _) => { _timer.Stop(); _engine.Pause(); };
        ShowPage("Overview");
    }
    private void SetStyles()
    {
        var button = new Style(typeof(Button));
        button.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#20313B")));
        button.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        button.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
        button.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        button.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(15, 10, 15, 10)));
        button.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand));
        button.Setters.Add(new Setter(Control.TemplateProperty, System.Windows.Markup.XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='7' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center'/></Border></ControlTemplate>")));
        Resources[typeof(Button)] = button;
        var text = new Style(typeof(TextBox));
        text.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#0D171D"))); text.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        text.Setters.Add(new Setter(Control.BorderBrushProperty, Line)); text.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10)));
        text.Setters.Add(new Setter(TextBox.CaretBrushProperty, Brushes.White)); Resources[typeof(TextBox)] = text;
        var password = new Style(typeof(PasswordBox));
        password.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#0D171D"))); password.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        password.Setters.Add(new Setter(Control.BorderBrushProperty, Line)); password.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10))); Resources[typeof(PasswordBox)] = password;
    }
    private static TextBlock Text(string value, double size = 14, Brush? color = null, FontWeight? weight = null) =>
        new() { Text = value, FontSize = size, Foreground = color ?? Brushes.White, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
    private static StackPanel Stack(params UIElement[] elements)
    {
        var stack = new StackPanel(); foreach (var e in elements) stack.Children.Add(e); return stack;
    }
    private static Border Card(UIElement content) => new() { Background = PanelBrush, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(20), Child = content, Margin = new Thickness(0, 0, 12, 14) };
    private Button Button(string label, Func<Task> click, bool primary = false)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 9, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        if (primary) { button.Background = Accent; button.Foreground = Brush("#10231C"); }
        button.Click += async (_, _) => await Execute(click); return button;
    }
    private async Task Execute(Func<Task> action)
    {
        if (_working) return;
        _working = true;
        try { await action(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Mintpilot", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { _working = false; _status.Text = _engine.Status; if (_page != "Settings") ShowPage(_page); }
    }
    private static WrapPanel Actions(params UIElement[] elements)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 14) }; foreach (var e in elements) { row.Children.Add(e); if (e is FrameworkElement f) f.Margin = new Thickness(0, 0, 9, 8); } return row;
    }
    public void ShowPage(string page)
    {
        _page = page;
        foreach (var (name, button) in _nav) { button.Background = name == page ? Brush("#244138") : Brushes.Transparent; button.Foreground = name == page ? Accent : Muted; }
        var body = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        body.Children.Add(Text(page == "Overview" ? "Mission control" : page, 34, Brushes.White, FontWeights.SemiBold));
        body.Children.Add(Text(page switch
        {
            "Overview" => "Your launch agent, live wallet, and on-chain activity.",
            "Launches" => "Track launch results, holdings, and market statistics.",
            "Promotions" => "Delivery history for your connected promotion channels.",
            "Wallet" => "Deposit SOL, withdraw available funds, and back up your wallet.",
            _ => "Manage launch limits, AI generation, and connected services."
        }, 14, Muted));
        body.Children.Add(new Border { Height = 20 });
        switch (page)
        {
            case "Overview": Overview(body); break;
            case "Launches": Launches(body); break;
            case "Promotions": Promotions(body); break;
            case "Wallet": Wallet(body); break;
            case "Settings": SettingsPage(body); break;
        }
        _content.Content = body;
    }
    private void Overview(StackPanel body)
    {
        var s = _engine.State.Settings;
        var hero = Stack(Text("LAUNCH AGENT", 11, Accent), Text(_engine.Running ? "Autopilot is running" : "Ready when you are", 28, Brushes.White, FontWeights.SemiBold),
            Text("Initial buy " + s.BuySol + " SOL · Up to " + s.MaxLaunchSol + " SOL per launch · " + s.IntervalMinutes + " minute interval", 13, Muted));
        hero.Children.Add(Actions(
            Button(_engine.Running ? "Ⅱ  Pause agent" : "▷  Start agent", async () => { if (_engine.Running) _engine.Pause(); else { _engine.Start(); await _engine.Poll(); } }, true),
            Button("+  Launch a coin", LaunchDialog), Button("Sync", _engine.Poll)));
        body.Children.Add(Card(hero));
        var ops = _engine.State.Operations;
        var today = ops.Where(o => o.Kind == "Create" && o.CreatedUtc.UtcDateTime.Date == DateTime.UtcNow.Date).ToList();
        long spent = ops.Where(o => o.Kind == "Create").Sum(o => Math.Max(0, o.DebitLamports ?? 0));
        long todayCommitted = today.Sum(o => o.Pending ? o.LimitLamports : Math.Max(0, o.DebitLamports ?? 0));
        var stats = new[]
        {
            ("Available wallet", _engine.BalanceLamports is null ? "Connecting…" : Sol.Display(_engine.Available)),
            ("Finalized launches", ops.Count(o => o.Kind == "Create" && o.Status == "Confirmed").ToString()),
            ("Published promotions", ops.Sum(o => (o.XStatus == "Published" ? 1 : 0) + (o.TelegramStatus == "Published" ? 1 : 0)).ToString()),
            ("Launch expenditure", Sol.Display(spent)),
            ("Network fees", Sol.Display(ops.Sum(o => o.FeeLamports ?? 0))),
            ("Sale proceeds (net)", Sol.Display(-ops.Where(o => o.Kind == "Sell").Sum(o => o.DebitLamports ?? 0))),
            ("Failed / expired", ops.Count(o => o.Status is "Failed" or "Expired").ToString()),
            ("Daily budget remaining", Sol.Display(Math.Max(0, Sol.Lamports(s.DailyCapSol) - todayCommitted)))
        };
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4 };
        foreach (var (label, value) in stats) grid.Children.Add(Card(Stack(Text(label, 12, Muted), Text(value, 23, Brushes.White, FontWeights.SemiBold))));
        body.Children.Add(grid);
        body.Children.Add(Card(Stack(Text("Wallet balance", 18, Brushes.White, FontWeights.SemiBold), Text("Confirmed SOL · saved locally", 11, Muted), Chart())));
        body.Children.Add(Text("Recent activity", 18, Brushes.White, FontWeights.SemiBold));
        body.Children.Add(OperationGrid(ops.AsEnumerable().Reverse().Take(8)));
        body.Children.Add(Text("Autopilot runs while Mintpilot is open and Windows is awake. The agent starts paused after reopening.", 12, Muted));
    }
    private UIElement Chart()
    {
        var canvas = new Canvas { Height = 145, ClipToBounds = true };
        void Draw()
        {
            canvas.Children.Clear(); double width = Math.Max(1, canvas.ActualWidth);
            var data = _engine.State.BalanceHistory.TakeLast(100).ToArray();
            if (data.Length < 2) { canvas.Children.Add(Text("Balance history appears after two successful syncs.", 12, Muted)); return; }
            double max = Math.Max(1, data.Max(p => p.Lamports));
            for (int i = 0; i < 4; i++) canvas.Children.Add(new System.Windows.Shapes.Line { X1 = 0, X2 = width, Y1 = i * 40, Y2 = i * 40, Stroke = Line });
            var polyline = new Polyline { Stroke = Accent, StrokeThickness = 2 };
            for (int i = 0; i < data.Length; i++) polyline.Points.Add(new Point(i * (width - 5) / (data.Length - 1), 130 - data[i].Lamports / max * 115));
            canvas.Children.Add(polyline);
        }
        canvas.SizeChanged += (_, _) => Draw(); return canvas;
    }
    private DataGrid GridFor(IEnumerable<object> rows, params (string Title, string Path)[] columns)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false,
            ItemsSource = rows, Background = PanelBrush, Foreground = Brushes.White, BorderBrush = Line,
            RowBackground = PanelBrush, AlternatingRowBackground = Brush("#15232B"), GridLinesVisibility = DataGridGridLinesVisibility.None,
            RowHeight = 43, HeadersVisibility = DataGridHeadersVisibility.Column, MinHeight = 120, MaxHeight = 420,
            SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow, Margin = new Thickness(0, 12, 10, 14)
        };
        var header = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        header.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#1B2B34"))); header.Setters.Add(new Setter(Control.ForegroundProperty, Muted));
        header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10))); grid.ColumnHeaderStyle = header;
        var cell = new Style(typeof(DataGridCell)); cell.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8)));
        cell.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0))); grid.CellStyle = cell;
        foreach (var (title, path) in columns) grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(path), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 100 });
        return grid;
    }
    private DataGrid OperationGrid(IEnumerable<Operation> operations) => GridFor(operations.Cast<object>().ToList(),
        ("Operation", "Kind"), ("Name", "Name"), ("Status", "Status"), ("Net wallet debit", "Amount"), ("Created (UTC)", "CreatedUtc"));
    private sealed class TokenRow
    {
        public Operation Source = null!;
        public string Token { get; init; } = "";
        public string Status { get; init; } = "";
        public string Holdings { get; init; } = "";
        public string Price { get; init; } = "";
        public string MarketCap { get; init; } = "";
        public string Volume { get; init; } = "";
        public string Value { get; init; } = "";
    }
    private void Launches(StackPanel body)
    {
        var rows = _engine.State.Operations.Where(o => o.Kind == "Create").Reverse().Select(o =>
        {
            _engine.Markets.TryGetValue(o.Mint, out var market);
            decimal quantity = _engine.Holdings.GetValueOrDefault(o.Mint);
            return new TokenRow
            {
                Source = o, Token = o.Name + " · $" + o.Symbol, Status = o.Status,
                Holdings = _engine.BalanceLamports is null ? "Pending sync" : quantity.ToString("N2"),
                Price = market?.PriceUsd > 0 ? "$" + market.PriceUsd.ToString("0.########") : "Unavailable",
                MarketCap = market?.MarketCapUsd > 0 ? "$" + market.MarketCapUsd.ToString("N0") : "Unavailable",
                Volume = market is not null ? "$" + market.Volume24Usd.ToString("N0") : "Unavailable",
                Value = market?.PriceUsd > 0 ? "$" + (market.PriceUsd * quantity).ToString("N2") : "Unavailable"
            };
        }).ToList();
        var grid = GridFor(rows.Cast<object>().ToList(), ("Token", "Token"), ("Result", "Status"), ("Holdings", "Holdings"), ("Price (USD)", "Price"), ("Market cap", "MarketCap"), ("24h volume", "Volume"), ("Held value", "Value"));
        Operation Selection() => (grid.SelectedItem as TokenRow)?.Source ?? throw new InvalidOperationException("Select a launch first.");
        body.Children.Add(Actions(Button("+ Launch a coin", LaunchDialog, true), Button("Sell remaining tokens", async () =>
        {
            var op = Selection();
            if (MessageBox.Show(this, "Sell 100% of this wallet's " + op.Symbol + " tokens using your configured slippage and fee limits?", "Sell tokens", MessageBoxButton.YesNo) == MessageBoxResult.Yes) await _engine.Sell(op);
        }), Button("Copy mint address", () => { Clipboard.SetText(Selection().Mint); return Task.CompletedTask; }), Button("Details", () => { Details(Selection()); return Task.CompletedTask; })));
        body.Children.Add(grid); body.Children.Add(Text(_engine.MarketStatus, 12, Muted));
        body.Children.Add(Text("Prices and held values are market estimates. New coins may appear after the market provider indexes them.", 12, Muted));
    }
    private void Promotions(StackPanel body)
    {
        body.Children.Add(Text("Delivery records are saved for each channel. Review a delivery marked Unknown before retrying it.", 13, Muted));
        foreach (var op in _engine.State.Operations.Where(o => o.Kind == "Create").Reverse())
        {
            var post = new TextBox { Text = op.Promotion, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
            body.Children.Add(Card(Stack(Text(op.Name + " · $" + op.Symbol, 18, Brushes.White, FontWeights.SemiBold), post,
                Text("Telegram " + op.TelegramStatus + "  ·  X " + op.XStatus, 12, Accent),
                Actions(Button("Copy post", () => { Clipboard.SetText(op.Promotion); return Task.CompletedTask; }), Button("Publish / retry", async () =>
                {
                    if ((op.TelegramStatus == "Unknown" || op.XStatus == "Unknown") && MessageBox.Show(this, "A previous post may have arrived. Check your channel first. Retry delivery now?", "Review promotion", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                    await _engine.Publish(op);
                })))));
        }
    }
    private void Wallet(StackPanel body)
    {
        var address = new TextBox { Text = _engine.Address, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 16 };
        body.Children.Add(Card(Stack(Text("SOLANA DEPOSIT ADDRESS", 11, Accent), address,
            Text("Send SOL on Solana mainnet to this address. This app signs transactions locally.", 13, Muted),
            Actions(Button("Copy address", () => { Clipboard.SetText(_engine.Address); return Task.CompletedTask; }), Button("Sync balance", _engine.Poll)))));
        body.Children.Add(Card(Stack(Text("Spendable SOL", 13, Muted), Text(_engine.BalanceLamports is null ? "Connecting…" : Sol.Display(_engine.Available), 35, Brushes.White, FontWeights.SemiBold),
            Text("Pending launch reservation " + Sol.Display(_engine.Reserved), 12, Muted),
            Actions(Button("Withdraw SOL", WithdrawDialog, true), Button("Back up wallet", BackupDialog), Button("Restore backup", RestoreDialog)))));
        body.Children.Add(Text("Withdrawals send available SOL after the network fee. Sell token holdings from Launches to convert them to SOL.", 12, Muted));
        body.Children.Add(OperationGrid(_engine.State.Operations.AsEnumerable().Reverse()));
    }
    private void Details(Operation op)
    {
        var dialog = Dialog("Transaction details", 650);
        var content = Stack(Text(op.Name + " · " + op.Status, 21), Text(op.Error, 13, Muted));
        foreach (var (label, value) in new[] { ("Transaction signature", op.Signature), ("Mint", op.Mint), ("Recipient", op.Recipient), ("Metadata", op.Uri) })
        {
            if (value.Length == 0) continue;
            content.Children.Add(Text(label, 12, Muted)); content.Children.Add(new TextBox { Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap });
        }
        dialog.Content = content; dialog.ShowDialog();
    }
    private Window Dialog(string title, double width = 470) => new()
    {
        Owner = this, Title = title, Width = width, SizeToContent = SizeToContent.Height, MaxHeight = 760,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = PanelBrush,
        Foreground = Brushes.White, FontFamily = FontFamily, FontSize = 14, Padding = new Thickness(24)
    };
    private static TextBox Field(StackPanel parent, string label, string value)
    {
        parent.Children.Add(Text(label, 12, Muted)); var box = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 9) }; parent.Children.Add(box); return box;
    }
    private static PasswordBox Secret(StackPanel parent, string label, string value = "")
    {
        parent.Children.Add(Text(label, 12, Muted)); var box = new PasswordBox { Password = value, Margin = new Thickness(0, 0, 0, 9) }; parent.Children.Add(box); return box;
    }
    private async Task LaunchDialog()
    {
        var dialog = Dialog("Launch a coin", 520);
        var body = new StackPanel();
        body.Children.Add(Text("Create on pump.fun", 24, Brushes.White, FontWeights.SemiBold));
        body.Children.Add(Text("Initial buy " + _engine.State.Settings.BuySol + " SOL. Total wallet debit is limited to " + _engine.State.Settings.MaxLaunchSol + " SOL.", 13, Muted));
        var name = Field(body, "Name", ""); var symbol = Field(body, "Ticker", ""); var description = Field(body, "Description", "");
        var post = Field(body, "Promotion text (up to 150 characters)", "");
        var image = Field(body, "PNG artwork path (leave blank for a generated token icon)", "");
        body.Children.Add(Button("Choose PNG", () => { var picker = new OpenFileDialog { Filter = "PNG image|*.png" }; if (picker.ShowDialog(dialog) == true) image.Text = picker.FileName; return Task.CompletedTask; }));
        bool launch = false;
        var confirm = new Button { Content = "Launch with real SOL", Background = Accent, Foreground = Brush("#10231C"), Margin = new Thickness(0, 18, 0, 0), Padding = new Thickness(15, 10, 15, 10) };
        confirm.Click += (_, _) => { launch = true; dialog.Close(); }; body.Children.Add(confirm); dialog.Content = body; dialog.ShowDialog();
        if (!launch) return;
        var idea = new CoinIdea { Name = name.Text.Trim(), Symbol = symbol.Text.Trim().ToUpperInvariant(), Description = description.Text, Post = post.Text }; idea.Validate();
        byte[] png = image.Text.Trim().Length == 0 ? Artwork.Render(idea) : File.ReadAllBytes(image.Text);
        if (png.Length > 5_000_000) throw new InvalidOperationException("Choose a PNG under 5 MB.");
        using (var input = new MemoryStream(png)) BitmapFrame.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        await _engine.Launch(idea, png);
    }
    private async Task WithdrawDialog()
    {
        var dialog = Dialog("Withdraw SOL"); var body = new StackPanel();
        body.Children.Add(Text("Withdraw to your wallet", 24)); var destination = Field(body, "Destination Solana public address", "");
        var amount = Field(body, "SOL amount (leave blank to withdraw all spendable SOL)", "");
        body.Children.Add(Text("The agent pauses for this withdrawal. The destination and amount are verified before local signing.", 12, Muted));
        bool send = false; var button = new Button { Content = "Withdraw SOL", Margin = new Thickness(0, 15, 0, 0) };
        button.Click += (_, _) => { send = true; dialog.Close(); }; body.Children.Add(button); dialog.Content = body; dialog.ShowDialog();
        if (!send) return;
        decimal? value = amount.Text.Trim().Length == 0 ? null : decimal.Parse(amount.Text, CultureInfo.InvariantCulture);
        await _engine.Withdraw(destination.Text.Trim(), value);
    }
    private Task BackupDialog()
    {
        var dialog = Dialog("Back up wallet"); var body = new StackPanel();
        body.Children.Add(Text("Encrypted wallet backup", 24)); var password = Secret(body, "Backup password (at least 12 characters)");
        var repeat = Secret(body, "Repeat password"); bool save = false;
        var button = new Button { Content = "Save encrypted backup", Margin = new Thickness(0, 15, 0, 0) };
        button.Click += (_, _) => { save = true; dialog.Close(); }; body.Children.Add(button); dialog.Content = body; dialog.ShowDialog();
        if (save)
        {
            if (password.Password != repeat.Password) throw new InvalidOperationException("Backup passwords do not match.");
            var file = new SaveFileDialog { Filter = "Mintpilot encrypted wallet|*.wallet", FileName = "Mintpilot.wallet" };
            var bytes = _engine.Vault.Export(password.Password);
            if (file.ShowDialog(this) == true) Vault.AtomicWrite(file.FileName, bytes);
        }
        return Task.CompletedTask;
    }
    private async Task RestoreDialog()
    {
        _engine.Pause(); if (_engine.Busy) throw new InvalidOperationException("Wait for the active operation to finish.");
        if (MessageBox.Show(this, "Restore replaces the active wallet. Back up the current wallet before continuing. Continue with a backup file?", "Restore wallet", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var picker = new OpenFileDialog { Filter = "Mintpilot encrypted wallet|*.wallet" };
        if (picker.ShowDialog(this) != true) return;
        var dialog = Dialog("Unlock backup"); var body = new StackPanel(); var password = Secret(body, "Backup password"); bool restore = false;
        var button = new Button { Content = "Restore wallet" }; button.Click += (_, _) => { restore = true; dialog.Close(); }; body.Children.Add(button); dialog.Content = body; dialog.ShowDialog();
        if (restore) { _engine.Vault.Restore(File.ReadAllBytes(picker.FileName), password.Password); await _engine.Poll(); }
    }
    private void SettingsPage(StackPanel body)
    {
        var s = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(_engine.State.Settings))!;
        var form = new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 650 };
        form.Children.Add(Text("Wallet and launch limits", 20, Brushes.White, FontWeights.SemiBold));
        var rpc = Field(form, "Solana mainnet HTTPS RPC URL", s.RpcUrl);
        var pinata = Secret(form, "Pinata JWT · public IPFS uploads", s.PinataToken);
        var buy = Field(form, "Initial buy per token (SOL)", s.BuySol.ToString(CultureInfo.InvariantCulture));
        var cap = Field(form, "Maximum total SOL debit per launch, including rent and fees", s.MaxLaunchSol.ToString(CultureInfo.InvariantCulture));
        var daily = Field(form, "Daily launch budget (SOL, resets at midnight UTC)", s.DailyCapSol.ToString(CultureInfo.InvariantCulture));
        var reserve = Field(form, "Wallet SOL reserve for launches", s.ReserveSol.ToString(CultureInfo.InvariantCulture));
        var interval = Field(form, "Minutes between launches", s.IntervalMinutes.ToString());
        var count = Field(form, "Maximum finalized/pending launches per UTC day", s.MaxDailyLaunches.ToString());
        var slippage = Field(form, "Trade slippage (%)", s.Slippage.ToString(CultureInfo.InvariantCulture));
        var priority = Field(form, "Priority fee (SOL)", s.PriorityFeeSol.ToString(CultureInfo.InvariantCulture));
        form.Children.Add(Text("AI generation", 20, Brushes.White, FontWeights.SemiBold));
        var provider = new ComboBox { ItemsSource = new[] { "Ollama", "OpenAI" }, SelectedItem = s.AiProvider, Padding = new Thickness(8), Margin = new Thickness(0, 6, 0, 10), Foreground = Brushes.Black };
        form.Children.Add(provider);
        var model = Field(form, "Model name · Ollama example llama3.2:3b / OpenAI example gpt-4.1-mini", s.AiModel);
        var aiKey = Secret(form, "OpenAI API key (for OpenAI mode)", s.OpenAiKey);
        var theme = Field(form, "Coin theme", s.Theme);
        form.Children.Add(Text("Promotion channels", 20, Brushes.White, FontWeights.SemiBold));
        var telegram = new CheckBox { Content = "Automatically publish to Telegram after finalization", IsChecked = s.TelegramEnabled, Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 10) }; form.Children.Add(telegram);
        var tKey = Secret(form, "Telegram bot token", s.TelegramToken); var tChat = Field(form, "Telegram destination chat ID", s.TelegramChat);
        var x = new CheckBox { Content = "Automatically publish to X after finalization", IsChecked = s.XEnabled, Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 10) }; form.Children.Add(x);
        var xKey = Secret(form, "X OAuth 1.0a consumer API key", s.XConsumerKey); var xSecret = Secret(form, "X consumer API secret", s.XConsumerSecret);
        var xToken = Secret(form, "X user access token (Read and Write permission)", s.XAccessToken); var xAccessSecret = Secret(form, "X user access token secret", s.XAccessSecret);
        form.Children.Add(Text("Credentials and the wallet seed are encrypted for your Windows user. Ollama uses localhost port 11434. Promotion channels require your own API access.", 12, Muted));
        form.Children.Add(Actions(Button("Save settings", async () =>
        {
            static decimal D(TextBox t) => decimal.Parse(t.Text, CultureInfo.InvariantCulture);
            s.RpcUrl = rpc.Text.Trim(); s.PinataToken = pinata.Password.Trim(); s.BuySol = D(buy); s.MaxLaunchSol = D(cap); s.DailyCapSol = D(daily); s.ReserveSol = D(reserve);
            s.IntervalMinutes = int.Parse(interval.Text, CultureInfo.InvariantCulture); s.MaxDailyLaunches = int.Parse(count.Text, CultureInfo.InvariantCulture); s.Slippage = D(slippage); s.PriorityFeeSol = D(priority);
            s.AiProvider = provider.SelectedItem?.ToString() ?? "Ollama"; s.AiModel = model.Text.Trim(); s.OpenAiKey = aiKey.Password.Trim(); s.Theme = theme.Text.Trim();
            s.TelegramEnabled = telegram.IsChecked == true; s.TelegramToken = tKey.Password.Trim(); s.TelegramChat = tChat.Text.Trim();
            s.XEnabled = x.IsChecked == true; s.XConsumerKey = xKey.Password.Trim(); s.XConsumerSecret = xSecret.Password.Trim(); s.XAccessToken = xToken.Password.Trim(); s.XAccessSecret = xAccessSecret.Password.Trim();
            await _engine.SaveSettings(s); MessageBox.Show(this, "Settings saved.", "Mintpilot");
        }, true)));
        body.Children.Add(Card(form));
    }
}

public static class Artwork
{
    public static byte[] Render(CoinIdea idea)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(idea.Name));
        var drawing = new DrawingVisual();
        using (var d = drawing.RenderOpen())
        {
            d.DrawRectangle(new SolidColorBrush(Color.FromRgb(11, 19, 26)), null, new Rect(0, 0, 512, 512));
            var accent = new SolidColorBrush(Color.FromRgb((byte)(90 + hash[0] % 100), (byte)(140 + hash[1] % 100), (byte)(130 + hash[2] % 100)));
            d.DrawEllipse(accent, null, new Point(256, 245), 178, 178);
            d.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)), 2), new Point(256, 245), 204, 204);
            var letters = new FormattedText(idea.Symbol[..Math.Min(3, idea.Symbol.Length)], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 96, new SolidColorBrush(Color.FromRgb(12, 28, 25)), 1);
            d.DrawText(letters, new Point((512 - letters.Width) / 2, 180));
            var label = new FormattedText(idea.Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 21, Brushes.White, 1);
            d.DrawText(label, new Point((512 - label.Width) / 2, 465));
        }
        var bitmap = new RenderTargetBitmap(512, 512, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
}
