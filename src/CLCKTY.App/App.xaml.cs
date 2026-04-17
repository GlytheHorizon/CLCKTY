using CLCKTY.App.Core;
using CLCKTY.App.Services;
using CLCKTY.App.UI;
using Wpf = System.Windows;

namespace CLCKTY.App;

public partial class App : Wpf.Application
{
	private IKeyboardHookService? _keyboardHookService;
	private ISoundEngine? _soundEngine;
	private TrayService? _trayService;
	private StartupService? _startupService;
	private MainViewModel? _mainViewModel;
	private MainWindow? _mainWindow;

	private bool _isExitRequested;
	private bool _isCleanedUp;

	protected override void OnStartup(Wpf.StartupEventArgs e)
	{
		base.OnStartup(e);

		ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown;

		_soundEngine = new SoundEngine();
		_keyboardHookService = new KeyboardHookService();
		_startupService = new StartupService();
		_mainViewModel = new MainViewModel(_soundEngine, _startupService);
		_mainWindow = new MainWindow(_mainViewModel);

		_mainWindow.Closing += MainWindow_Closing;
		_mainWindow.StateChanged += MainWindow_StateChanged;

		_trayService = new TrayService();
		_trayService.OpenRequested += TrayService_OpenRequested;
		_trayService.SoundsToggled += TrayService_SoundsToggled;
		_trayService.ExitRequested += TrayService_ExitRequested;
		_trayService.IsSoundEnabled = _mainViewModel.IsEnabled;

		_mainViewModel.SoundEnabledChanged += MainViewModel_SoundEnabledChanged;

		_keyboardHookService.KeyDown += KeyboardHookService_KeyDown;
		_keyboardHookService.KeyUp += KeyboardHookService_KeyUp;
		_keyboardHookService.Start();

		var launchInTray = e.Args.Any(arg => string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase));
		if (!launchInTray)
		{
			ShowControlPanel();
		}
	}

	protected override void OnExit(Wpf.ExitEventArgs e)
	{
		Cleanup();
		base.OnExit(e);
	}

	private void KeyboardHookService_KeyDown(object? sender, GlobalKeyPressedEventArgs e)
	{
		_soundEngine?.StartHoldForKey(e.VirtualKey);
	}

	private void KeyboardHookService_KeyUp(object? sender, GlobalKeyPressedEventArgs e)
	{
		_soundEngine?.ReleaseForKey(e.VirtualKey);
	}

	private void MainViewModel_SoundEnabledChanged(object? sender, bool isEnabled)
	{
		if (_trayService is not null)
		{
			_trayService.IsSoundEnabled = isEnabled;
		}
	}

	private void TrayService_OpenRequested(object? sender, EventArgs e)
	{
		Dispatcher.Invoke(ShowControlPanel);
	}

	private void TrayService_SoundsToggled(object? sender, bool isEnabled)
	{
		if (_mainViewModel is null)
		{
			return;
		}

		Dispatcher.Invoke(() => _mainViewModel.IsEnabled = isEnabled);
	}

	private void TrayService_ExitRequested(object? sender, EventArgs e)
	{
		Dispatcher.Invoke(RequestExit);
	}

	private void MainWindow_StateChanged(object? sender, EventArgs e)
	{
		if (_mainWindow?.WindowState == Wpf.WindowState.Minimized)
		{
			_mainWindow.Hide();
		}
	}

	private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
	{
		if (_isExitRequested)
		{
			return;
		}

		e.Cancel = true;
		_mainWindow?.Hide();
	}

	private void ShowControlPanel()
	{
		_mainWindow?.ShowPanel();
	}

	private void RequestExit()
	{
		if (_isExitRequested)
		{
			return;
		}

		_isExitRequested = true;

		if (_mainWindow is not null)
		{
			_mainWindow.Closing -= MainWindow_Closing;
			_mainWindow.StateChanged -= MainWindow_StateChanged;
			_mainWindow.Close();
		}

		Cleanup();
		Shutdown();
	}

	private void Cleanup()
	{
		if (_isCleanedUp)
		{
			return;
		}

		_isCleanedUp = true;

		if (_mainViewModel is not null)
		{
			_mainViewModel.SoundEnabledChanged -= MainViewModel_SoundEnabledChanged;
		}

		if (_trayService is not null)
		{
			_trayService.OpenRequested -= TrayService_OpenRequested;
			_trayService.SoundsToggled -= TrayService_SoundsToggled;
			_trayService.ExitRequested -= TrayService_ExitRequested;
			_trayService.Dispose();
			_trayService = null;
		}

		if (_keyboardHookService is not null)
		{
			_keyboardHookService.KeyDown -= KeyboardHookService_KeyDown;
			_keyboardHookService.KeyUp -= KeyboardHookService_KeyUp;
			_keyboardHookService.Dispose();
			_keyboardHookService = null;
		}

		if (_soundEngine is not null)
		{
			_soundEngine.Dispose();
			_soundEngine = null;
		}
	}
}

