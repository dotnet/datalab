using System;
using System.Data;

namespace Woodstar.Data;

/// Interface for the purpose of augmenting and validating (potentially database specific) facet information.
interface IFacetsTransformer
{
    Facets Transform(DbType dbType, Facets suppliedFacets);
    Facets Transform<T>(T value, Facets suppliedFacets);
    Facets Transform(object value, Type type, Facets suppliedFacets);
}

class IdentityFacetsTransformer : IFacetsTransformer
{
    public Facets Transform(DbType dbType, Facets suppliedFacets) => suppliedFacets;
    public Facets Transform<T>(T value, Facets suppliedFacets) => suppliedFacets;
    public Facets Transform(object value, Type type, Facets suppliedFacets) => suppliedFacets;
}

//     if (precision.HasValue)
//     {
//         // Be lax here, if we don't know the type that's ok.
//         if (facets.Precision.HasValue && precision.Value > facets.Precision.Value)
//         {
//             throw new ArgumentOutOfRangeException("Precision of this type of value cannot be larger than: " + facets.Precision.Value);
//         }
//     }
//
//     if (scale.HasValue)
//     {
//         // Be lax here, if we don't know the type that's ok.
//         if (facets.MaxScale.HasValue && scale.Value > facets.MaxScale.Value)
//         {
//             throw new ArgumentOutOfRangeException("Scale of this type of value cannot be larger than: " + facets.MaxScale.Value);
//         }
//     }
