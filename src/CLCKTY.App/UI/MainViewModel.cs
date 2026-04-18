using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using CLCKTY.App.Core;
using CLCKTY.App.Services;
using Forms = System.Windows.Forms;

namespace CLCKTY.App.UI;

public sealed class MainViewModel : ViewModelBase
{
    private const KeyEventTrigger DefaultMappingTrigger = KeyEventTrigger.Down;
    private const int MaxLiveActivityEntries = 12;
    private const int MaxDashboardActivityEntries = 80;
    private const int MaxCategoryActivityEntries = 40;

    private readonly ISoundEngine _soundEngine;
    private readonly StartupService _startupService;
    private readonly IReadOnlyList<KeyMappingInputOption> _keyboardInputOptions;
    private readonly IReadOnlyList<KeyMappingInputOption> _mouseInputOptions;
    private readonly Dictionary<KeyMappingRowViewModel, (int inputCode, KeyEventTrigger trigger)> _rowBindings = new();
    private bool _isSynchronizingMappings;
    private KeyMappingRowViewModel? _capturingInputRow;
    private KeyMappingRowViewModel? _lastMouseCapturedRow;
    private DateTime _lastMouseCapturedAtUtc;

    private SoundProfileDescriptor? _selectedKeyboardProfile;
    private SoundProfileDescriptor? _selectedMouseProfile;
    private bool _isEnabled;
    private bool _isKeyboardSoundEnabled = true;
    private bool _isMouseSoundEnabled = true;
    private bool _startWithWindows;
    private double _volume;
    private double _keyboardVolume = 100d;
    private double _mouseVolume = 100d;
    private string _statusText = "Ready";
    private string _lastTriggeredPreview = "Press any key or click a mouse button to test.";
    private bool _isImportingPack;
    private bool _isImportingClip;

