using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Ansight.Host.Qr.Generator;

internal struct VersionInfo
{
    public VersionInfo(int version, List<VersionInfoDetails> versionInfoDetails)
    {
        this.Version = version;
        this.Details = versionInfoDetails;
    }
    public int Version { get; }
    public List<VersionInfoDetails> Details { get; }
}
