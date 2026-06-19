namespace BeMusicSeeker.ViewModels;

public sealed class PlayHistorySummaryCard
{
    internal PlayHistorySummaryCard(string label, string value, bool compact = false)
    {
        Label = label ?? string.Empty;
        Value = value ?? string.Empty;
        Compact = compact;
    }

    public string Label { get; }

    public string Value { get; }

    public bool Compact { get; }
}
