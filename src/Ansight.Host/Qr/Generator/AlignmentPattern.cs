using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Ansight.Host.Qr.Generator;

internal struct AlignmentPattern
{
    public int Version;
    public List<Point> PatternPositions;
}
