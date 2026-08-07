using System.Reflection;
using RepoDb.Attributes;

namespace RepoDb.ClickHouse.Bulk;

/// <summary>
/// Maps the public readable properties of a .NET entity to ClickHouse columns, honoring RepoDb's
/// <c>[Map]</c> attribute and field-name cache.
/// </summary>
/// <remarks>
/// This mapper is used internally by <see cref="ClickHouseBulkInserter{TEntity}"/> to build the
/// per-row value sequence passed to <c>InsertBinaryAsync</c>. It does not honor a RepoDb
/// <c>[Ignore]</c>-style attribute for property exclusion: RepoDb 1.15.1 removed the attribute from
/// its core package, so the only supported ways to exclude a property from the mapping are a
/// write-only property (no getter) or passing its resolved column name in <c>ignoredColumns</c>.
/// </remarks>
internal sealed class EntityFieldMapper<TEntity> where TEntity : class
{
    /// <summary>
    /// Represents the binding between one .NET property and one ClickHouse column: the resolved
    /// column name, the .NET type to use when serializing the value, and a compiled getter delegate.
    /// </summary>
    /// <param name="ColumnName">The ClickHouse column name the property maps to.</param>
    /// <param name="ColumnType">
    /// The .NET type to use for serialization — the actual column type from <c>system.columns</c>
    /// when available, otherwise the property's own (unwrapped-nullable) type.
    /// </param>
    /// <param name="ValueGetter">Compiled delegate that reads the property's value off an entity instance.</param>
    public sealed record FieldBinding(
        string ColumnName,
        Type ColumnType,
        Func<TEntity, object?> ValueGetter);

    /// <summary>The resolved column bindings for <typeparamref name="TEntity"/>, in declaration order.</summary>
    public IReadOnlyList<FieldBinding> Bindings { get; }

    /// <summary>
    /// Builds the mapper by reflecting over <typeparamref name="TEntity"/>'s public instance properties
    /// and resolving each one to a ClickHouse column name and type.
    /// </summary>
    /// <param name="dbFields">
    /// Column metadata read from <c>system.columns</c> via <see cref="DbHelpers.ClickHouseDbHelper"/>.
    /// When supplied, it is used to resolve each column's real ClickHouse-derived .NET type (e.g.
    /// <c>UInt64</c> → <see langword="ulong"/> rather than the CLR property's own type) and to skip
    /// properties that have no matching column. When <see langword="null"/>, the mapper falls back to
    /// reflection-only resolution and includes every readable property.
    /// </param>
    /// <param name="ignoredColumns">
    /// Column names to exclude from the mapping (e.g. computed or <c>MATERIALIZED</c> columns that
    /// must never be written). Matched case-insensitively against the resolved column name.
    /// </param>
    public EntityFieldMapper(
        IEnumerable<DbField>? dbFields = null,
        IEnumerable<string>? ignoredColumns = null)
    {
        var ignored = new HashSet<string>(
            ignoredColumns ?? [],
            StringComparer.OrdinalIgnoreCase);

        // Prefer RepoDb's own field cache, which already accounts for ClassMapper/fluent mappings.
        var repoDbFields = FieldCache.Get<TEntity>();

        var bindings = new List<FieldBinding>();

        foreach (var prop in typeof(TEntity).GetProperties(
            BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead)
                continue;

            // Resolve the column name: RepoDb's field cache first, then [Map], then the property name as-is.
            var columnName = ResolveColumnName(prop, repoDbFields);

            if (ignored.Contains(columnName))
                continue;

            // Prefer the type reported by system.columns (the real ClickHouse column type) over the CLR property type.
            var dbField = dbFields?.FirstOrDefault(f =>
                string.Equals(f.Name, columnName, StringComparison.OrdinalIgnoreCase));

            var columnType = dbField?.Type ?? Nullable.GetUnderlyingType(prop.PropertyType)
                             ?? prop.PropertyType;

            // Compile a getter once per property instead of reflecting on every row at insert time.
            var getter = BuildGetter(prop);

            bindings.Add(new FieldBinding(columnName, columnType, getter));
        }

        Bindings = bindings.AsReadOnly();
    }

    // ─── Column name resolution ──────────────────────────────────────────────

    /// <summary>
    /// Resolves the ClickHouse column name for a single property, trying RepoDb's field cache first
    /// (which reflects any <c>ClassMapper</c>/fluent mapping configuration), then a direct
    /// <c>[Map]</c> attribute on the property, and finally falling back to the CLR property name
    /// unchanged.
    /// </summary>
    /// <param name="prop">The property being resolved.</param>
    /// <param name="repoDbFields">
    /// RepoDb's cached <see cref="Field"/> list for <typeparamref name="TEntity"/>, or
    /// <see langword="null"/> if unavailable.
    /// </param>
    /// <returns>The column name to use for <paramref name="prop"/>.</returns>
    private static string ResolveColumnName(
        PropertyInfo prop,
        IEnumerable<Field>? repoDbFields)
    {
        // 1. RepoDb's cached field (accounts for ClassMapper/fluent mappings).
        if (repoDbFields is not null)
        {
            var match = repoDbFields.FirstOrDefault(f =>
                string.Equals(f.Name, prop.Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match.Name;
        }

        // 2. A [Map] attribute declared directly on the property.
        var mapAttr = prop.GetCustomAttribute<MapAttribute>();
        if (mapAttr is not null)
            return mapAttr.Name;

        // 3. The property name, unchanged.
        return prop.Name;
    }

    // ─── Getter compilation ───────────────────────────────────────────────────

    /// <summary>
    /// Compiles a boxing property getter as an expression tree instead of invoking
    /// <see cref="PropertyInfo.GetValue(object?)"/> reflectively on every row, which is significantly
    /// slower when applied across large batches.
    /// </summary>
    /// <param name="prop">The property to build a getter for.</param>
    /// <returns>A compiled delegate that reads <paramref name="prop"/>'s value, boxed as <see cref="object"/>.</returns>
    private static Func<TEntity, object?> BuildGetter(PropertyInfo prop)
    {
        var param = System.Linq.Expressions.Expression.Parameter(typeof(TEntity), "e");
        var access = System.Linq.Expressions.Expression.Property(param, prop);
        var boxed = System.Linq.Expressions.Expression.Convert(access, typeof(object));
        return System.Linq.Expressions.Expression.Lambda<Func<TEntity, object?>>(boxed, param)
                                                  .Compile();
    }
}
