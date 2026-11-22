using System.Xml.Linq;
using Backbone.App.Validation;
using Backbone.Contracts;
using Xunit;

namespace Backbone.App.Tests.Validation;

public sealed class ValidationIssueTextsTests
{
    private static string AppDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Backbone.slnx")))
        {
            dir = dir.Parent;
        }
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("No se encuentra Backbone.slnx."), "src", "Backbone.App");
    }

    private static Dictionary<string, string> Load(string culture) =>
        XDocument.Load(Path.Combine(AppDir(), "Strings", culture, "Resources.resw")).Root!
            .Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);

    [Theory]
    [InlineData("es-ES")]
    [InlineData("en-US")]
    public void ProtectedFoldersUnavailable_HasTextInBothLanguages(string culture)
    {
        var res = Load(culture);
        var text = Assert.Contains(ValidationIssueTexts.KeyFor(ValidationIssueCode.ProtectedFoldersUnavailable), res);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("%ProgramData%", text, StringComparison.Ordinal);
        Assert.DoesNotContain("apartad", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("untrusted", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedFoldersUnavailable_ShownOnSourceField()
    {
        var res = Load("es-ES");
        var texts = new ValidationIssueTexts(k => res[k]);
        var issues = new[]
        {
            new ValidationIssue { Field = "Sources[0].Path", Code = ValidationIssueCode.ProtectedFoldersUnavailable },
            new ValidationIssue { Field = "Sources[1].Path", Code = ValidationIssueCode.ProtectedFoldersUnavailable },
        };

        var byField = texts.ByField(issues);

        Assert.Equal(2, byField.Count);
        Assert.Equal(res["Validation_ProtectedFoldersUnavailable"], Assert.Single(byField["Sources[0].Path"]));
        Assert.Equal(res["Validation_ProtectedFoldersUnavailable"], Assert.Single(byField["Sources[1].Path"]));
    }
}
