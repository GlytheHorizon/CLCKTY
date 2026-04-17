using System.Windows;
using CLCKTY.Core;
using CLCKTY.Services;
using CLCKTY.UI;
using CLCKTY.UI.ViewModels;

namespace CLCKTY;

public partial class App : System.Windows.Application
{
	private IKeyboardHookService? _keyboardHook;
	private IMouseHookService? _mouseHook;
	private ISoundEngine? _soundEngine;
	private IAudioProfileManager? _profileManager;
	private ISettingsService? _settingsService;
	private ITrayService? _trayService;
	private PerformanceMonitorService? _performanceMonitor;
	private MainViewModel? _mainViewModel;
	private MainWindow? _mainWindow;

	private bool _ctrlPressed;
	private bool _altPressed;
	private bool _hotkeyLatched;
	private bool _isExiting;

	protected override async void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);

		ShutdownMode = ShutdownMode.OnExplicitShutdown;

		_settingsService = new SettingsService();
		_profileManager = new AudioProfileManager();
		_soundEngine = new SoundEngine();
		_keyboardHook = new KeyboardHookService();
		_mouseHook = new MouseHookService();
		_trayService = new TrayService();
		_performanceMonitor = new PerformanceMonitorService();

		_mainViewModel = new MainViewModel(_soundEngine, _profileManager, _settingsService);
		_mainWindow = new MainWindow(_mainViewModel);

		_trayService.OpenRequested += (_, _) => OpenMainWindow();
		_trayService.ToggleSoundsRequested += (_, enabled) => _mainViewModel.SetSoundsEnabledFromTray(enabled);
		_trayService.ExitRequested += (_, _) => ExitApplication();

		_mainViewModel.SoundsEnabledChanged += (_, enabled) => _trayService.SetSoundState(enabled);

		_keyboardHook.InputReceived += OnKeyboardInputReceived;
		_mouseHook.ClickReceived += OnMouseClickReceived;
		_performanceMonitor.CpuSampled += OnCpuSampled;

		await _mainViewModel.InitializeAsync().ConfigureAwait(true);
		_trayService.SetSoundState(_mainViewModel.SoundsEnabled);

		_keyboardHook.Start();
		_mouseHook.Start();
		_performanceMonitor.Start();

		if (!_mainViewModel.StartMinimizedToTray)
		{
			_mainWindow.Show();
		}
	}

	protected override void OnExit(ExitEventArgs e)
	{
		_performanceMonitor?.Stop();
		_performanceMonitor?.Dispose();

		_keyboardHook?.Dispose();
		_mouseHook?.Dispose();
		_soundEngine?.Dispose();
		_trayService?.Dispose();
		_mainViewModel?.Dispose();

		base.OnExit(e);
	}

	private void OpenMainWindow()
	{
		if (_mainWindow is null)
		{
			return;
		}

		Dispatcher.Invoke(_mainWindow.ShowControlPanel);
	}

	private void ExitApplication()
	{
		if (_isExiting)
		{
			return;
		}

		_isExiting = true;
		if (_mainWindow is not null)
		{
			_mainWindow.PrepareForExit();
			_mainWindow.Close();
		}

		Shutdown();
	}

	private void OnMouseClickReceived(object? sender, MouseInputEventArgs e)
	{
		_soundEngine?.PlayMouseClick(e.Button);
	}

	private void OnKeyboardInputReceived(object? sender, KeyboardInputEventArgs e)
	{
		if (_soundEngine is null)
		{
			return;
		}

		UpdateModifierStates(e);

		if (TryHandleToggleHotkey(e))
		{
			return;
		}

		if (e.IsKeyDown)
		{
			_soundEngine.PlayKeyDown(e.VirtualKeyCode);
		}
		else
		{
			_soundEngine.PlayKeyUp(e.VirtualKeyCode);
		}
	}

	private void UpdateModifierStates(KeyboardInputEventArgs e)
	{
		var isCtrl = e.VirtualKeyCode is 0x11 or 0xA2 or 0xA3;
		var isAlt = e.VirtualKeyCode is 0x12 or 0xA4 or 0xA5;

		if (isCtrl)
		{
			_ctrlPressed = e.IsKeyDown;
		}

		if (isAlt)
		{
			_altPressed = e.IsKeyDown;
		}
	}

	private bool TryHandleToggleHotkey(KeyboardInputEventArgs e)
	{
		if (_mainViewModel is null || !_mainViewModel.GlobalHotkeyEnabled)
		{
			return false;
		}

		const int vkM = 0x4D;
		if (e.IsKeyDown && e.VirtualKeyCode == vkM)
		{
			if (_ctrlPressed && _altPressed && !_hotkeyLatched)
			{
				_hotkeyLatched = true;
				_ = Dispatcher.BeginInvoke(() => _mainViewModel.ToggleSounds());
				return true;
			}
		}

		if (!e.IsKeyDown && e.VirtualKeyCode == vkM)
		{
			_hotkeyLatched = false;
		}

		return false;
	}

	private void OnCpuSampled(object? sender, double cpuUsagePercent)
	{
		if (_mainViewModel is null)
		{
			return;
		}

		_ = Dispatcher.BeginInvoke(() => _mainViewModel.UpdateCpuUsage(cpuUsagePercent));
	}
}