    public MainViewModel(ISoundEngine soundEngine, StartupService startupService)
    {
        _soundEngine = soundEngine;
        _startupService = startupService;

        _keyboardInputOptions = BuildKeyboardInputOptions();
        _mouseInputOptions = BuildMouseInputOptions();

        Profiles = new ObservableCollection<SoundProfileDescriptor>();
        KeyboardProfiles = new ObservableCollection<SoundProfileDescriptor>();
        MouseProfiles = new ObservableCollection<SoundProfileDescriptor>();
        KeyboardMappings = new ObservableCollection<KeyMappingRowViewModel>();
        MouseMappings = new ObservableCollection<KeyMappingRowViewModel>();
        ActivityFeed = new ObservableCollection<string>();
        DashboardActivityFeed = new ObservableCollection<string>();
        KeyboardActivityFeed = new ObservableCollection<string>();
        MouseActivityFeed = new ObservableCollection<string>();

        IsEnabled = _soundEngine.IsEnabled;
        Volume = _soundEngine.MasterVolume * 100d;
        StartWithWindows = _startupService.IsEnabled();

        ImportKeyboardSoundPackCommand = new RelayCommand(_ => _ = ImportSoundPackAsync(false), _ => !IsImportingPack);
        ImportMouseSoundPackCommand = new RelayCommand(_ => _ = ImportSoundPackAsync(true), _ => !IsImportingPack);
        ImportSoundPackCommand = ImportKeyboardSoundPackCommand;
        AddKeyboardMappingCommand = new RelayCommand(_ => AddKeyboardMapping());
        AddMouseMappingCommand = new RelayCommand(_ => AddMouseMapping());
        CaptureKeyboardInputCommand = new RelayCommand(parameter => BeginInputCapture(parameter as KeyMappingRowViewModel), parameter => parameter is KeyMappingRowViewModel);
        RemoveMappingCommand = new RelayCommand(parameter => RemoveMapping(parameter as KeyMappingRowViewModel), parameter => parameter is KeyMappingRowViewModel);
        ImportMappingAudioCommand = new RelayCommand(parameter => _ = ImportMappingAudioAsync(parameter as KeyMappingRowViewModel), parameter => parameter is KeyMappingRowViewModel && !IsImportingClip);
        RemoveRowSoundChoiceCommand = new RelayCommand(parameter => RemoveRowSoundChoice(parameter as KeyMappingRowViewModel), parameter => parameter is KeyMappingRowViewModel);
        RemoveClipOptionCommand = new RelayCommand(parameter => RemoveClipOption(parameter as KeyMappingOption), parameter => parameter is KeyMappingOption option && option.CanRemove);
        RemoveImportedProfileCommand = new RelayCommand(
            parameter => RemoveImportedProfile(parameter as SoundProfileDescriptor),
            parameter => parameter is SoundProfileDescriptor profile
                && profile.IsImported
                && !string.Equals(profile.SourceLabel, "custom", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(profile.SourceLabel, "custome", StringComparison.OrdinalIgnoreCase));
        ClearMappingsCommand = new RelayCommand(_ => ClearMappings());

        RemoveLegacyUpMappings();
        BuildMappingRows();
        LoadProfiles(_soundEngine.GetActiveProfileId(false), _soundEngine.GetActiveProfileId(true));

        StatusText = "Listening globally. No keystrokes are stored.";
    }

    public event EventHandler<bool>? SoundEnabledChanged;

    public event EventHandler<InputTriggeredPreviewEventArgs>? InputTriggered;

    public ObservableCollection<SoundProfileDescriptor> Profiles { get; }

    public ObservableCollection<SoundProfileDescriptor> KeyboardProfiles { get; }

    public ObservableCollection<SoundProfileDescriptor> MouseProfiles { get; }

    public ObservableCollection<KeyMappingRowViewModel> KeyboardMappings { get; }

    public ObservableCollection<KeyMappingRowViewModel> MouseMappings { get; }

    public ObservableCollection<string> ActivityFeed { get; }

    public ObservableCollection<string> DashboardActivityFeed { get; }

    public ObservableCollection<string> KeyboardActivityFeed { get; }

    public ObservableCollection<string> MouseActivityFeed { get; }

    public ICommand ImportSoundPackCommand { get; }

    public ICommand ImportKeyboardSoundPackCommand { get; }

    public ICommand ImportMouseSoundPackCommand { get; }

    public ICommand AddKeyboardMappingCommand { get; }

    public ICommand AddMouseMappingCommand { get; }

    public ICommand CaptureKeyboardInputCommand { get; }

    public ICommand RemoveMappingCommand { get; }

    public ICommand ImportMappingAudioCommand { get; }

    public ICommand RemoveRowSoundChoiceCommand { get; }

    public ICommand RemoveClipOptionCommand { get; }

    public ICommand RemoveImportedProfileCommand { get; }

    public ICommand ClearMappingsCommand { get; }

    public SoundProfileDescriptor? SelectedKeyboardProfile
    {
        get => _selectedKeyboardProfile;
        set
        {
            if (!SetProperty(ref _selectedKeyboardProfile, value) || value is null)
            {
                return;
            }

            _soundEngine.SetActiveProfile(value.Id, false);
            UpdateMappingOptions();
            StatusText = $"Keyboard profile: {value.DisplayName}";
        }
    }

    public SoundProfileDescriptor? SelectedMouseProfile
    {
        get => _selectedMouseProfile;
        set
        {
            if (!SetProperty(ref _selectedMouseProfile, value) || value is null)
            {
                return;
            }

            _soundEngine.SetActiveProfile(value.Id, true);
            UpdateMappingOptions();
            StatusText = $"Mouse profile: {value.DisplayName}";
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
        private set
        {
            if (!SetProperty(ref _statusText, value))
            {
                return;
            }

            AppendDashboardActivity(value);
            AppendActivity(value);
        }
    }

    public double KeyboardVolume
    {
        get => _keyboardVolume;
        set
        {
            var clamped = Math.Clamp(value, 0d, 100d);
            if (!SetProperty(ref _keyboardVolume, clamped))
            {
                return;
            }
        }
    }

    public double MouseVolume
    {
        get => _mouseVolume;
        set
        {
            var clamped = Math.Clamp(value, 0d, 100d);
            if (!SetProperty(ref _mouseVolume, clamped))
            {
                return;
            }
        }
    }

    public string LastTriggeredPreview
    {
        get => _lastTriggeredPreview;
        private set => SetProperty(ref _lastTriggeredPreview, value);
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

            if (ImportKeyboardSoundPackCommand is RelayCommand importKeyboardRelay)
            {
                importKeyboardRelay.RaiseCanExecuteChanged();
            }

            if (ImportMouseSoundPackCommand is RelayCommand importMouseRelay)
            {
                importMouseRelay.RaiseCanExecuteChanged();
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

        var normalizedMappings = _soundEngine.GetMappings()
            .GroupBy(mapping => mapping.InputCode)
            .Select(group => group.FirstOrDefault(mapping => mapping.Trigger == DefaultMappingTrigger) ?? group.First())
            .ToList();

        foreach (var mapping in normalizedMappings)
        {
            var isMouseMapping = InputBindingCode.IsMouseCode(mapping.InputCode);
            var row = CreateMappingRow(isMouseMapping, mapping.InputCode, DefaultMappingTrigger, mapping.ClipId);
            GetCollection(isMouseMapping).Add(row);
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
        var row = CreateMappingRow(false, nextCode, DefaultMappingTrigger);
        KeyboardMappings.Add(row);
        UpdateMappingOptions();
        BeginInputCapture(row);
    }

    private void AddMouseMapping()
    {
        var nextCode = GetNextAvailableCode(MouseMappings, _mouseInputOptions);
        var row = CreateMappingRow(true, nextCode, DefaultMappingTrigger);
        MouseMappings.Add(row);
        UpdateMappingOptions();
        BeginInputCapture(row);
        StatusText = "Added mouse mapping row. Click a mouse button to bind input.";
    }

    private static int GetNextAvailableCode(
        IEnumerable<KeyMappingRowViewModel> existingRows,
        IReadOnlyList<KeyMappingInputOption> options)
    {
        var used = existingRows.Select(row => row.InputCode).ToHashSet();
        return options.FirstOrDefault(option => !used.Contains(option.Code))?.Code ?? options[0].Code;
    }

    private void RemoveLegacyUpMappings()
    {
        foreach (var mapping in _soundEngine.GetMappings().Where(mapping => mapping.Trigger == KeyEventTrigger.Up))
        {
            _soundEngine.SetKeyMapping(mapping.InputCode, null, KeyEventTrigger.Up);
        }
    }

    private void RemoveMapping(KeyMappingRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (ReferenceEquals(_capturingInputRow, row))
        {
            _capturingInputRow.IsCapturingInput = false;
            _capturingInputRow = null;
        }

        if (_rowBindings.TryGetValue(row, out var previousBinding))
        {
            _soundEngine.SetKeyMapping(previousBinding.inputCode, null, previousBinding.trigger);
            _rowBindings.Remove(row);
        }

        _soundEngine.SetKeyMapping(row.InputCode, null, DefaultMappingTrigger);
        row.MappingChanged -= OnMappingChanged;

        var collection = GetCollection(row.IsMouseMapping);
        if (!collection.Remove(row))
        {
            return;
        }

        StatusText = $"Removed mapping for {row.InputLabel}.";
    }

    public bool TryCaptureKeyboardInput(int virtualKey)
    {
        if (_capturingInputRow is null || _capturingInputRow.IsMouseMapping)
        {
            return false;
        }

        var row = _capturingInputRow;
        _capturingInputRow = null;

        row.IsCapturingInput = false;
        row.InputCode = virtualKey;
        StatusText = $"Captured key: {row.InputLabel}.";
        return true;
    }

    public bool TryCaptureMouseInput(int inputCode)
    {
        if (_capturingInputRow is null || !_capturingInputRow.IsMouseMapping)
        {
            return false;
        }

        var row = _capturingInputRow;
        _capturingInputRow = null;
        _lastMouseCapturedRow = row;
        _lastMouseCapturedAtUtc = DateTime.UtcNow;

        row.IsCapturingInput = false;
        row.InputCode = inputCode;
        StatusText = $"Captured mouse input: {row.InputLabel}.";
        return true;
    }

    public void RefreshProfiles()
    {
        LoadProfiles(_soundEngine.GetActiveProfileId(false), _soundEngine.GetActiveProfileId(true));
        UpdateMappingOptions();
    }

    public void ReportInputTriggered(int inputCode, KeyEventTrigger trigger = KeyEventTrigger.Down)
    {
        var isMouseInput = InputBindingCode.IsMouseCode(inputCode);
        var categoryEnabled = isMouseInput ? IsMouseSoundEnabled : IsKeyboardSoundEnabled;

        if (!IsEnabled || !categoryEnabled)
        {
            return;
        }

        var inputLabel = isMouseInput ? DescribeMouseInput(inputCode) : DescribeVirtualKey(inputCode);
        var clipDisplayName = _soundEngine.GetPlaybackClipDisplayName(inputCode, trigger);

        LastTriggeredPreview = $"{inputLabel} = {clipDisplayName}";
        var triggerLabel = trigger == KeyEventTrigger.Down ? "press" : "release";
        AppendActivity($"{(isMouseInput ? "Mouse" : "Keyboard")} {triggerLabel}: {inputLabel} = {clipDisplayName}", isMouseInput);

        var intensity = Math.Clamp(Volume / 100d, 0.2d, 1.0d);
        InputTriggered?.Invoke(this, new InputTriggeredPreviewEventArgs(inputCode, trigger, intensity));
    }

    private static string DescribeMouseInput(int inputCode)
    {
        return inputCode switch
        {
            InputBindingCode.MouseLeft => "Mouse Left",
            InputBindingCode.MouseRight => "Mouse Right",
            InputBindingCode.MouseMiddle => "Mouse Middle",
            InputBindingCode.MouseX1 => "Mouse X1",
            InputBindingCode.MouseX2 => "Mouse X2",
            _ => $"Mouse 0x{inputCode:X}"
        };
    }

    private void BeginInputCapture(KeyMappingRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        // When a mouse row captures using a click on the same button,
        // WPF click command can fire right after capture and accidentally re-arm capture.
        if (row.IsMouseMapping
            && ReferenceEquals(row, _lastMouseCapturedRow)
            && DateTime.UtcNow - _lastMouseCapturedAtUtc < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        if (_capturingInputRow is not null)
        {
            _capturingInputRow.IsCapturingInput = false;
        }

        _capturingInputRow = row;
        row.IsCapturingInput = true;
        StatusText = row.IsMouseMapping
            ? "Click a mouse button to set this mapping input."
            : "Press a key to set this mapping input.";
    }

    private void LoadProfiles(string preferredKeyboardProfileId, string preferredMouseProfileId)
    {
        var keyboardProfiles = _soundEngine.GetProfiles(false)
            .OrderBy(profile => profile.IsImported)
            .ThenBy(profile => profile.DisplayName)
            .ToList();

        var mouseProfiles = _soundEngine.GetProfiles(true)
            .OrderBy(profile => profile.IsImported)
            .ThenBy(profile => profile.DisplayName)
            .ToList();

        KeyboardProfiles.Clear();
        MouseProfiles.Clear();
        Profiles.Clear();

        foreach (var profile in keyboardProfiles)
        {
            KeyboardProfiles.Add(profile);
            Profiles.Add(profile);
        }

        foreach (var profile in mouseProfiles)
        {
            MouseProfiles.Add(profile);
            Profiles.Add(profile);
        }

        SelectedKeyboardProfile = KeyboardProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, preferredKeyboardProfileId, StringComparison.OrdinalIgnoreCase))
            ?? KeyboardProfiles.FirstOrDefault();

        SelectedMouseProfile = MouseProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, preferredMouseProfileId, StringComparison.OrdinalIgnoreCase))
            ?? MouseProfiles.FirstOrDefault();
    }

    private void UpdateMappingOptions()
    {
        var keyboardClipOptions = BuildClipOptions(false);
        var mouseClipOptions = BuildClipOptions(true);

        UpdateOptionsForRows(KeyboardMappings, keyboardClipOptions);
        UpdateOptionsForRows(MouseMappings, mouseClipOptions);
    }

    private List<KeyMappingOption> BuildClipOptions(bool isMouseMapping)
    {
        var clipOptions = new List<KeyMappingOption>
        {
            new(null, "Default", "Profile")
        };

        var orderedClips = _soundEngine.GetClipOptions(isMouseMapping)
            .OrderByDescending(clip => string.Equals(clip.SourceLabel, "Uploaded", StringComparison.OrdinalIgnoreCase))
            .ThenBy(clip => clip.DisplayName)
            .ToList();

        foreach (var clip in orderedClips)
        {
            clipOptions.Add(new KeyMappingOption(clip.Id, clip.DisplayName, clip.SourceLabel, clip.CanRemove));
        }

        return clipOptions;
    }

    private void UpdateOptionsForRows(IEnumerable<KeyMappingRowViewModel> rows, IReadOnlyCollection<KeyMappingOption> options)
    {
        foreach (var row in rows)
        {
            var previousClipId = row.SelectedClipId;
            row.UpdateOptions(options);

            if (previousClipId is not null && row.SelectedClipId is null)
            {
                _soundEngine.SetKeyMapping(row.InputCode, null, DefaultMappingTrigger);
            }
        }
    }

    private void RemoveImportedProfile(SoundProfileDescriptor? profile)
    {
        if (profile is null || !profile.IsImported)
        {
            return;
        }

        if (string.Equals(profile.SourceLabel, "custom", StringComparison.OrdinalIgnoreCase)
            || string.Equals(profile.SourceLabel, "custome", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "Custom profiles cannot be deleted from Sound Profiles.";
            return;
        }

        var confirmDialog = new ConfirmActionDialog(
            "Delete Imported Pack",
            $"Delete imported pack '{profile.DisplayName}'?",
            "Delete Pack")
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };

        if (confirmDialog.ShowDialog() != true)
        {
            return;
        }

        TryRemoveImportedProfile(profile);
    }

    public bool TryRemoveImportedProfile(SoundProfileDescriptor? profile)
    {
        if (profile is null || !profile.IsImported)
        {
            return false;
        }

        if (!_soundEngine.RemoveImportedProfile(profile.Id))
        {
            StatusText = $"Could not remove {profile.DisplayName}.";
            return false;
        }

        LoadProfiles(_soundEngine.GetActiveProfileId(false), _soundEngine.GetActiveProfileId(true));
        StatusText = $"Removed imported pack: {profile.DisplayName}.";
        return true;
    }

    private void RemoveClipOption(KeyMappingOption? option)
    {
        if (option?.Id is null)
        {
            return;
        }

        var removeFromKeyboard = _soundEngine.GetClipOptions(false)
            .Any(clip => string.Equals(clip.Id, option.Id, StringComparison.OrdinalIgnoreCase) && clip.CanRemove);

        var removeFromMouse = !removeFromKeyboard && _soundEngine.GetClipOptions(true)
            .Any(clip => string.Equals(clip.Id, option.Id, StringComparison.OrdinalIgnoreCase) && clip.CanRemove);

        if (!removeFromKeyboard && !removeFromMouse)
        {
            StatusText = $"{option.DisplayName} cannot be deleted.";
            return;
        }

        var confirmDialog = new ConfirmActionDialog(
            "Delete Sound Choice",
            $"Remove '{option.DisplayLabel}' from this profile?",
            "Delete")
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };

        if (confirmDialog.ShowDialog() != true)
        {
            return;
        }

        var removed = _soundEngine.RemoveClipFromActiveProfile(option.Id, removeFromMouse);
        if (!removed)
        {
            StatusText = $"Could not remove {option.DisplayName}.";
            return;
        }

        UpdateMappingOptions();
        StatusText = $"Removed {option.DisplayName} from choices.";
    }

