using System.Xml.Linq;
using Backbone.App.Validation;
using Backbone.App.ViewModels;
using Backbone.Contracts;
using Xunit;

namespace Backbone.App.Tests.ViewModels;

/// <summary>HU-07: todo código de error y de validación tiene texto en es-ES y en-US.</summary>
public sealed class ResourceCoverageTests
{
    internal static Dictionary<string, string> LoadResources(string culture)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Backbone.slnx")))
        {
            dir = dir.Parent;
        }

        var path = Path.Combine(dir?.FullName ?? throw new InvalidOperationException("No se encuentra Backbone.slnx."), "src", "Backbone.App", "Strings", culture, "Resources.resw");
        return XDocument.Load(path).Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);
    }

    [Theory]
    [Trait("Story", "HU-07")]
    [InlineData("es-ES")]
    [InlineData("en-US")]
    public void EveryErrorAndValidationCodeHasText(string culture)
    {
        var res = LoadResources(culture);
        foreach (var code in Enum.GetValues<BackboneErrorCode>())
        {
            Assert.True(res.TryGetValue(BackboneErrorTexts.KeyFor(code), out var t) && !string.IsNullOrWhiteSpace(t), $"{culture}: {code}");
        }

        foreach (var code in Enum.GetValues<ValidationIssueCode>())
        {
            Assert.True(res.TryGetValue(ValidationIssueTexts.KeyFor(code), out var t) && !string.IsNullOrWhiteSpace(t), $"{culture}: {code}");
        }
    }
}
