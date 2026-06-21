namespace BeMusicSeeker.ViewModels;

public sealed class PlayHistorySummaryCard
{
    internal PlayHistorySummaryCard(string label, string value, bool compact = false, string filterKey = null, string filterText = null, bool isSelected = false)
    {
        Label = label ?? string.Empty;
        Value = value ?? string.Empty;
        Compact = compact;
        FilterKey = filterKey ?? string.Empty;
        FilterText = filterText ?? string.Empty;
        IsSelected = isSelected;
    }

    public string Label { get; }

    public string Value { get; }

    public bool Compact { get; }

    public string FilterKey { get; }

    public string FilterText { get; }

    public bool IsFilterable => !string.IsNullOrWhiteSpace(FilterKey) && !string.IsNullOrWhiteSpace(FilterText);

    public bool IsSelected { get; }
}
