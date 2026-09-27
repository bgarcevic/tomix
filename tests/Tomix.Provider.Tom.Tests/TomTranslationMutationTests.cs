using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// <c>translation:&lt;culture&gt;/&lt;property&gt;</c> assignments (issue #227): the mutator writes
/// caption, description and display-folder translations into an existing culture, and the
/// summarizer reads them back as <c>Translation:&lt;culture&gt;/&lt;Property&gt;</c> bag entries.
/// </summary>
public sealed class TomTranslationMutationTests
{
    [Theory]
    [InlineData("caption", TranslatedProperty.Caption)]
    [InlineData("name", TranslatedProperty.Caption)]
    [InlineData("Description", TranslatedProperty.Description)]
    [InlineData("displayFolder", TranslatedProperty.DisplayFolder)]
    public void SetProperty_WritesMeasureTranslation(string property, TranslatedProperty expected)
    {
        var (db, measure, culture) = SalesWithCulture();

        Set(db, "Sales/Revenue", $"translation:da-DK/{property}", "Omsætning");

        Assert.Equal("Omsætning", culture.ObjectTranslations[measure, expected]?.Value);
    }

    [Fact]
    public void SetProperty_ReplacesExistingTranslation()
    {
        var (db, measure, culture) = SalesWithCulture();

        Set(db, "Sales/Revenue", "translation:da-DK/caption", "Salg");
        Set(db, "Sales/Revenue", "translation:da-DK/caption", "Omsætning");

        Assert.Single(culture.ObjectTranslations);
        Assert.Equal("Omsætning", culture.ObjectTranslations[measure, TranslatedProperty.Caption]!.Value);
    }

    [Fact]
    public void SetProperty_EmptyValueRemovesTranslation()
    {
        var (db, _, culture) = SalesWithCulture();
        Set(db, "Sales/Revenue", "translation:da-DK/caption", "Omsætning");

        Set(db, "Sales/Revenue", "translation:da-DK/caption", "");

        Assert.Empty(culture.ObjectTranslations);
    }

    [Theory]
    [InlineData("Sales", "Salg")]
    [InlineData("Sales/Amount", "Beløb")]
    [InlineData(".", "Salgsmodel")]
    public void SetProperty_TranslatesTablesColumnsAndTheModel(string path, string caption)
    {
        var (db, _, culture) = SalesWithCulture();

        Set(db, path, "translation:da-DK/caption", caption);

        var translation = Assert.Single(culture.ObjectTranslations);
        Assert.Equal(caption, translation.Value);
    }

    [Fact]
    public void SetProperty_MissingCulture_FailsWithAddHint()
    {
        var (db, _, _) = SalesWithCulture();

        var ex = Assert.Throws<ArgumentException>(
            () => Set(db, "Sales/Revenue", "translation:sv-SE/caption", "Intäkt"));

        Assert.Contains("Culture 'sv-SE' does not exist", ex.Message);
        Assert.Contains("tx add Cultures/sv-SE", ex.Message);
    }

    [Theory]
    [InlineData("translation:da-DK/expression", "Translatable properties: caption, description, displayFolder.")]
    [InlineData("translation:da-DK", "translation:<culture>/<property>")]
    [InlineData("translation:/caption", "translation:<culture>/<property>")]
    public void SetProperty_MalformedOrUnknownProperty_Fails(string property, string message)
    {
        var (db, _, _) = SalesWithCulture();

        var ex = Assert.ThrowsAny<Exception>(() => Set(db, "Sales/Revenue", property, "x"));

        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void SetProperty_DisplayFolderOnTable_IsRejected()
    {
        var (db, _, culture) = SalesWithCulture();

        var ex = Assert.Throws<NotSupportedException>(
            () => Set(db, "Sales", "translation:da-DK/displayFolder", "Mappe"));

        Assert.Contains("tables", ex.Message);
        Assert.Empty(culture.ObjectTranslations);
    }

    [Fact]
    public void Snapshot_ReadsTranslationsBack()
    {
        var (db, _, _) = SalesWithCulture();
        Set(db, "Sales/Revenue", "translation:da-DK/caption", "Omsætning");
        Set(db, "Sales/Revenue", "translation:da-DK/displayFolder", "Nøgletal");
        Set(db, ".", "translation:da-DK/description", "Salgsmodel");

        var snapshot = TomModelSummarizer.Snapshot(db, "M");

        var revenue = snapshot.Objects.Single(o => o.Name == "Sales").Children.Single(o => o.Name == "Revenue");
        Assert.Equal("Omsætning", revenue.Properties![$"{PropertyBagKeys.TranslationPrefix}da-DK/Caption"]);
        Assert.Equal("Nøgletal", revenue.Properties[$"{PropertyBagKeys.TranslationPrefix}da-DK/DisplayFolder"]);
        Assert.Equal("Salgsmodel", snapshot.Properties![$"{PropertyBagKeys.TranslationPrefix}da-DK/Description"]);
    }

    private static (Database Db, Measure Revenue, Culture Culture) SalesWithCulture()
    {
        var db = WithSales(withAmountColumn: true);
        var revenue = new Measure { Name = "Revenue", Expression = "SUM(Sales[Amount])" };
        db.Model.Tables["Sales"].Measures.Add(revenue);
        var culture = new Culture { Name = "da-DK" };
        db.Model.Cultures.Add(culture);
        return (db, revenue, culture);
    }

    private static void Set(Database db, string path, string property, string value)
        => new TomModelMutator(db).SetProperty(new ModelObjectSetRequest(
            path, [new ModelPropertyAssignment(property, value)], Type: null));
}
