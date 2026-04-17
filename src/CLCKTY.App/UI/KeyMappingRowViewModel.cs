using System.Collections.ObjectModel;

namespace CLCKTY.App.UI;

public sealed class KeyMappingRowViewModel : ViewModelBase
{
    private string? _selectedClipId;

    public KeyMappingRowViewModel(string keyLabel, int virtualKey)
    {
        KeyLabel = keyLabel;
        VirtualKey = virtualKey;
        Options = new ObservableCollection<KeyMappingOption>();
    }

    public string KeyLabel { get; }

    public int VirtualKey { get; }

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

            MappingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? MappingChanged;

    public void UpdateOptions(IEnumerable<KeyMappingOption> options)
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
    }
}
