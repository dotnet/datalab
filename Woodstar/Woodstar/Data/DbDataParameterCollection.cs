using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Woodstar.Data;

public abstract partial class DbDataParameterCollection<TParameter> where TParameter : DbDataParameter
{
    // Internal for tests
    internal const int LookupThreshold = 5;
    protected internal const string PositionalName = "";

    readonly struct ParameterItem
    {
        readonly string _name;
        readonly object? _value;

        ParameterItem(string name, object? value)
        {
            if (value is DbParameter)
            {
                if (value is not TParameter p)
                    throw new InvalidCastException(
                        $"The DbParameter \"{value}\" is not of type \"{typeof(TParameter).Name}\" and cannot be used in this parameter collection, it can be added as a value to an {typeof(TParameter).Name} if this was intended.");

                // Prevent any changes from now on as the name may end up being used in the lookup.
                // We don't want the lookup to get out of sync but we also don't want any backreferences from parameter to collection so we freeze the name instead.
                p.NotifyCollectionAdd();

                if (!name.AsSpan().Equals(CreateNameSpan(p.ParameterName), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"Parameter name must be a case-insensitive match with the property '{nameof(DbDataParameter.ParameterName)}' on the given {typeof(TParameter).Name}.", nameof(name));
            }

            _name = name;
            _value = value;
        }

        /// The canonical name used for uniqueness.
        public string Name => _name;

        /// Either null, an object or an WoodstarDbParameter, any other derived DbParameter types are not accepted.
        public object? Value => _value;

        public bool TryGetAsParameter([NotNullWhen(true)]out TParameter? parameter)
        {
            if (Value is TParameter p)
            {
                parameter = p;
                return true;
            }

            parameter = default;
            return false;
        }

        public KeyValuePair<string, object?> AsKeyValuePair() => new(_name, _value);

        static int ComputePrefixLength(string name) => name.Length > 0 && name[0] is '@' or ':' ? 1 : 0;
        static string CreateName(string parameterName) => parameterName.Substring(ComputePrefixLength(parameterName));
        public static ReadOnlySpan<char> CreateNameSpan(string parameterName) => parameterName.AsSpan(ComputePrefixLength(parameterName));

        public static ParameterItem Create(string? parameterName, object? value)
        {
            if (parameterName is null)
            {
                // We allow all parameter types here and only fail in the constructor to give a nicer validation ordering.
                // This is a fallback for the T?/object? value accepting apis.
                if (value is not IDbDataParameter parameter)
                    throw new ArgumentNullException(nameof(parameterName));

                parameterName = parameter.ParameterName;
            }

            return new(CreateName(parameterName), value);
        }
    }

    readonly List<ParameterItem> _parameters;

    // Dictionary lookups for GetValue to improve performance.
    Dictionary<string, int>? _caseInsensitiveLookup;

    /// <summary>
    /// Initializes a new instance of the DbDataParameterCollection class.
    /// </summary>
    protected DbDataParameterCollection(int initialCapacity = 5)
    {
        _parameters = new(initialCapacity);
    }

    bool LookupEnabled => _parameters.Count >= LookupThreshold;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    string GetName(int index) => _parameters[index].Name;

    bool NameIsPositional(string name) => CanParameterBePositional && name is PositionalName;

    void LookupClear() => _caseInsensitiveLookup?.Clear();

    void LookupAdd(string name, int index)
    {
        if (NameIsPositional(name))
            return;

        _caseInsensitiveLookup?.TryAdd(name, index);
    }

    void LookupInsert(string name, int index)
    {
        if (_caseInsensitiveLookup is null)
            return;

        if (NameIsPositional(name) || !_caseInsensitiveLookup.TryGetValue(name, out var indexCi) || index < indexCi)
        {
            for (var i = index + 1; i < _parameters.Count; i++)
            {
                var parameterName = GetName(i);
                if (_caseInsensitiveLookup.TryGetValue(parameterName, out var currentI) && currentI + 1 == i)
                    _caseInsensitiveLookup[parameterName] = i;
            }

            if (!NameIsPositional(name))
                _caseInsensitiveLookup[name] = index;
        }
    }

    void LookupRemove(string name, int index)
    {
        if (NameIsPositional(name) || _caseInsensitiveLookup is null)
            return;

        if (_caseInsensitiveLookup.Remove(name))
        {
            for (var i = index; i < _parameters.Count; i++)
            {
                var parameterName = GetName(i);
                if (_caseInsensitiveLookup.TryGetValue(parameterName, out var currentI) && currentI - 1 == i)
                    _caseInsensitiveLookup[parameterName] = i;
            }

            // Fix-up the case-insensitive lookup to point to the next match, if any.
            for (var i = 0; i < _parameters.Count; i++)
            {
                var parameterName = GetName(i);
                if (parameterName.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    _caseInsensitiveLookup[name] = i;
                    break;
                }
            }
        }
    }

    void LookupChangeName(ParameterItem item, string oldName, int index)
    {
        if (oldName.Equals(item.Name, StringComparison.OrdinalIgnoreCase))
            return;

        if (oldName.Length != 0)
            LookupRemove(oldName, index);
        if (NameIsPositional(item.Name))
            LookupAdd(item.Name, index);
    }

    protected object? GetValue(int index) => _parameters[index].Value;

#if NETSTANDARD
    ParameterItem _refLocation;
#endif

    ref ParameterItem GetItemRef(int index)
    {
#if NETSTANDARD
        _refLocation = _parameters[index];
        return ref _refLocation;
#else
        return ref CollectionsMarshal.AsSpan(_parameters)[index];
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected TParameter GetOrAddParameterInstance(int index)
    {
        ref var p = ref GetItemRef(index);
        if (p.TryGetAsParameter(out var parameter))
            return parameter;

        return ReplaceValue(ref p);

        TParameter ReplaceValue(ref ParameterItem p)
        {
            var dbParam = CreateParameter(p.Name, p.Value);
            p = ParameterItem.Create(p.Name, dbParam);
            return dbParam;
        }
    }

    /// parameterName can only be null if object is an instance of TParameter.
    protected int AddCore(string? parameterName, object? value)
    {
        var item = ParameterItem.Create(parameterName, value);
        _parameters.Add(item);
        LookupAdd(item.Name, _parameters.Count - 1);
        return _parameters.Count;
    }

    protected void ReplaceCore(int index, string? parameterName, object? value)
    {
        ref var current = ref GetItemRef(index);
        var item = ParameterItem.Create(parameterName, value);
        LookupChangeName(item, current.Name, index);
        current = item;
    }

    protected void InsertCore(int index, string? parameterName, object? value)
    {
        var item = ParameterItem.Create(parameterName, value);
        _parameters.Insert(index, item);
        // Also called if the item is positional, the lookup needs to be shifted to account for the insert.
        LookupInsert(item.Name, index);
    }

    protected void RemoveAtCore(int index)
    {
        var item = _parameters[index];
        _parameters.RemoveAt(index);
        if (!LookupEnabled)
            LookupClear();
        else
            LookupRemove(item.Name, index);
    }

    protected int IndexOfCore(KeyValuePair<string, object?> item)
    {
        var index = IndexOfCore(item.Key);
        if (index == -1)
            return -1;

        var p = _parameters[index];
        if (item.Value == p.Value)
            return index;

        var name = ParameterItem.CreateNameSpan(item.Key);
        for (var i = index; i < _parameters.Count; i++)
        {
            p = _parameters[i];
            if (name.Equals(p.Name.AsSpan(), StringComparison.OrdinalIgnoreCase) && p.Value == item.Value)
                return i;
        }

        return -1;
    }

    protected int IndexOfCore(object? value)
    {
        for (var i = 0; i < _parameters.Count; i++)
        {
            var p = _parameters[i];
            if (value == p.Value)
                return i;
        }

        return -1;
    }

    protected int IndexOfCore(string parameterName)
    {
        var name = ParameterItem.CreateNameSpan(parameterName);

        // Using a dictionary is always faster after around 10 items when matched against reference equality.
        // For string equality this is the case after ~3 items so we take a decent compromise going with 5.
        if (LookupEnabled && name.Length != 0)
        {
            if (_caseInsensitiveLookup is null)
                BuildLookup();

            if (_caseInsensitiveLookup!.TryGetValue(name.ToString(), out var indexCi))
                return indexCi;

            return -1;
        }

        // Do case-insensitive search.
        for (var i = 0; i < _parameters.Count; i++)
        {
            var otherName = GetName(i);
            if (name.Equals(otherName.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;

        void BuildLookup()
        {
            _caseInsensitiveLookup = new(_parameters.Count, StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < _parameters.Count; i++)
            {
                var item = _parameters[i];
                if (!NameIsPositional(item.Name))
                    LookupAdd(item.Name, i);
            }
        }
    }

    protected bool TryGetValueCore(string parameterName, out object? value)
    {
        var index = IndexOfCore(parameterName);

        if (index == -1)
        {
            value = default;
            return false;
        }

        var p = _parameters[index];
        value = p.Value;
        return true;
    }

    // Beautiful antique ADO.NET design to fill the public GetEnumerator method slot with a non generic IEnumerable method...
    protected Enumerator GetValueEnumerator() => new(this);

    protected abstract bool CanParameterBePositional { get; }
    protected abstract TParameter CreateParameter(string parameterName, object? value);
    protected abstract TParameter CreateParameter<T>(string parameterName, T? value);
}

// Public surface & ADO.NET
/// <summary>
/// Represents a collection of parameters relevant to a <see cref="DbCommand"/> as well as their respective mappings to columns in
/// a <see cref="DataSet"/>.
/// </summary>
public abstract partial class DbDataParameterCollection<TParameter>: DbParameterCollection, ICollection<KeyValuePair<string, object?>>, IDataParameterCollection
{
    /// <summary>
    /// Adds a parameter value with the given name.
    /// </summary>
    /// <param name="parameterName">The name of the parameter.</param>
    /// <param name="value">The value for the parameter.</param>
    /// <typeparam name="T">The type of value.</typeparam>
    public void Add<T>(string parameterName, T? value)
    {
        if (parameterName is null)
            throw new ArgumentNullException(nameof(parameterName));

        AddCore(parameterName, value);
    }

    /// <summary>
    /// Adds a parameter value with the given name and DbType.
    /// </summary>
    /// <param name="parameterName">The name of the parameter.</param>
    /// <param name="dbType">The DbType for the parameter.</param>
    /// <param name="value">The value for the parameter.</param>
    /// <typeparam name="T">The type of value.</typeparam>
    public void Add<T>(string parameterName, DbType dbType, T? value)
    {
        if (parameterName is null)
            throw new ArgumentNullException(nameof(parameterName));

        var parameter = CreateParameter(parameterName, value);
        parameter.DbType = dbType;
        AddCore(parameterName, parameter);
    }

    /// <summary>
    /// Gets a value indicating whether a parameter with the specified name exists in the collection.
    /// </summary>
    /// <param name="parameterName">The name of the parameter to find.</param>
    /// <param name="value">
    /// A reference to the requested value, which can be  <see langword="null"/>, is returned if it is found in the list.
    /// This value is always <see langword="null"/> if the parameter is not found.
    /// </param>
    /// <returns>
    /// <see langword="true"/> whether the collection contains the parameter;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    public bool TryGetValue(string parameterName, out object? value)
    {
        if (parameterName is null)
            throw new ArgumentNullException(nameof(parameterName));

        return TryGetValueCore(parameterName, out value);
    }

    /// <summary>
    /// Removes the parameter specified by the parameterName from the collection.
    /// </summary>
    /// <param name="parameterName">The name of the parameter to remove from the collection.</param>
    public void Remove(string parameterName)
    {
        if (parameterName is null)
            throw new ArgumentNullException(nameof(parameterName));

        var index = IndexOfCore(parameterName);
        if (index == -1)
            throw new ArgumentException("A parameter with the given name was not found.", nameof(parameterName));

        RemoveAtCore(index);
    }

    public struct Enumerator : IEnumerator<KeyValuePair<string, object?>>
    {
        readonly List<ParameterItem> _parameters;
        int _index;
        KeyValuePair<string, object?> _current;

        Enumerator(List<ParameterItem> parameters)
        {
            _parameters = parameters;
        }

        public Enumerator(DbDataParameterCollection<TParameter> parameters)
        {
            _parameters = parameters._parameters;
        }

        public bool MoveNext()
        {
            var parameters = _parameters;

            if ((uint)_index < (uint)parameters.Count)
            {
                _current = parameters[_index].AsKeyValuePair();
                _index++;
                return true;
            }

            _current = default;
            _index = parameters.Count + 1;
            return false;
        }

        public KeyValuePair<string, object?> Current => _current;

        public void Reset()
        {
            _index = 0;
            _current = default;
        }

        public Enumerator GetEnumerator() => new(_parameters);

        object IEnumerator.Current => Current;
        public void Dispose() { }
    }

    /// <inheritdoc />
    IEnumerator<KeyValuePair<string, object?>> IEnumerable<KeyValuePair<string, object?>>.GetEnumerator() => GetValueEnumerator();

    /// <inheritdoc />
    public void Add(KeyValuePair<string, object?> item)
    {
        if (item.Key is null)
            throw new ArgumentNullException(nameof(item), "Key is null.");

        AddCore(item.Key, item.Value);
    }

    /// <inheritdoc />
    public bool Remove(KeyValuePair<string, object?> item)
    {
        if (item.Key is null)
            throw new ArgumentNullException(nameof(item), "Key is null.");

        var index = IndexOfCore(item);
        if (index == -1)
            return false;

        RemoveAtCore(index);
        return true;
    }

    /// <inheritdoc />
    public bool Contains(KeyValuePair<string, object?> item)
    {
        if (item.Key is null)
            throw new ArgumentNullException(nameof(item), "Key is null.");

        return IndexOfCore(item) != -1;
    }

    /// <summary>
    /// Returns the names and values as they are stored, this means objects don't have to be non-null or of type DbParameter.
    /// </summary>
    /// <param name="array"></param>
    /// <param name="arrayIndex"></param>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public void CopyTo(KeyValuePair<string, object?>[] array, int arrayIndex)
    {
        if (array is null)
            throw new ArgumentNullException(nameof(array));

        if ((uint)arrayIndex > array.Length)
            throw new ArgumentOutOfRangeException(nameof(arrayIndex), "Index cannot be negative or larger than the length of the array.");

        if (arrayIndex >= 0 && arrayIndex + _parameters.Count <= array.Length)
            throw new ArgumentOutOfRangeException(nameof(array), "Array too small.");

        for (var i = 0; i < _parameters.Count; i++)
        {
            var p = _parameters[i];
            array[arrayIndex + i] = new KeyValuePair<string, object?>(p.Name, p.Value);
        }
    }

    // Reimplemented IDataParameterCollection methods that otherwise do a cast in the setter to DbParameter on the object value.
    /// <inheritdoc cref="IList.this[int]" />
    object? IList.this[int index]
    {
        get
        {
            if ((uint)index >= _parameters.Count)
                throw new ArgumentOutOfRangeException(nameof(index), "Index cannot be negative or larger than or equal to Count.");

            return GetValue(index);
        }
        set
        {
            if ((uint)index >= _parameters.Count)
                throw new ArgumentOutOfRangeException(nameof(index), "Index cannot be negative or larger than or equal to Count.");

            ReplaceCore(index, null, value);
        }
    }

    /// <inheritdoc cref="IDataParameterCollection.this[string]" />
    object IDataParameterCollection.this[string parameterName]
    {
        get
        {
            if (parameterName is null)
                throw new ArgumentNullException(nameof(parameterName));

            var index = IndexOfCore(parameterName);
            if (index == -1)
                throw new ArgumentException("A parameter with the given name was not found.", nameof(parameterName));

            return GetOrAddParameterInstance(index);
        }
        set
        {
            if (parameterName is null)
                throw new ArgumentNullException(nameof(parameterName));

            var index = IndexOfCore(parameterName);
            if (index == -1)
                AddCore(parameterName, value);
            else
                ReplaceCore(index, parameterName, value);
        }
    }

    /// <inheritdoc cref="IList.Add" />
    int IList.Add(object? value) => AddCore(null, value);

    /// <inheritdoc cref="DbParameterCollection.GetEnumerator" />
    public override IEnumerator GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return GetOrAddParameterInstance(i);
    }

    /// <inheritdoc cref="DbParameterCollection.Add" />
    public override int Add(object value) => AddCore(null, value ?? throw new ArgumentNullException(nameof(value)));

    /// <inheritdoc cref="DbParameterCollection.AddRange" />
    public override void AddRange(Array values)
    {
        foreach (var parameter in values)
            AddCore(null, parameter);
    }

    /// <inheritdoc cref="DbParameterCollection.Insert" />
    public override void Insert(int index, object? value)
    {
        if ((uint)index > _parameters.Count)
            throw new ArgumentOutOfRangeException(nameof(index), "Index cannot be negative or larger than Count.");

        InsertCore(index, null, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <inheritdoc cref="DbParameterCollection.RemoveAt(string)" />
    public override void RemoveAt(string parameterName) => RemoveAtCore(IndexOfCore(parameterName ?? throw new ArgumentNullException(nameof(parameterName))));

    /// <inheritdoc cref="DbParameterCollection.RemoveAt(int)" />
    public override void RemoveAt(int index)
    {
        if ((uint)index >= _parameters.Count)
            throw new ArgumentOutOfRangeException(nameof(index), "Index cannot be negative or larger than or equal to Count.");

        RemoveAtCore(index);
    }

    /// <inheritdoc cref="DbParameterCollection.Remove" />
    public override void Remove(object? value) => RemoveAtCore(IndexOfCore(value ?? throw new ArgumentNullException(nameof(value))));

    /// <inheritdoc cref="DbParameterCollection.IndexOf(string)" />
    public override int IndexOf(string parameterName) => IndexOfCore(parameterName ?? throw new ArgumentNullException(nameof(parameterName)));

    /// <inheritdoc cref="DbParameterCollection.IndexOf(object)" />
    public override int IndexOf(object? value) => IndexOfCore(value ?? throw new ArgumentNullException(nameof(value)));

    /// <inheritdoc cref="DbParameterCollection.Contains(string)" />
    public override bool Contains(string parameterName) => IndexOfCore(parameterName ?? throw new ArgumentNullException(nameof(parameterName))) != -1;

    /// <inheritdoc cref="DbParameterCollection.Contains(object)" />
    public override bool Contains(object? value) => IndexOfCore(value ?? throw new ArgumentNullException(nameof(value))) != -1;

    /// <inheritdoc cref="DbParameterCollection.CopyTo" />
    public override void CopyTo(Array array, int arrayIndex)
    {
        if (array is null)
            throw new ArgumentNullException(nameof(array));

        if ((uint)arrayIndex > array.Length)
            throw new ArgumentOutOfRangeException(nameof(arrayIndex), "Index cannot be negative or larger than the length of the array.");

        if (arrayIndex >= 0 && arrayIndex + _parameters.Count <= array.Length)
            throw new ArgumentOutOfRangeException(nameof(array), "Array too small.");

        var list = array as IList;
        for (var i = 0; i < _parameters.Count; i++)
            list[arrayIndex + i] = GetOrAddParameterInstance(i);
    }

    /// <inheritdoc cref="DbParameterCollection.Count" />
    public override int Count => _parameters.Count;

    /// <inheritdoc cref="DbParameterCollection.IsReadOnly" />
    public override bool IsReadOnly => false;

    /// <inheritdoc cref="DbParameterCollection.IsFixedSize" />
    public override bool IsFixedSize => false;

    /// <inheritdoc cref="DbParameterCollection.IsSynchronized" />
    public override bool IsSynchronized => false;

    /// <inheritdoc cref="DbParameterCollection.SyncRoot" />
    public override object SyncRoot => _parameters;

    /// <inheritdoc cref="DbParameterCollection.Clear" />
    public override void Clear()
    {
        LookupClear();
        _parameters.Clear();
    }

    protected override DbParameter GetParameter(int index) => (DbParameter)((IList)this)[index]!;
    protected override DbParameter GetParameter(string parameterName) => (DbParameter)((IDataParameterCollection)this)[parameterName];
    protected override void SetParameter(int index, DbParameter value) => ((IList)this)[index] = value;
    protected override void SetParameter(string parameterName, DbParameter value) => ((IDataParameterCollection)this)[parameterName] = value;
}
