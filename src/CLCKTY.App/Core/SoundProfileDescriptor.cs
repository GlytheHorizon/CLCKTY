namespace CLCKTY.App.Core;

public sealed record SoundClipDescriptor(string Id, string DisplayName, string SourceLabel, bool CanRemove);

public sealed record SoundProfileDescriptor(string Id, string DisplayName, IReadOnlyList<SoundClipDescriptor> Clips, bool IsImported)
{
	public string SourceLabel => IsImported ? "Imported" : "Stock";
}
