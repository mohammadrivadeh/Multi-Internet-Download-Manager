using System.Collections.ObjectModel;
using System.Collections.Specialized;
using DownloadManager.App.Presentation;
namespace DownloadManager.Tests;
public class StableCollectionTests
{
    private sealed class Row(int id, string text) { public int Id { get; } = id; public string Text { get; set; } = text; }
    private static void Apply(ObservableCollection<Row> rows, (int Id, string Text)[] snapshot) => StableCollection.Reconcile(rows,snapshot,r=>r.Id,s=>s.Id,s=>new Row(s.Id,s.Text),(r,s)=>r.Text=s.Text);
    [Fact]
    public void RefreshPreservesIdentityAndDoesNotResetOrReplaceRows()
    {
        var first=new Row(1,"old"); var rows=new ObservableCollection<Row> {first,new(2,"second")};
        var changes=new List<NotifyCollectionChangedAction>(); rows.CollectionChanged+=(_,e)=>changes.Add(e.Action);
        Apply(rows,[(1,"updated"),(2,"second")]);
        Assert.Same(first,rows[0]); Assert.Equal("updated",first.Text); Assert.Empty(changes);
    }
    [Fact]
    public void ReorderAddAndRemoveKeepSurvivorsAndNeverReset()
    {
        var survivor=new Row(2,"two"); var rows=new ObservableCollection<Row> {new(1,"one"),survivor,new(3,"three")};
        var changes=new List<NotifyCollectionChangedAction>(); rows.CollectionChanged+=(_,e)=>changes.Add(e.Action);
        Apply(rows,[(3,"three"),(4,"four"),(2,"changed")]);
        Assert.Equal(new[]{3,4,2},rows.Select(r=>r.Id)); Assert.Same(survivor,rows[2]);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset,changes);
    }
    [Fact]
    public void DuplicateSnapshotsFailBeforeChangingTheVisibleCollection()
    {
        var rows=new ObservableCollection<Row> {new(1,"one")};
        Assert.Throws<ArgumentException>(()=>Apply(rows,[(2,"a"),(2,"b")])); Assert.Equal(1,Assert.Single(rows).Id);
    }
}
