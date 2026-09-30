namespace Ansight.Infrastructure.Utilities;

public static class PasswordValidator
{
    public static bool IsValidPasswordCombination(string password, string confirmedPassword)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        var isValid = password == confirmedPassword
                      && password.Length >= 8
                      && password.Any(char.IsLower)
                      && password.Any(char.IsUpper)
                      && password.Any(char.IsNumber);

        return isValid;
    }
}
