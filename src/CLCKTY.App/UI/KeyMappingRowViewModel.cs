using System.Collections.ObjectModel;
using CLCKTY.App.Core;

namespace CLCKTY.App.UI;

public sealed class KeyMappingRowViewModel : ViewModelBase
{
    private int _inputCode;
    private string? _selectedClipId;
    private KeyEventTrigger _mappingTrigger;
    private bool _suppressMappingChanged;
    private bool _isCapturingInput;

    public KeyMappingRowViewModel(
        IEnumerable<KeyMappingInputOption> inputOptions,
        bool isMouseMapping,
        int inputCode,
        KeyEventTrigger trigger = KeyEventTrigger.Down,
        string? selectedClipId = null)
    {
        IsMouseMapping = isMouseMapping;
        InputOptions = new ObservableCollection<KeyMappingInputOption>(inputOptions);
        Options = new ObservableCollection<KeyMappingOption>();

        _inputCode = ResolveInputCode(inputCode);
        _mappingTrigger = trigger;
        _selectedClipId = selectedClipId;
    }

    public bool IsMouseMapping { get; }

    public ObservableCollection<KeyMappingInputOption> InputOptions { get; }

    public int InputCode
    {
        get => _inputCode;
        set
        {
            var resolved = ResolveInputCode(value);

            if (!SetProperty(ref _inputCode, resolved))
            {
                return;
            }

            OnPropertyChanged(nameof(InputLabel));
            RaiseMappingChanged();
        }
    }

    public string InputLabel
    {
        get
        {
            if (IsCapturingInput)
            {
                return "Press any key...";
            }

            var option = InputOptions.FirstOrDefault(item => item.Code == InputCode);
            return option?.DisplayName ?? $"Code 0x{InputCode:X2}";
        }
    }

    public bool IsCapturingInput
    {
        get => _isCapturingInput;
        set
        {
            if (!SetProperty(ref _isCapturingInput, value))
            {
                return;
            }

            OnPropertyChanged(nameof(InputLabel));
        }
    }

    public KeyEventTrigger MappingTrigger
    {
        get => _mappingTrigger;
        set
        {
            if (!SetProperty(ref _mappingTrigger, value))
            {
                return;
            }

            RaiseMappingChanged();
        }
    }

    public ObservableCollection<KeyMappingOption> Options { get; }

    public string? SelectedClipId
    {
        get => _selectedClipId;
        set
        {
            if (!SetProperty(ref _selectedClipId, value))
            {
                return;
            }

            NotifyClipPropertiesChanged();

            RaiseMappingChanged();
        }
    }

    public string SelectedClipDisplayName
    {
        get
        {
            if (SelectedClipId is null)
            {
                return "Default";
            }

            return Options.FirstOrDefault(option => string.Equals(option.Id, SelectedClipId, StringComparison.OrdinalIgnoreCase))?.DisplayName
                ?? SelectedClipId;
        }
    }

    public string AudioButtonLabel => $"Audio: {SelectedClipDisplayName}";

    public event EventHandler? MappingChanged;

    public void UpdateOptions(IEnumerable<KeyMappingOption> options)
    {
        _suppressMappingChanged = true;
        try
        {
            Options.Clear();

            foreach (var option in options)
            {
                Options.Add(option);
            }

            if (SelectedClipId is null)
            {
                return;
            }

            if (!Options.Any(option => string.Equals(option.Id, SelectedClipId, StringComparison.OrdinalIgnoreCase)))
            {
                SelectedClipId = null;
            }

            NotifyClipPropertiesChanged();
        }
        finally
        {
            _suppressMappingChanged = false;
        }
    }

    private int ResolveInputCode(int requestedCode)
    {
        var match = InputOptions.FirstOrDefault(option => option.Code == requestedCode);
        return match?.Code ?? InputOptions.FirstOrDefault()?.Code ?? requestedCode;
    }

    private void RaiseMappingChanged()
    {
        if (_suppressMappingChanged)
        {
            return;
        }

        MappingChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyClipPropertiesChanged()
    {
        OnPropertyChanged(nameof(SelectedClipDisplayName));
        OnPropertyChanged(nameof(AudioButtonLabel));
    }
}
