using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CLCKTY.App.Core;
using CLCKTY.App.Services;
using Forms = System.Windows.Forms;

namespace CLCKTY.App.UI;

public sealed class MainViewModel : ViewModelBase
{
    private readonly ISoundEngine _soundEngine;
    private readonly StartupService _startupService;
    private readonly IReadOnlyList<KeyMappingInputOption> _keyboardInputOptions;
    private readonly IReadOnlyList<KeyMappingInputOption> _mouseInputOptions;
    private readonly Dictionary<KeyMappingRowViewModel, (int inputCode, KeyEventTrigger trigger)> _rowBindings = new();

    private SoundProfileDescriptor? _selectedProfile;
    private bool _isEnabled;
    private bool _isKeyboardSoundEnabled = true;
    private bool _isMouseSoundEnabled = true;
    private bool _startWithWindows;
    private double _volume;
    private string _statusText = "Ready";
    private bool _isImportingPack;
    private bool _isImportingClip;

    public MainViewModel(ISoundEngine soundEngine, StartupService startupService)
    {
        _soundEngine = soundEngine;
        _startupService = startupService;

        _keyboardInputOptions = BuildKeyboardInputOptions();
        _mouseInputOptions = BuildMouseInputOptions();

        Profiles = new ObservableCollection<SoundProfileDescriptor>();
        KeyboardMappings = new ObservableCollection<KeyMappingRowViewModel>();
        MouseMappings = new ObservableCollection<KeyMappingRowViewModel>();

        IsEnabled = _soundEngine.IsEnabled;
        Volume = _soundEngine.MasterVolume * 100d;
        StartWithWindows = _startupService.IsEnabled();

        ImportSoundPackCommand = new RelayCommand(_ => _ = ImportSoundPackAsync(), _ => !IsImportingPack);
        AddKeyboardMappingCommand = new RelayCommand(_ => AddKeyboardMapping());
        AddMouseMappingCommand = new RelayCommand(_ => AddMouseMapping());
        RemoveMappingCommand = new RelayCommand(parameter => RemoveMapping(parameter as KeyMappingRowViewModel), parameter => parameter is KeyMappingRowViewModel);
        ImportMappingAudioCommand = new RelayCommand(parameter => _ = ImportMappingAudioAsync(parameter as KeyMappingRowViewModel), parameter => parameter is KeyMappingRowViewModel && !IsImportingClip);
        ClearMappingsCommand = new RelayCommand(_ => ClearMappings());

        BuildMappingRows();
        LoadProfiles(_soundEngine.ActiveProfileId);

        StatusText = "Listening globally. No keystrokes are stored.";
    }

    public event EventHandler<bool>? SoundEnabledChanged;

    public ObservableCollection<SoundProfileDescriptor> Profiles { get; }

    public ObservableCollection<KeyMappingRowViewModel> KeyboardMappings { get; }

    public ObservableCollection<KeyMappingRowViewModel> MouseMappings { get; }

    public ICommand ImportSoundPackCommand { get; }

    public ICommand AddKeyboardMappingCommand { get; }

    public ICommand AddMouseMappingCommand { get; }

    public ICommand RemoveMappingCommand { get; }

    public ICommand ImportMappingAudioCommand { get; }

    public ICommand ClearMappingsCommand { get; }

