using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace Ansight.Cli.Commands.Setup;

internal sealed record AndroidPackage(string Name, Uri Download, long Bytes, string Sha1, string License);
