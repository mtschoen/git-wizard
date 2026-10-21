using System.Collections.ObjectModel;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

// Replaces the private backing list of an ObservableCollection (System.Collections.ObjectModel.Collection's
// "items" field) with a forwarding list that counts indexer reads, so a test can observe how many
// existing rows a search touched without any production instrumentation.
sealed class CountingList(IList<RepositoryNodeViewModel> inner) : IList<RepositoryNodeViewModel>
{
    public int IndexerReads { get; private set; }

    public static CountingList Install(ObservableCollection<RepositoryNodeViewModel> collection)
    {
        var field = typeof(Collection<RepositoryNodeViewModel>)
            .GetField("items", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, "Collection<T> no longer exposes its backing list field.");
        var counting = new CountingList((IList<RepositoryNodeViewModel>)field!.GetValue(collection)!);
        field.SetValue(collection, counting);
        return counting;
    }

    public RepositoryNodeViewModel this[int index]
    {
        get
        {
            IndexerReads++;
            return inner[index];
        }
        set => inner[index] = value;
    }

    public int Count => inner.Count;
    public bool IsReadOnly => inner.IsReadOnly;
    public void Add(RepositoryNodeViewModel item) => inner.Add(item);
    public void Clear() => inner.Clear();
    public bool Contains(RepositoryNodeViewModel item) => inner.Contains(item);
    public void CopyTo(RepositoryNodeViewModel[] array, int arrayIndex) => inner.CopyTo(array, arrayIndex);
    public IEnumerator<RepositoryNodeViewModel> GetEnumerator() => inner.GetEnumerator();
    public int IndexOf(RepositoryNodeViewModel item) => inner.IndexOf(item);
    public void Insert(int index, RepositoryNodeViewModel item) => inner.Insert(index, item);
    public bool Remove(RepositoryNodeViewModel item) => inner.Remove(item);
    public void RemoveAt(int index) => inner.RemoveAt(index);
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
