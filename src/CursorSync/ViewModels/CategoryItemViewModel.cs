using CommunityToolkit.Mvvm.ComponentModel;
using CursorSync.Models;
using CursorSync.Services;

namespace CursorSync.ViewModels;

public partial class CategoryItemViewModel : ObservableObject
{
    private readonly Action _onChanged;

    public CategoryItemViewModel(CategoryDefinition definition, bool isEnabled, Action onChanged)
    {
        Definition = definition;
        _isEnabled = isEnabled;
        _onChanged = onChanged;
    }

    public CategoryDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Title => Definition.Title;
    public string Description => Definition.Description;
    public string Glyph => Definition.Glyph;
    public CategoryGroup Group => Definition.Group;
    public bool RequiresCursorClosed => Definition.RequiresCursorClosed;
    public bool IsLarge => Definition.IsLarge;
    public bool IsRecommended => Definition.DefaultEnabled;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _sizeText = "…";
    [ObservableProperty] private string _detailText = "Not scanned yet";
    [ObservableProperty] private bool _isPresent;
    [ObservableProperty] private long _bytes;
    [ObservableProperty] private bool _wasMeasured;

    partial void OnIsEnabledChanged(bool value) => _onChanged();

    public void ApplyScan(CategoryScan scan)
    {
        IsPresent = scan.Present;
        Bytes = scan.Bytes;
        WasMeasured = scan.Deep;
        if (!scan.Deep)
        {
            SizeText = scan.Present ? "Large" : "Empty";
            DetailText = scan.Present
                ? "Present — measured when enabled or before sync"
                : "Nothing on this machine yet";
            return;
        }

        SizeText = scan.Present ? FileSizeFormatter.FromBytes(scan.Bytes) : "Empty";
        DetailText = scan.Present
            ? $"{scan.Files} file{(scan.Files == 1 ? "" : "s")} · {FileSizeFormatter.FromBytes(scan.Bytes)}"
            : "Nothing on this machine yet";
    }
}

public sealed class CategoryGroupViewModel
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required IReadOnlyList<CategoryItemViewModel> Items { get; init; }
}
