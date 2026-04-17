using CLCKTY.App.Core;
using CLCKTY.App.Services;
using CLCKTY.App.UI;
using System.Collections.Generic;
using Wpf = System.Windows;
using Threading = System.Threading;

namespace CLCKTY.App;

public partial class App : Wpf.Application
{
	private const string SingleInstanceMutexName = "Global\\CLCKTY.App.Singleton";

	private IKeyboardHookService? _keyboardHookService;
	private ISoundEngine? _soundEngine;
	private TrayService? _trayService;
	private StartupService? _startupService;
	private MainViewModel? _mainViewModel;
	private MainWindow? _mainWindow;
	private Threading.Mutex? _singleInstanceMutex;

	private bool _isExitRequested;
	private bool _isCleanedUp;
	private readonly HashSet<int> _pressedKeys = new();
	private readonly HashSet<string> _latchedHotkeys = new(StringComparer.OrdinalIgnoreCase);

	protected override void OnStartup(Wpf.StartupEventArgs e)
	{
		base.OnStartup(e);

		_singleInstanceMutex = new Threading.Mutex(true, SingleInstanceMutexName, out var createdNew);
		if (!createdNew)
		{
			_singleInstanceMutex.Dispose();
			_singleInstanceMutex = null;
			Shutdown();
			return;
		}

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
		_keyboardHookService.MouseDown += KeyboardHookService_MouseDown;
		_keyboardHookService.MouseUp += KeyboardHookService_MouseUp;
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
		_pressedKeys.Add(e.VirtualKey);

		if (_mainViewModel is not null && _mainViewModel.TryCaptureKeyboardInput(e.VirtualKey))
		{
			_pressedKeys.Remove(e.VirtualKey);
			return;
		}

		if (_mainViewModel is not null && TryHandleToggleHotkeys())
		{
			return;
		}

		if (_mainViewModel is null || !_mainViewModel.IsKeyboardSoundEnabled)
		{
			return;
		}

		_mainViewModel.ReportInputTriggered(e.VirtualKey, KeyEventTrigger.Down);
		_soundEngine?.StartHoldForKey(e.VirtualKey);
	}

	private void KeyboardHookService_KeyUp(object? sender, GlobalKeyPressedEventArgs e)
	{
		_pressedKeys.Remove(e.VirtualKey);
		ReleaseToggleHotkeyLatches();

		if (_mainViewModel is null || !_mainViewModel.IsKeyboardSoundEnabled)
		{
			return;
		}

		_mainViewModel.ReportInputTriggered(e.VirtualKey, KeyEventTrigger.Up);
		_soundEngine?.ReleaseForKey(e.VirtualKey);
	}

	private void KeyboardHookService_MouseDown(object? sender, GlobalMouseButtonEventArgs e)
	{
		if (_mainViewModel is not null && _mainViewModel.TryCaptureMouseInput(e.InputCode))
		{
			return;
		}

		if (_mainViewModel is null || !_mainViewModel.IsMouseSoundEnabled)
		{
			return;
		}

		_mainViewModel.ReportInputTriggered(e.InputCode, KeyEventTrigger.Down);
		_soundEngine?.StartHoldForKey(e.InputCode);
	}

	private void KeyboardHookService_MouseUp(object? sender, GlobalMouseButtonEventArgs e)
	{
		if (_mainViewModel is null || !_mainViewModel.IsMouseSoundEnabled)
		{
			return;
		}

		_mainViewModel.ReportInputTriggered(e.InputCode, KeyEventTrigger.Up);
		_soundEngine?.ReleaseForKey(e.InputCode);
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
			_keyboardHookService.MouseDown -= KeyboardHookService_MouseDown;
			_keyboardHookService.MouseUp -= KeyboardHookService_MouseUp;
			_keyboardHookService.Dispose();
			_keyboardHookService = null;
		}

		if (_soundEngine is not null)
		{
			_soundEngine.Dispose();
			_soundEngine = null;
		}

		if (_singleInstanceMutex is not null)
		{
			_singleInstanceMutex.ReleaseMutex();
			_singleInstanceMutex.Dispose();
			_singleInstanceMutex = null;
		}
	}

	private bool TryHandleToggleHotkeys()
	{
		if (_mainViewModel is null)
		{
			return false;
		}

		var handled = false;

		if (IsMasterToggleHotkeyPressed())
		{
			handled = true;
			if (_latchedHotkeys.Add("master"))
			{
				_mainViewModel.IsEnabled = !_mainViewModel.IsEnabled;
			}
		}

		if (IsKeyboardToggleHotkeyPressed())
		{
			handled = true;
			if (_latchedHotkeys.Add("keyboard"))
			{
				_mainViewModel.IsKeyboardSoundEnabled = !_mainViewModel.IsKeyboardSoundEnabled;
			}
		}

		if (IsMouseToggleHotkeyPressed())
		{
			handled = true;
			if (_latchedHotkeys.Add("mouse"))
			{
				_mainViewModel.IsMouseSoundEnabled = !_mainViewModel.IsMouseSoundEnabled;
			}
		}

		return handled;
	}

	private void ReleaseToggleHotkeyLatches()
	{
		if (!IsMasterToggleHotkeyPressed())
		{
			_latchedHotkeys.Remove("master");
		}

		if (!IsKeyboardToggleHotkeyPressed())
		{
			_latchedHotkeys.Remove("keyboard");
		}

		if (!IsMouseToggleHotkeyPressed())
		{
			_latchedHotkeys.Remove("mouse");
		}
	}

	private bool IsMasterToggleHotkeyPressed() => IsCtrlAltPressed() && _pressedKeys.Contains(0x4D);

	private bool IsKeyboardToggleHotkeyPressed() => IsCtrlAltPressed() && _pressedKeys.Contains(0x4B);

	private bool IsMouseToggleHotkeyPressed() => IsCtrlAltPressed() && _pressedKeys.Contains(0x4F);

	private bool IsCtrlAltPressed()
	{
		var ctrlPressed = _pressedKeys.Contains(0x11) || _pressedKeys.Contains(0xA2) || _pressedKeys.Contains(0xA3);
		var altPressed = _pressedKeys.Contains(0x12) || _pressedKeys.Contains(0xA4) || _pressedKeys.Contains(0xA5);
		return ctrlPressed && altPressed;
	}
}

