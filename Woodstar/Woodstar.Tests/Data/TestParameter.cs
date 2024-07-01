using System.Data;

namespace Woodstar.Data.Tests;

class TestParameter : DbDataParameter
{
    protected override DbType? DbTypeCore { get; set; }
    protected override DbDataParameter CloneCore() => new TestParameter();
    protected override object? ValueCore { get; set; }
    public new void NotifyCollectionAdd() => base.NotifyCollectionAdd();
    public void Increment(int count = 1) => IncrementInUse(count);
    public void Decrement(int count = 1) => DecrementInUse(count);
    public bool InUse => IsInUse;
    public new TestParameter Clone() => (TestParameter)base.Clone();
}
