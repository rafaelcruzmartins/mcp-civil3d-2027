using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Civil3dMcp.Infrastructure;

/// <summary>
/// Opções JSON. Chama-se Fmt (e não Json) porque um assembly referenciado define um namespace global Json.
/// </summary>
public static class Fmt
{
    /// <summary>Entrada e schema das ferramentas (sem literais NaN/Infinity, senão o schema fica poluído).</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        // O SDK faz MakeReadOnly() nas opções: o resolver tem que estar definido antes.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Saída: aceita NaN/Infinity (comuns em propriedades de superfícies vazias).</summary>
    public static JsonSerializerOptions OutputOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Out(object? value) => JsonSerializer.Serialize(value, OutputOptions);

    public static double R(double v, int digits = 4) => double.IsFinite(v) ? Math.Round(v, digits) : v;
}
