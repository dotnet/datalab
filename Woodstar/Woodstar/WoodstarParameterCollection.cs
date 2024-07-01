using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using Woodstar.Data;

namespace Woodstar;

/// <summary>
/// Represents a collection of parameters relevant to an <see cref="WoodstarCommand"/> as well as their respective mappings to columns in
/// a <see cref="DataSet"/>.
/// </summary>
public sealed class WoodstarParameterCollection: DbDataParameterCollection<WoodstarDbParameter>, IList<WoodstarDbParameter>
{
    public WoodstarParameterCollection()
    {}
    public WoodstarParameterCollection(int initialCapacity)
        :base(initialCapacity)
    {}

    internal new Enumerator GetValueEnumerator() => base.GetValueEnumerator();

    public void Add<T>(T? value) => AddCore(PositionalName, value);

    public void Add<T>(DbType dbType, T? value)
    {
        var parameter = CreateParameter(PositionalName, value);
        parameter.DbType = dbType;
        AddCore(null, parameter);
    }

    public void Add<T>(WoodstarDbType dbType, T? value)
    {
        var parameter = CreateParameter(PositionalName, value);
        parameter.WoodstarDbType = dbType;
        AddCore(null, parameter);
    }

    public void Add<T>(string parameterName, WoodstarDbType dbType, T? value)
    {
        if (parameterName is null)
            throw new ArgumentNullException(nameof(parameterName));

        var parameter = CreateParameter(parameterName, value);
        parameter.WoodstarDbType = dbType;
        AddCore(parameterName, parameter);
    }

    bool TryGetValueCore(string parameterName, [NotNullWhen(true)]out WoodstarDbParameter? parameter)
    {
        var index = IndexOfCore(parameterName);

        if (index == -1)
        {
            parameter = null;
            return false;
        }

        parameter = GetOrAddParameterInstance(index);
        return true;
    }

    IEnumerator<WoodstarDbParameter> IEnumerable<WoodstarDbParameter>.GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return GetOrAddParameterInstance(i);
    }

    /// <summary>
    /// Gets the <see cref="WoodstarParameter"/> with the specified name.
    /// </summary>
    /// <param name="parameterName">The name of the <see cref="WoodstarParameter"/> to retrieve.</param>
    /// <value>
    /// The <see cref="WoodstarParameter"/> with the specified name, or a <see langword="null"/> reference if the parameter is not found.
    /// </value>
    public new WoodstarDbParameter this[string parameterName]
    {
        get
        {
            if (parameterName is null)
                throw new ArgumentNullException(nameof(parameterName));

            if (!TryGetValueCore(parameterName, out WoodstarDbParameter? parameter))
                throw new ArgumentException("Parameter was not found.");

            return parameter;
        }
        set
        {
            if (parameterName is null)
                throw new ArgumentNullException(nameof(parameterName));

            if (value is null)
                throw new ArgumentNullException(nameof(value));

            var index = IndexOfCore(parameterName);
            if (index == -1)
                AddCore(parameterName, value);
            else
                ReplaceCore(index, parameterName, value);
        }
    }

    /// <summary>
    /// Gets the <see cref="WoodstarParameter"/> at the specified index.
    /// </summary>
    /// <param name="index">The zero-based index of the <see cref="WoodstarParameter"/> to retrieve.</param>
    /// <value>The <see cref="WoodstarParameter"/> at the specified index.</value>
    public new WoodstarDbParameter this[int index]
    {
        get
        {
            if ((uint)index >= Count)
                throw new ArgumentOutOfRangeException(nameof(index), "Index cannot be negative or larger than or equal to Count.");

            return GetOrAddParameterInstance(index);
        }
        set
        {
            if ((uint)index >= Count)
                throw new ArgumentOutOfRangeException(nameof(index), "Index cannot be negative or larger than or equal to Count.");

            if (value is null)
                throw new ArgumentNullException(nameof(value));

            ReplaceCore(index, value.ParameterName, value);
        }
    }

    /// <summary>
    /// Gets a value indicating whether a <see cref="WoodstarParameter"/> with the specified name exists in the collection.
    /// </summary>
    /// <param name="parameterName">The name of the <see cref="WoodstarParameter"/> object to find.</param>
    /// <param name="parameter">
    /// A reference to the requested parameter is returned if it is found in the list.
    /// This value is <see langword="null"/> if the parameter is not found.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the collection contains the parameter and param will contain the parameter;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    public bool TryGetValue(string parameterName, [NotNullWhen(true)] out WoodstarDbParameter? parameter)
    {
        if (parameterName is null)
            throw new ArgumentNullException(nameof(parameterName));

        return TryGetValueCore(parameterName, out parameter);
    }

    /// <inheritdoc />
    void ICollection<WoodstarDbParameter>.Add(WoodstarDbParameter item) => AddCore(null, item ?? throw new ArgumentNullException(nameof(item)));

    /// <summary>
    /// Insert the specified parameter into the collection.
    /// </summary>
    /// <param name="index">Index of the existing parameter before which to insert the new one.</param>
    /// <param name="value">Parameter to insert.</param>
    public void Insert(int index, WoodstarDbParameter value)
    {
        if ((uint)index > Count)
            throw new ArgumentOutOfRangeException(nameof(index), "Index cannot be negative or larger than Count.");

        if (value is null)
            throw new ArgumentNullException(nameof(value));

        InsertCore(index, value.ParameterName, value);
    }

    /// <summary>
    /// Remove the specified parameter from the collection.
    /// </summary>
    /// <param name="value">Parameter to remove.</param>
    /// <returns>True if the parameter was found and removed, otherwise false.</returns>
    public bool Remove(WoodstarDbParameter value)
    {
        var index = IndexOfCore(value ?? throw new ArgumentNullException(nameof(value)));
        if (index == -1)
            return false;

        RemoveAtCore(index);
        return true;
    }

    /// <summary>
    /// Report the offset within the collection of the given parameter.
    /// </summary>
    /// <param name="value">Parameter to find.</param>
    /// <returns>Index of the parameter, or -1 if the parameter is not present.</returns>
    public int IndexOf(WoodstarDbParameter value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));

        return IndexOfCore(value);
    }

    /// <summary>
    /// Report whether the specified parameter is present in the collection.
    /// </summary>
    /// <param name="value">Parameter to find.</param>
    /// <returns>True if the parameter was found, otherwise false.</returns>
    public bool Contains(WoodstarDbParameter value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));

        return IndexOfCore(value) != -1;
    }

    /// <summary>
    /// Convert collection to a System.Array.
    /// </summary>
    /// <param name="array">Destination array.</param>
    /// <param name="arrayIndex">Starting index in destination array.</param>
    public void CopyTo(WoodstarDbParameter[] array, int arrayIndex) => CopyTo((Array)array, arrayIndex);

    /// <inheritdoc />
    bool ICollection<WoodstarDbParameter>.IsReadOnly => false;

    protected override bool CanParameterBePositional => true;
    protected override WoodstarDbParameter CreateParameter(string parameterName, object? value) => new WoodstarParameter(parameterName, value);
    protected override WoodstarDbParameter CreateParameter<T>(string parameterName, T? value) where T : default
        => new WoodstarParameter<T>(parameterName, value);
}