    public SoundProfileDescriptor? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value) || value is null)
            {
                return;
            }

            _soundEngine.SetActiveProfile(value.Id);
            UpdateMappingOptions();
            StatusText = $"Active profile: {value.DisplayName}";
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value))
            {
                return;
            }

            _soundEngine.IsEnabled = value;
            SoundEnabledChanged?.Invoke(this, value);
            StatusText = value ? "Sound engine enabled." : "Sound engine muted.";
        }
    }

    public bool IsKeyboardSoundEnabled
    {
        get => _isKeyboardSoundEnabled;
        set
        {
            if (!SetProperty(ref _isKeyboardSoundEnabled, value))
            {
                return;
            }

            StatusText = value ? "Keyboard sounds enabled." : "Keyboard sounds disabled.";
        }
    }

    public bool IsMouseSoundEnabled
    {
        get => _isMouseSoundEnabled;
        set
        {
            if (!SetProperty(ref _isMouseSoundEnabled, value))
            {
                return;
            }

            StatusText = value ? "Mouse sounds enabled." : "Mouse sounds disabled.";
        }
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!SetProperty(ref _startWithWindows, value))
            {
                return;
            }

            _startupService.SetEnabled(value);
            StatusText = value ? "Launch on startup enabled." : "Launch on startup disabled.";
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0d, 100d);
            if (!SetProperty(ref _volume, clamped))
            {
                return;
            }

            _soundEngine.MasterVolume = (float)(clamped / 100d);
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsImportingPack
    {
        get => _isImportingPack;
        private set
        {
            if (!SetProperty(ref _isImportingPack, value))
            {
                return;
            }

            if (ImportSoundPackCommand is RelayCommand importPackRelay)
            {
                importPackRelay.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsImportingClip
    {
        get => _isImportingClip;
        private set
        {
            if (!SetProperty(ref _isImportingClip, value))
            {
                return;
            }

            if (ImportMappingAudioCommand is RelayCommand importClipRelay)
            {
                importClipRelay.RaiseCanExecuteChanged();
            }
        }
    }

    private void BuildMappingRows()
    {
        _rowBindings.Clear();
        KeyboardMappings.Clear();
        MouseMappings.Clear();

        foreach (var mapping in _soundEngine.GetMappings())
        {
            var isMouseMapping = InputBindingCode.IsMouseCode(mapping.InputCode);
            var row = CreateMappingRow(isMouseMapping, mapping.InputCode, mapping.Trigger, mapping.ClipId);
            GetCollection(isMouseMapping).Add(row);
        }

        if (KeyboardMappings.Count == 0)
        {
            KeyboardMappings.Add(CreateMappingRow(false, _keyboardInputOptions[0].Code));
        }

        if (MouseMappings.Count == 0)
        {
            MouseMappings.Add(CreateMappingRow(true, _mouseInputOptions[0].Code));
        }
    }

    private KeyMappingRowViewModel CreateMappingRow(
        bool isMouseMapping,
        int inputCode,
        KeyEventTrigger trigger = KeyEventTrigger.Down,
        string? selectedClipId = null)
    {
        var inputOptions = isMouseMapping ? _mouseInputOptions : _keyboardInputOptions;
        var row = new KeyMappingRowViewModel(inputOptions, isMouseMapping, inputCode, trigger, selectedClipId);
        row.MappingChanged += OnMappingChanged;
        _rowBindings[row] = (row.InputCode, row.MappingTrigger);
        return row;
    }

    private ObservableCollection<KeyMappingRowViewModel> GetCollection(bool isMouseMapping)
    {
        return isMouseMapping ? MouseMappings : KeyboardMappings;
    }

    private IEnumerable<KeyMappingRowViewModel> GetAllRows()
    {
        foreach (var row in KeyboardMappings)
        {
            yield return row;
        }

        foreach (var row in MouseMappings)
        {
            yield return row;
        }
    }

    private void AddKeyboardMapping()
    {
        var nextCode = GetNextAvailableCode(KeyboardMappings, _keyboardInputOptions);
        var row = CreateMappingRow(false, nextCode);
        KeyboardMappings.Add(row);
        UpdateMappingOptions();
        StatusText = "Added keyboard mapping row.";
    }

    private void AddMouseMapping()
    {
        var nextCode = GetNextAvailableCode(MouseMappings, _mouseInputOptions);
        var row = CreateMappingRow(true, nextCode);
        MouseMappings.Add(row);
        UpdateMappingOptions();
        StatusText = "Added mouse mapping row.";
    }

    private static int GetNextAvailableCode(
        IEnumerable<KeyMappingRowViewModel> existingRows,
        IReadOnlyList<KeyMappingInputOption> options)
    {
        var used = existingRows.Select(row => row.InputCode).ToHashSet();
        return options.FirstOrDefault(option => !used.Contains(option.Code))?.Code ?? options[0].Code;
    }

    private void RemoveMapping(KeyMappingRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (_rowBindings.TryGetValue(row, out var previousBinding))
        {
            _soundEngine.SetKeyMapping(previousBinding.inputCode, null, previousBinding.trigger);
            _rowBindings.Remove(row);
        }

        _soundEngine.SetKeyMapping(row.InputCode, null, row.MappingTrigger);
        row.MappingChanged -= OnMappingChanged;

        var collection = GetCollection(row.IsMouseMapping);
        if (!collection.Remove(row))
        {
            return;
        }

        if (collection.Count == 0)
        {
            var seedCode = row.IsMouseMapping ? _mouseInputOptions[0].Code : _keyboardInputOptions[0].Code;
            collection.Add(CreateMappingRow(row.IsMouseMapping, seedCode));
        }

        StatusText = $"Removed mapping for {row.InputLabel}.";
    }

    private void LoadProfiles(string preferredProfileId)
    {
        var profiles = _soundEngine.Profiles
            .OrderBy(profile => profile.IsImported)
            .ThenBy(profile => profile.DisplayName)
            .ToList();

        Profiles.Clear();

        foreach (var profile in profiles)
        {
            Profiles.Add(profile);
        }

        SelectedProfile = Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, preferredProfileId, StringComparison.OrdinalIgnoreCase))
            ?? Profiles.FirstOrDefault();
    }

    private void UpdateMappingOptions()
    {
        var clipOptions = new List<KeyMappingOption>
        {
            new(null, "Default")
        };

        foreach (var clip in _soundEngine.GetClipOptions())
        {
            clipOptions.Add(new KeyMappingOption(clip.Id, clip.DisplayName));
        }

        foreach (var row in GetAllRows())
        {
            var previousClipId = row.SelectedClipId;
            row.UpdateOptions(clipOptions);

            if (previousClipId is not null && row.SelectedClipId is null)
            {
                _soundEngine.SetKeyMapping(row.InputCode, null, row.MappingTrigger);
            }
        }
    }

    private void OnMappingChanged(object? sender, EventArgs e)
    {
        if (sender is not KeyMappingRowViewModel row)
        {
            return;
        }

        if (_rowBindings.TryGetValue(row, out var previousBinding)
            && (previousBinding.inputCode != row.InputCode || previousBinding.trigger != row.MappingTrigger))
        {
            _soundEngine.SetKeyMapping(previousBinding.inputCode, null, previousBinding.trigger);
        }

        _soundEngine.SetKeyMapping(row.InputCode, row.SelectedClipId, row.MappingTrigger);
        _rowBindings[row] = (row.InputCode, row.MappingTrigger);

        if (row.SelectedClipId is null)
        {
            StatusText = $"{row.InputLabel} ({row.MappingTrigger}): default clip.";
            return;
        }

        var clipName = row.Options.FirstOrDefault(option =>
                string.Equals(option.Id, row.SelectedClipId, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName
            ?? row.SelectedClipId;

        StatusText = $"{row.InputLabel} ({row.MappingTrigger}): {clipName}";
    }

    private void ClearMappings()
    {
        _soundEngine.ClearMappings();

        foreach (var row in GetAllRows())
        {
            row.SelectedClipId = null;
        }

        StatusText = "Custom key and mouse mappings cleared.";
    }

    private async Task ImportSoundPackAsync()
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select a folder with .wav files or config.json + timeline audio.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        IsImportingPack = true;

        try
        {
            var importedProfileId = await _soundEngine.ImportSoundPackAsync(dialog.SelectedPath)
                .ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(importedProfileId))
            {
                StatusText = "No valid sound pack found (expected WAV files or config.json).";
                return;
            }

            LoadProfiles(importedProfileId);
            StatusText = "Custom sound pack imported.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Sound pack import canceled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed: {ex.Message}";
        }
        finally
        {
            IsImportingPack = false;
        }
    }

    private async Task ImportMappingAudioAsync(KeyMappingRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        using var dialog = new Forms.OpenFileDialog
        {
            Title = "Choose an audio file for this mapping",
            Filter = "Audio files|*.wav;*.mp3;*.ogg;*.oga;*.flac;*.aac;*.m4a|All files|*.*",
            Multiselect = false,
            CheckFileExists = true,
            RestoreDirectory = true
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        IsImportingClip = true;

        try
        {
            var importedClipId = await _soundEngine.ImportAudioClipAsync(dialog.FileName)
                .ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(importedClipId))
            {
                StatusText = "Unable to import that audio file.";
                return;
            }

            UpdateMappingOptions();
            row.SelectedClipId = importedClipId;
            StatusText = $"Mapped {row.InputLabel} to {Path.GetFileName(dialog.FileName)}.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Audio import canceled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Audio import failed: {ex.Message}";
        }
        finally
        {
            IsImportingClip = false;
        }
    }

    private static IReadOnlyList<KeyMappingInputOption> BuildMouseInputOptions()
    {
        return new List<KeyMappingInputOption>
        {
            new KeyMappingInputOption(InputBindingCode.MouseLeft, "Mouse Left"),
            new KeyMappingInputOption(InputBindingCode.MouseRight, "Mouse Right"),
            new KeyMappingInputOption(InputBindingCode.MouseMiddle, "Mouse Middle"),
            new KeyMappingInputOption(InputBindingCode.MouseX1, "Mouse X1 (Back)"),
            new KeyMappingInputOption(InputBindingCode.MouseX2, "Mouse X2 (Forward)")
        };
    }

    private static IReadOnlyList<KeyMappingInputOption> BuildKeyboardInputOptions()
    {
        var options = new List<KeyMappingInputOption>();

        for (var code = 0x08; code <= 0xFE; code++)
        {
            options.Add(new KeyMappingInputOption(code, DescribeVirtualKey(code)));
        }

        return options;
    }

    private static string DescribeVirtualKey(int virtualKey)
    {
        if (virtualKey >= 0x30 && virtualKey <= 0x39)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey >= 0x41 && virtualKey <= 0x5A)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey >= 0x60 && virtualKey <= 0x69)
        {
            return $"Numpad {virtualKey - 0x60}";
        }

        if (virtualKey >= 0x70 && virtualKey <= 0x87)
        {
            return $"F{virtualKey - 0x6F}";
        }

        return virtualKey switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0C => "Clear",
            0x0D => "Enter",
            0x10 => "Shift",
            0x11 => "Ctrl",
            0x12 => "Alt",
            0x13 => "Pause",
            0x14 => "Caps Lock",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "Page Up",
            0x22 => "Page Down",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left Arrow",
            0x26 => "Up Arrow",
            0x27 => "Right Arrow",
            0x28 => "Down Arrow",
            0x29 => "Select",
            0x2A => "Print",
            0x2B => "Execute",
            0x2C => "Print Screen",
            0x2D => "Insert",
            0x2E => "Delete",
            0x5B => "Left Windows",
            0x5C => "Right Windows",
            0x5D => "Applications",
            0x6A => "Numpad *",
            0x6B => "Numpad +",
            0x6C => "Separator",
            0x6D => "Numpad -",
            0x6E => "Numpad .",
            0x6F => "Numpad /",
            0x90 => "Num Lock",
            0x91 => "Scroll Lock",
            0xA0 => "Left Shift",
            0xA1 => "Right Shift",
            0xA2 => "Left Ctrl",
            0xA3 => "Right Ctrl",
            0xA4 => "Left Alt",
            0xA5 => "Right Alt",
            0xBA => "; :",
            0xBB => "= +",
            0xBC => ", <",
            0xBD => "- _",
            0xBE => ". >",
            0xBF => "/ ?",
            0xC0 => "` ~",
            0xDB => "[ {",
            0xDC => "\\ |",
            0xDD => "] }",
            0xDE => "' \"",
            0xE2 => "OEM 102",
            _ => $"VK 0x{virtualKey:X2}"
        };
    }
}
