using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ansight.Infrastructure.Security;

namespace Ansight.Host.SimulatorAgent.Secrets;

internal sealed record SecretAccess(
    IReadOnlyDictionary<string, SimulatorAgentSecretMetadata> Secrets,
    Func<string, string?> Resolve)
{
    public static SecretAccess Empty { get; } = new(
        new Dictionary<string, SimulatorAgentSecretMetadata>(StringComparer.OrdinalIgnoreCase),
        static _ => null);
}
