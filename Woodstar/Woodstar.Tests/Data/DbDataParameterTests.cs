using System;
using System.Data;
using Xunit;

namespace Woodstar.Data.Tests;

public class DbDataParameterTests
{
    [Fact]
    public void ParameterNameDefaultEmptyString()
    {
        var p = new TestParameter();
        Assert.Equal(p.ParameterName, "");
    }

    [Fact]
    public void DirectionDefaultInput()
    {
        var p = new TestParameter();
        Assert.Equal(p.Direction, ParameterDirection.Input);
    }

    [Fact]
    public void FrozenNameThrows()
    {
        var p = new TestParameter() { ParameterName = "Name" };
        p.NotifyCollectionAdd();
        Assert.Throws<InvalidOperationException>(() => p.ParameterName = "NewName");
    }

    [Fact]
    public void IncrementInUse()
    {
        var p = new TestParameter();
        p.Increment();
        Assert.True(p.InUse);
    }

    [Fact]
    public void IncrementTo24bits()
    {
        var p = new TestParameter();
        p.Increment((int)Math.Pow(2, 24) - 1);
    }

    [Fact]
    public void DecrementInUse()
    {
        var p = new TestParameter();
        p.Increment();
        p.Decrement();
        Assert.True(!p.InUse);
    }

    [Fact]
    public void IncrementPast24bitsThrows()
    {
        var p = new TestParameter();
        Assert.Throws<InvalidOperationException>(() => p.Increment(int.MaxValue));
    }

    [Fact]
    public void DecrementPastZeroThrows()
    {
        var p = new TestParameter();
        Assert.Throws<InvalidOperationException>(() => p.Decrement());
    }

    [Fact]
    public void DecrementFrom24bitsPastZeroThrows()
    {
        var p = new TestParameter();
        p.Increment((int)Math.Pow(2, 24) - 1);
        Assert.Throws<InvalidOperationException>(() => p.Decrement(int.MaxValue));
    }

    [Fact]
    public void MixedUseOfCombinedFieldWorks()
    {
        var p = new TestParameter();
        p.Direction = ParameterDirection.InputOutput;
        p.IsNullable = true;
        p.Increment((int)Math.Pow(2, 24) - 1);
        p.NotifyCollectionAdd();
        Assert.True(p.InUse);
        Assert.Equal(p.Direction, ParameterDirection.InputOutput);
        Assert.True(p.IsNullable);

        p.Decrement((int)Math.Pow(2, 24) - 1);
        p.Direction = ParameterDirection.Output;
        p.IsNullable = false;
        Assert.Equal(p.Direction, ParameterDirection.Output);
        Assert.False(p.IsNullable);
        Assert.False(p.InUse);
        Assert.Throws<InvalidOperationException>(() => p.ParameterName = "NewName");
    }

    [Fact]
    public void MutationWhileInUseThrows()
    {
        var p = new TestParameter();
        p.Direction = ParameterDirection.InputOutput;
        p.Increment();
        Assert.Throws<InvalidOperationException>(() => p.Value = "");
        Assert.Throws<InvalidOperationException>(() => p.Direction = ParameterDirection.Input);
        Assert.Throws<InvalidOperationException>(() => p.ParameterName = "");
        Assert.Throws<InvalidOperationException>(() => p.DbType = DbType.Binary);
        Assert.Throws<InvalidOperationException>(() => p.Size = 1);
        Assert.Throws<InvalidOperationException>(() => p.Scale = 1);
        Assert.Throws<InvalidOperationException>(() => p.Precision = 1);
        Assert.Throws<InvalidOperationException>(() => p.IsNullable = true);
        Assert.Throws<InvalidOperationException>(() => p.SourceColumn = "");
        Assert.Throws<InvalidOperationException>(() => p.SourceColumnNullMapping = true);
        Assert.Throws<InvalidOperationException>(() => p.SourceVersion = DataRowVersion.Current);
    }

    [Fact]
    public void MutationAfterInUseWorks()
    {
        var p = new TestParameter();
        p.Direction = ParameterDirection.InputOutput;
        p.Increment();
        p.Decrement();
        p.Value = "";
        p.Direction = ParameterDirection.Input;
        p.ParameterName = "";
        p.DbType = DbType.Binary;
        p.Size = 1;
        p.Scale = 1;
        p.Precision = 1;
        p.IsNullable = true;
        p.SourceColumn = "";
        p.SourceColumnNullMapping = true;
        p.SourceVersion = DataRowVersion.Current;
    }

    [Fact]
    public void CloneAllowsNameChange()
    {
        var p = new TestParameter();
        p.NotifyCollectionAdd();
        p = p.Clone();
        p.ParameterName = "NewName";
    }

    [Fact]
    public void CloneHasFacets()
    {
        var p = new TestParameter();
        p.Precision = 10;
        p.NotifyCollectionAdd();
        var clone = p.Clone();
        Assert.Equal(p.Precision, clone.Precision);
    }

    [Fact]
    public void ValueTypeChangeResetsFacets()
    {
        var p = new TestParameter();
        p.Precision = 10;
        p.Value = 100;
        p.Value = "";
        Assert.Equal(p.Precision, 0);
    }

    [Fact]
    public void InvalidDirectionThrows()
    {
        var p = new TestParameter();
        Assert.Throws<ArgumentOutOfRangeException>(() => p.Direction = (ParameterDirection)int.MaxValue);
    }

    [Fact]
    public void InvalidDbTypeThrows()
    {
        var p = new TestParameter();
        Assert.Throws<ArgumentOutOfRangeException>(() => p.DbType = (DbType)int.MaxValue);
    }
}
