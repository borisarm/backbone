using Backbone.Contracts;

namespace Backbone.App.Validation;

/// <summary>
/// Traduce los códigos de validación del servicio (<see cref="ValidationIssueCode"/>, §7.4 y §7.5) a texto localizado y los
/// agrupa por campo para mostrarlos junto a cada control. La app no muestra <c>Message</c>. Las cadenas vienen de los recursos
/// mediante <c>getString</c> (claves <c>Validation_*</c> de <c>Resources.resw</c>).
/// </summary>
internal sealed class ValidationIssueTexts(Func<string, string> getString)
{
    /// <summary>Clave de recurso del texto de un código.</summary>
    public static string KeyFor(ValidationIssueCode code) => "Validation_" + code;

    /// <summary>Texto localizado de un problema.</summary>
    public string Translate(ValidationIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        // Un servicio más nuevo puede enviar un código que esta app no conoce (§7.5): texto genérico.
        return getString(KeyFor(Enum.IsDefined(issue.Code) ? issue.Code : ValidationIssueCode.Other));
    }

    /// <summary>Textos por campo (<c>Sources[0].Path</c>…), en el orden recibido; el campo vacío es el de la entrada entera.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ByField(IEnumerable<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var issue in issues)
        {
            if (!result.TryGetValue(issue.Field, out var list))
            {
                result[issue.Field] = list = [];
            }
            list.Add(Translate(issue));
        }
        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
    }
}
