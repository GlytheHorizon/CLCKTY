using System.Collections.ObjectModel;
using System.Windows.Input;
using CLCKTY.App.Core;
using CLCKTY.App.Services;
using Forms = System.Windows.Forms;

namespace CLCKTY.App.UI;

public sealed class MainViewModel : ViewModelBase
{
    private readonly ISoundEngine _soundEngine;
    private readonly StartupService _startupService;

    private SoundProfileDescriptor? _selectedProfile;
    private bool _isEnabled;
    private bool _startWithWindows;
    private double _volume;
    private string _statusText = "Ready";
    private bool _isImporting;

    public MainViewModel(ISoundEngine soundEngine, StartupService startupService)
    {
        _soundEngine = soundEngine;
        _startupService = startupService;

        Profiles = new ObservableCollection<SoundProfileDescriptor>();
        KeyMappings = new ObservableCollection<KeyMappingRowViewModel>();

        IsEnabled = _soundEngine.IsEnabled;
        Volume = _soundEngine.MasterVolume * 100d;
        StartWithWindows = _startupService.IsEnabled();

        ImportSoundPackCommand = new RelayCommand(_ => _ = ImportSoundPackAsync(), _ => !IsImporting);
        ClearMappingsCommand = new RelayCommand(_ => ClearMappings());

        BuildMappingRows();
        LoadProfiles(_soundEngine.ActiveProfileId);
        StatusText = "Listening globally. No keystrokes are stored.";
    }

    public event EventHandler<bool>? SoundEnabledChanged;

    public ObservableCollection<SoundProfileDescriptor> Profiles { get; }

    public ObservableCollection<KeyMappingRowViewModel> KeyMappings { get; }

    public ICommand ImportSoundPackCommand { get; }

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

    public bool IsImporting
    {
        get => _isImporting;
        private set
        {
            if (!SetProperty(ref _isImporting, value))
            {
                return;
            }

            if (ImportSoundPackCommand is RelayCommand relay)
            {
                relay.RaiseCanExecuteChanged();
            }
        }
    }

    private void BuildMappingRows()
    {
        var keys = new (string Label, int Vk)[]
        {
            ("Space", 0x20),
            ("Enter", 0x0D),
            ("Backspace", 0x08),
            ("Tab", 0x09),
            ("A", 0x41),
            ("S", 0x53),
            ("D", 0x44),
            ("F", 0x46)
        };

        foreach (var key in keys)
        {
            var row = new KeyMappingRowViewModel(key.Label, key.Vk);
            row.MappingChanged += OnMappingChanged;
            KeyMappings.Add(row);
        }
    }

    private void LoadProfiles(string preferredProfileId)
    {
        var profiles = _soundEngine.Profiles
            .OrderBy(profile => profile.DisplayName)
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

        foreach (var row in KeyMappings)
        {
            row.UpdateOptions(clipOptions);

            var currentMapping = _soundEngine.GetKeyMapping(row.VirtualKey);
            row.SelectedClipId = currentMapping;
        }
    }

    private void OnMappingChanged(object? sender, EventArgs e)
    {
        if (sender is not KeyMappingRowViewModel row)
        {
            return;
        }

        _soundEngine.SetKeyMapping(row.VirtualKey, row.SelectedClipId);
        StatusText = row.SelectedClipId is null
            ? $"{row.KeyLabel}: default clip"
            : $"{row.KeyLabel}: mapped to {row.SelectedClipId}";
    }

    private void ClearMappings()
    {
        _soundEngine.ClearMappings();

        foreach (var row in KeyMappings)
        {
            row.SelectedClipId = null;
        }

        StatusText = "Custom key mappings cleared.";
    }

    private async Task ImportSoundPackAsync()
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select a folder with .wav files (default.wav optional).",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        IsImporting = true;

        try
        {
            var importedProfileId = await _soundEngine.ImportSoundPackAsync(dialog.SelectedPath)
                .ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(importedProfileId))
            {
                StatusText = "No WAV files found in selected folder.";
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
            IsImporting = false;
        }
    }
}
