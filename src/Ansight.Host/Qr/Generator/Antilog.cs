using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Ansight.Host.Qr.Generator;

internal struct Antilog
{
    public Antilog(int exponentAlpha, int integerValue)
    {
        this.ExponentAlpha = exponentAlpha;
        this.IntegerValue = integerValue;
    }
    public int ExponentAlpha { get; }
    public int IntegerValue { get; }
}
