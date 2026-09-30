namespace Ansight.Host.Sessions;

public enum SessionImportFailureReason
{
    None,
    PasswordRequired,
    InvalidPassword,
    InvalidArchive,
    UnsupportedArchive,
    IoError
}
