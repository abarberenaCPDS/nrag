using DotnetRag.Blazor.Models;

namespace DotnetRag.Blazor.State;

public sealed class CollectionsState
{
    public List<CollectionInfo> AllCollections { get; set; } = [];
    public HashSet<string> SelectedCollectionNames { get; } = [];
    public CollectionInfo? DrawerCollection { get; private set; }
    public bool IsDrawerOpen { get; private set; }
    public string SearchQuery { get; set; } = "";
    public List<FilterCondition> ActiveFilters { get; set; } = [];

    public event Action? OnChange;

    public IEnumerable<CollectionInfo> Filtered =>
        string.IsNullOrWhiteSpace(SearchQuery)
            ? AllCollections
            : AllCollections.Where(c => c.CollectionName.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase));

    public void ToggleSelection(string collectionName)
    {
        if (!SelectedCollectionNames.Remove(collectionName))
            SelectedCollectionNames.Add(collectionName);
        OnChange?.Invoke();
    }

    public void SetSingleSelection(string collectionName)
    {
        SelectedCollectionNames.Clear();
        SelectedCollectionNames.Add(collectionName);
        OnChange?.Invoke();
    }

    public void ClearSelection()
    {
        SelectedCollectionNames.Clear();
        OnChange?.Invoke();
    }

    public void OpenDrawer(CollectionInfo collection)
    {
        DrawerCollection = collection;
        IsDrawerOpen = true;
        OnChange?.Invoke();
    }

    public void CloseDrawer()
    {
        IsDrawerOpen = false;
        DrawerCollection = null;
        OnChange?.Invoke();
    }

    public void SetCollections(List<CollectionInfo> collections)
    {
        AllCollections = collections;
        OnChange?.Invoke();
    }

    public void NotifyChange() => OnChange?.Invoke();

    public string BuildFilterExpression()
    {
        if (ActiveFilters.Count == 0) return "";
        var parts = new List<string>();
        for (int i = 0; i < ActiveFilters.Count; i++)
        {
            var f = ActiveFilters[i];
            var clause = f.Operator switch
            {
                "in" => $"content_metadata[\"{f.Field}\"] in [\"{f.Value}\"]",
                "like" => $"content_metadata[\"{f.Field}\"] like \"%{f.Value}%\"",
                _ => $"content_metadata[\"{f.Field}\"] {f.Operator} \"{f.Value}\""
            };
            if (i > 0)
                parts.Add(f.LogicalOp);
            parts.Add(clause);
        }
        return string.Join(" ", parts);
    }
}
