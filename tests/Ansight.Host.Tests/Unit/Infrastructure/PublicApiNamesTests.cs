using Ansight.Infrastructure.Preferences;

namespace Ansight.Host.Tests.Unit.Infrastructure;

public sealed class PublicApiNamesTests
{
    [Theory]
    [InlineData(typeof(Ansight.Infrastructure.Localisation.ILocalisationService), "Ansight.Infrastructure.Localisation.ILocalisationService")]
    [InlineData(typeof(Ansight.Infrastructure.Logging.ILogger), "Ansight.Infrastructure.Logging.ILogger")]
    [InlineData(typeof(FilePreferencesStore), "Ansight.Infrastructure.Preferences.FilePreferencesStore")]
    [InlineData(typeof(Ansight.Infrastructure.Security.IEncryptedStorage), "Ansight.Infrastructure.Security.IEncryptedStorage")]
    [InlineData(typeof(Ansight.Infrastructure.Teams.IKnownTeamStore), "Ansight.Infrastructure.Teams.IKnownTeamStore")]
    [InlineData(typeof(Ansight.Infrastructure.Theming.ITheme), "Ansight.Infrastructure.Theming.ITheme")]
    [InlineData(typeof(Ansight.Infrastructure.Theming.Themes.ThemeBase), "Ansight.Infrastructure.Theming.Themes.ThemeBase")]
    [InlineData(typeof(Ansight.Infrastructure.Utilities.PasswordValidator), "Ansight.Infrastructure.Utilities.PasswordValidator")]
    [InlineData(typeof(Ansight.Infrastructure.Web.ISharedHttpClient), "Ansight.Infrastructure.Web.ISharedHttpClient")]
    public void InfrastructureUsesCurrentClrTypeIdentity(Type type, string publishedName)
    {
        Assert.Equal("Ansight.Infrastructure", type.Assembly.GetName().Name);
        Assert.Equal(publishedName, type.FullName);
        Assert.Same(type, type.Assembly.GetType(publishedName, throwOnError: true));
    }

    [Fact]
    public void PreferencesRetainSingleStringConstructorForPreviouslyCompiledClients()
    {
        // An optional extra argument would preserve source calls but remove this CLR signature.
        var constructor = typeof(FilePreferencesStore).GetConstructor([typeof(string)]);

        Assert.NotNull(constructor);
        Assert.True(constructor.IsPublic);
    }

    [Fact]
    public void CompanionHostApplicationUsesCurrentValues()
    {
        Assert.Equal(0, (int)CompanionHostApplication.Cli);
        Assert.Equal(1, (int)CompanionHostApplication.LegacyDesktop);
    }

    [Fact]
    public void PublishedFilePushHelperRemainsCallableWithItsOriginalSignature()
    {
        Func<string?, JsonObject?, JsonObject> createTemplate = AppFilePush.CreateHostArgumentTemplate;
        var template = createTemplate("image.png", new JsonObject
        {
            ["directoryPath"] = "uploads",
            ["overwrite"] = true
        });

        Assert.Equal("image.png", template["localFilePath"]?.GetValue<string>());
        Assert.Equal("image.png", template["fileName"]?.GetValue<string>());
        Assert.Equal("uploads", template["directoryPath"]?.GetValue<string>());
        Assert.True(template["overwrite"]?.GetValue<bool>());
        Assert.True(template["createDirectory"]?.GetValue<bool>());
    }

    [Fact]
    public void PublishedTitleResourceKeyUsesCurrentProductName()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ansight-public-api-{Guid.NewGuid():N}");
        try
        {
            var preferences = new UserPreferences(new FilePreferencesStore(Path.Combine(directory, "preferences.json")));
            var localisation = new Ansight.Infrastructure.Localisation.LocalisationService(preferences);

            Assert.Equal("Ansight", localisation.Localise("App_Title"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
