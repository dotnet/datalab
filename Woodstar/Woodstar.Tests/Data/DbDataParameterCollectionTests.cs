using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Woodstar.Data.Tests;

public class DbDataParameterCollectionTests
{
    const int LookupThreshold = PositionalTestCollection.LookupThreshold;
    const string PositionalName = PositionalTestCollection.PositionalName;

    class PositionalTestCollection : DbDataParameterCollection<TestParameter>
    {
        public PositionalTestCollection() {}

        public void Add<T>(T? value) => AddCore(PositionalName, value);

        protected override bool CanParameterBePositional => true;
        protected override TestParameter CreateParameter(string parameterName, object? value)
            => new() { ParameterName = parameterName, Value = value };

        protected override TestParameter CreateParameter<T>(string parameterName, T? value) where T : default
            => new() { ParameterName = parameterName, Value = value };
    }

    class UnknownDbParameter : DbParameter
    {
        public override void ResetDbType() => throw new NotImplementedException();
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
        [AllowNull] public override string ParameterName { get; set; } = "";
        [AllowNull] public override string SourceColumn { get; set; } = "";
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
    }

    [Fact]
    public void UnknownDbParameterThrows()
    {
        var collection = new PositionalTestCollection();
        Assert.Throws<InvalidCastException>(() => collection.Add(new UnknownDbParameter()));
    }

    [Fact]
    public void ClearSucceeds()
    {
        var collection = new PositionalTestCollection
        {
            { "test1", 1 },
            { "test2", "value" }
        };
        collection.Clear();
        Assert.False(collection.Contains("test1"));
        Assert.False(collection.Contains("test2"));
    }

    [Fact]
    public void IndexOfFindsPrefixedNames()
    {
        var collection = new PositionalTestCollection
        {
            { "@p0", 1 },
            { ":p1", 1 },
            { "p2", 1 }
        };

        for (var i = 0; i < collection.Count; i++)
        {
            Assert.Equal(i, collection.IndexOf("@p" + i));
            Assert.Equal(i, collection.IndexOf(":p" + i));
            Assert.Equal(i, collection.IndexOf("p" + i));
        }
    }

    [Theory]
    [InlineData(LookupThreshold)]
    [InlineData(LookupThreshold - 2)]
    public void CaseInsensitiveLookups(int count)
    {
        var collection = new PositionalTestCollection();
        for (var i = 0; i < count; i++)
            collection.Add($"p{i}", i);

        Assert.Equal(1, collection.IndexOf("P1"));
    }

    [Theory]
    [InlineData(LookupThreshold)]
    [InlineData(LookupThreshold - 2)]
    public void CaseSensitiveLookups(int count)
    {
        var collection = new PositionalTestCollection();
        for (var i = 0; i < count; i++)
            collection.Add($"p{i}", i);

        Assert.Equal(1, collection.IndexOf("p1"));
    }

    [Theory]
    [InlineData(LookupThreshold)]
    [InlineData(LookupThreshold - 2)]
    public void PositionalLookup(int count)
    {
        var collection = new PositionalTestCollection();
        for (var i = 0; i < count; i++)
            collection.Add(PositionalName, i);

        Assert.Equal(0, collection.IndexOf(""));
    }

    [Theory]
    [InlineData(LookupThreshold)]
    [InlineData(LookupThreshold - 2)]
    public void IndexerNameParameterNameMismatchThrows(int count)
    {
        var collection = new PositionalTestCollection();
        for (var i = 0; i < count; i++)
            collection.Add($"p{i}", i);

        collection["p1"] = new TestParameter { ParameterName = "p1", Value = 1};
        collection["p1"] = new TestParameter { ParameterName = "P1", Value = 1};

        Assert.Throws<ArgumentException>(() =>
        {
            collection["p1"] = new TestParameter { ParameterName = "p2", Value = 1};
        });
    }
}
