namespace CLCKTY.App.UI;

public sealed record KeyMappingOption(string? Id, string DisplayName, string SourceLabel = "", bool CanRemove = false)
{
	public string DisplayLabel => string.IsNullOrWhiteSpace(SourceLabel)
		? DisplayName
		: $"{DisplayName} ({SourceLabel})";

	public override string ToString() => DisplayLabel;
}
