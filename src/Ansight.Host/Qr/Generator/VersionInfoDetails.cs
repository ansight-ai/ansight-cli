using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Ansight.Host.Qr.Generator;

internal struct VersionInfoDetails
{
    public VersionInfoDetails(ECCLevel errorCorrectionLevel, Dictionary<EncodingMode, int> capacityDict)
    {
        this.ErrorCorrectionLevel = errorCorrectionLevel;
        this.CapacityDict = capacityDict;
    }

    public ECCLevel ErrorCorrectionLevel { get; }
    public Dictionary<EncodingMode, int> CapacityDict { get; }
}
