using System.Globalization;
using System.Text.RegularExpressions;
using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class LocalizationTests
{
    [TestMethod]
    public void AllLanguagesHaveEveryKeyAndMatchingFormatPlaceholders()
    {
        var source = LocalizationService.Read("en-US"); Assert.IsGreaterThanOrEqualTo(20, LocalizationService.AvailableLanguages.Count);
        foreach (var tag in LocalizationService.AvailableLanguages)
        {
            var locale = LocalizationService.Read(tag);
            CollectionAssert.AreEquivalent(source.Keys.ToArray(), locale.Keys.ToArray(), tag);
            foreach (var (key, value) in source)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(locale[key]), $"{tag}:{key}");
                CollectionAssert.AreEquivalent(Regex.Matches(value, @"\{\d+\}").Select(x => x.Value).ToArray(), Regex.Matches(locale[key], @"\{\d+\}").Select(x => x.Value).ToArray(), $"{tag}:{key}");
            }
            _ = CultureInfo.GetCultureInfo(tag);
        }
    }
    [TestMethod]
    [DataRow("ar-SA", true)] [DataRow("he-IL", true)] [DataRow("zh-CN", false)] [DataRow("en-US", false)]
    public void DirectionFollowsSelectedLanguage(string tag, bool rtl) => Assert.AreEqual(rtl, new LocalizationService(tag).IsRightToLeft);
    [TestMethod]
    public void UnsupportedLocaleFallsBackToEnglishAndLanguageVariantToAvailableCatalog()
    {
        Assert.AreEqual("en-US", new LocalizationService("xx-ZZ").LanguageTag);
        Assert.AreEqual("fr-FR", new LocalizationService("fr-CA").LanguageTag);
    }
}
