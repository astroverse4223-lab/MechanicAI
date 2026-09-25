using System.Text.Json;
using MechanicAI.Application.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MechanicAI.Infrastructure.Persistence;

/// <summary>Stores structured values (lists of records, dictionaries) as JSON text columns.</summary>
internal static class JsonValueConversion
{
    public static PropertyBuilder<T> HasJsonConversion<T>(this PropertyBuilder<T> builder, bool jsonb)
        where T : class, new()
    {
        var converter = new ValueConverter<T, string>(
            v => JsonSerializer.Serialize(v, Json.Options),
            v => string.IsNullOrEmpty(v) ? new T() : JsonSerializer.Deserialize<T>(v, Json.Lenient) ?? new T());

        var comparer = new ValueComparer<T>(
            (a, b) => JsonSerializer.Serialize(a, Json.Options) == JsonSerializer.Serialize(b, Json.Options),
            v => v == null ? 0 : JsonSerializer.Serialize(v, Json.Options).GetHashCode(StringComparison.Ordinal),
            v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, Json.Options), Json.Lenient)!);

        builder.HasConversion(converter, comparer);
        if (jsonb) builder.HasColumnType("jsonb");
        return builder;
    }
}
