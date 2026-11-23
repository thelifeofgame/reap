using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;
using Reap.Core;

namespace Reap.App;

public static class Program
{
    private const string InstanceName = "Reap.SingleInstance";
    private const string ShowEventName = "Reap.ShowMainWindow";

    [STAThread]
    public static void Main(string[] args)
    {
        // 只运行一份：再次启动时通知已在运行的那份弹出主窗口，然后退出。
        using var mutex = new Mutex(true, InstanceName, out var first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show)) show.Set();
            return;
        }
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(background: args.Contains("--background"), showEvent);
        });
    }
}

/// <summary>
/// 没有 App.xaml，XAML 编译器不会生成类型元数据提供者，
/// 这里把 WinUI 内置控件的元数据接上，否则控件的默认样式加载不出来。
/// </summary>
public sealed class App : Application, IXamlMetadataProvider
{
    private readonly XamlControlsXamlMetaDataProvider _controls = new();
    private readonly bool _background;
    private readonly EventWaitHandle _showEvent;
    private AppState? _state;
    private MainWindow? _main;
    private PopupWindow? _popup;
    private TaskbarIcon? _tray;

    public App(bool background, EventWaitHandle showEvent)
    {
        _background = background;
        _showEvent = showEvent;
    }

    public IXamlType GetXamlType(Type type) => _controls.GetXamlType(type);
    public IXamlType GetXamlType(string fullName) => _controls.GetXamlType(fullName);
    public XmlnsDefinition[] GetXmlnsDefinitions() => _controls.GetXmlnsDefinitions();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        var ui = DispatcherQueue.GetForCurrentThread();
        _state = new AppState(ui);
        _popup = new PopupWindow(_state, OpenMain);
        CreateTray();
        _state.Changed += UpdateTray;

        // 另一份实例请求弹出主窗口。
        ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => ui.TryEnqueue(() => OpenMain(Pane.Leftovers)), null, -1, false);

        if (!_background || !_state.Settings.LaunchedBefore)
        {
            _state.Settings.LaunchedBefore = true;
            _state.Settings.Save();
            OpenMain(Pane.Leftovers);
        }
    }

    private void CreateTray()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("打开主窗口", () => OpenMain(Pane.Leftovers)));
        menu.Items.Add(MenuItem("结束全部残留进程", () => _state!.KillAll()));
        menu.Items.Add(MenuItem("设置", () => OpenMain(Pane.Settings)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("退出 Reap", Quit));

        _tray = new TaskbarIcon
        {
            ToolTipText = "Reap",
            Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "Reap.ico")),
            NoLeftClickDelay = true,
            ContextMenuMode = ContextMenuMode.SecondWindow,
            ContextFlyout = menu,
            LeftClickCommand = new Command(() => _popup!.Toggle()),
        };
        _tray.ForceCreate();
    }

    private void UpdateTray()
    {
        if (_tray is null || _state is null) return;
        var count = _state.LeftoverCount;
        _tray.ToolTipText = count == 0 ? "Reap：很干净" : $"Reap：{count} 个残留进程，占用 {Format.Bytes(_state.LeftoverMemory)}";
    }

    private void OpenMain(Pane pane)
    {
        _main ??= new MainWindow(_state!);
        _main.Navigate(pane);
        _main.ShowAndActivate();
    }

    private void Quit()
    {
        _tray?.Dispose();
        Exit();
    }

    private static MenuFlyoutItem MenuItem(string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => action();
        return item;
    }

    private sealed class Command(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
