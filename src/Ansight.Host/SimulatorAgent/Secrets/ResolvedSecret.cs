using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ansight.Infrastructure.Security;

namespace Ansight.Host.SimulatorAgent.Secrets;

internal sealed record ResolvedSecret(
    SimulatorAgentSecretMetadata Metadata,
    string Value);
