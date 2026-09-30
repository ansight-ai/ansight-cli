namespace Ansight.Cli.Configuration;

// References only: master keys and tokens must never be stored in CLI preferences.
internal sealed record CredentialSettings(string Provider, string? KeyFile, string? StoreFile);
