using System.Data;

namespace Woodstar.Data;

interface IDbDataParameter<T>: IDbDataParameter
{
    new T? Value { get; set; }
}
