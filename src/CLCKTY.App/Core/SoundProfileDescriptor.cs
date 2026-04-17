namespace CLCKTY.App.Core;

public sealed record SoundClipDescriptor(string Id, string DisplayName);

public sealed record SoundProfileDescriptor(string Id, string DisplayName, IReadOnlyList<SoundClipDescriptor> Clips);
