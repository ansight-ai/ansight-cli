namespace Ansight.Infrastructure.Localisation;

public static class Localise
{
    private static readonly Lock gate = new();
    private static ILocalisationService? service;

    public static void SetService(ILocalisationService localisationService)
    {
        ArgumentNullException.ThrowIfNull(localisationService);

        lock (gate)
        {
            service = localisationService;
        }
    }

    public static string Value(string key, params LocalisationParameter[] parameters)
    {
        lock (gate)
        {
            return service?.Localise(key, parameters) ?? key;
        }
    }
}