    private void RemoveRowSoundChoice(KeyMappingRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(row.SelectedClipId))
        {
            StatusText = $"{row.InputLabel} already uses default sound.";
            return;
        }

        var clipOption = _soundEngine.GetClipOptions(row.IsMouseMapping)
            .FirstOrDefault(clip => string.Equals(clip.Id, row.SelectedClipId, StringComparison.OrdinalIgnoreCase));

        if (clipOption is null)
        {
            row.SelectedClipId = null;
            StatusText = $"{row.InputLabel}: reverted to default sound.";
            return;
        }

        if (!clipOption.CanRemove)
        {
            row.SelectedClipId = null;
            StatusText = $"{row.InputLabel}: reverted to default sound.";
            return;
        }

        var confirmDialog = new ConfirmActionDialog(
            "Delete Sound Choice",
            $"Delete '{clipOption.DisplayName}' from this {(row.IsMouseMapping ? "mouse" : "keyboard")} profile?",
            "Delete")
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };

        if (confirmDialog.ShowDialog() != true)
        {
            return;
        }

        if (!_soundEngine.RemoveClipFromActiveProfile(clipOption.Id, row.IsMouseMapping))
        {
            StatusText = $"Could not remove {clipOption.DisplayName}.";
            return;
        }

        UpdateMappingOptions();
        StatusText = $"Removed {clipOption.DisplayName} from {(row.IsMouseMapping ? "mouse" : "keyboard")} choices.";
    }

    private void OnMappingChanged(object? sender, EventArgs e)
    {
        if (_isSynchronizingMappings || sender is not KeyMappingRowViewModel row)
        {
            return;
        }

        if (row.MappingTrigger != DefaultMappingTrigger)
        {
            row.MappingTrigger = DefaultMappingTrigger;
            return;
        }

        if (_rowBindings.TryGetValue(row, out var previousBinding)
            && (previousBinding.inputCode != row.InputCode || previousBinding.trigger != DefaultMappingTrigger))
        {
            _soundEngine.SetKeyMapping(previousBinding.inputCode, null, previousBinding.trigger);
        }

        _soundEngine.SetKeyMapping(row.InputCode, row.SelectedClipId, DefaultMappingTrigger);
        _rowBindings[row] = (row.InputCode, DefaultMappingTrigger);

        if (row.SelectedClipId is not null)
        {
            RemoveDuplicateCustomMappings(row);
        }

        if (row.SelectedClipId is null)
        {
            StatusText = $"{row.InputLabel}: default clip.";
            return;
        }

        var clipName = row.Options.FirstOrDefault(option =>
                string.Equals(option.Id, row.SelectedClipId, StringComparison.OrdinalIgnoreCase))
            ?.DisplayLabel
            ?? row.SelectedClipId;

        StatusText = $"{row.InputLabel}: {clipName}";
    }

    private void RemoveDuplicateCustomMappings(KeyMappingRowViewModel sourceRow)
    {
        _isSynchronizingMappings = true;

        try
        {
            foreach (var row in GetAllRows())
            {
                if (ReferenceEquals(row, sourceRow)
                    || row.SelectedClipId is null
                    || row.InputCode != sourceRow.InputCode
                    || row.MappingTrigger != DefaultMappingTrigger)
                {
                    continue;
                }

                row.SelectedClipId = null;
                _soundEngine.SetKeyMapping(row.InputCode, null, DefaultMappingTrigger);
                _rowBindings[row] = (row.InputCode, DefaultMappingTrigger);
            }
        }
        finally
        {
            _isSynchronizingMappings = false;
        }
    }

    private void ClearMappings()
    {
        if (_capturingInputRow is not null)
        {
            _capturingInputRow.IsCapturingInput = false;
            _capturingInputRow = null;
        }

        _soundEngine.ClearMappings();

        foreach (var row in GetAllRows())
        {
            row.SelectedClipId = null;
        }

        StatusText = "Custom key and mouse mappings cleared.";
    }

    private void AppendActivity(string message, bool? isMouseActivity = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var entry = $"{DateTime.Now:HH:mm:ss}  {message}";

        ActivityFeed.Insert(0, entry);
        TrimActivityFeed(ActivityFeed, MaxLiveActivityEntries);

        if (isMouseActivity == true)
        {
            MouseActivityFeed.Insert(0, entry);
            TrimActivityFeed(MouseActivityFeed, MaxCategoryActivityEntries);
            return;
        }

        if (isMouseActivity == false)
        {
            KeyboardActivityFeed.Insert(0, entry);
            TrimActivityFeed(KeyboardActivityFeed, MaxCategoryActivityEntries);
            return;
        }

        var lowerMessage = message.ToLowerInvariant();

        if (lowerMessage.Contains("mouse", StringComparison.Ordinal))
        {
            MouseActivityFeed.Insert(0, entry);
            TrimActivityFeed(MouseActivityFeed, MaxCategoryActivityEntries);
        }

        if (lowerMessage.Contains("keyboard", StringComparison.Ordinal)
            || lowerMessage.Contains("key", StringComparison.Ordinal))
        {
            KeyboardActivityFeed.Insert(0, entry);
            TrimActivityFeed(KeyboardActivityFeed, MaxCategoryActivityEntries);
        }
    }

    private void AppendDashboardActivity(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var entry = $"{DateTime.Now:HH:mm:ss}  {message}";
        DashboardActivityFeed.Insert(0, entry);
        TrimActivityFeed(DashboardActivityFeed, MaxDashboardActivityEntries);
    }

    private static void TrimActivityFeed(ObservableCollection<string> feed, int maxEntries)
    {
        while (feed.Count > maxEntries)
        {
            feed.RemoveAt(feed.Count - 1);
        }
    }

    private async Task ImportSoundPackAsync(bool isMouseProfile)
    {
        var profileLabel = isMouseProfile ? "mouse" : "keyboard";

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = $"Select a folder with {profileLabel} sounds (.wav files or config.json + timeline audio).",
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
            var importedProfileId = await _soundEngine.ImportSoundPackAsync(dialog.SelectedPath, isMouseProfile)
                .ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(importedProfileId))
            {
                StatusText = "No valid sound pack found (expected WAV files or config.json).";
                return;
            }

            LoadProfiles(_soundEngine.GetActiveProfileId(false), _soundEngine.GetActiveProfileId(true));
            StatusText = $"Custom {profileLabel} sound pack imported.";
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
            var importedClipId = await _soundEngine.ImportAudioClipAsync(dialog.FileName, row.IsMouseMapping)
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

public sealed class InputTriggeredPreviewEventArgs : EventArgs
{
    public InputTriggeredPreviewEventArgs(int inputCode, KeyEventTrigger trigger, double intensity)
    {
        InputCode = inputCode;
        Trigger = trigger;
        Intensity = intensity;
    }

    public int InputCode { get; }

    public KeyEventTrigger Trigger { get; }

    public double Intensity { get; }
}
