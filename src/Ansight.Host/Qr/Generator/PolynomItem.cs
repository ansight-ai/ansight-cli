using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Ansight.Host.Qr.Generator;

internal struct PolynomItem
{
    public PolynomItem(int coefficient, int exponent)
    {
        this.Coefficient = coefficient;
        this.Exponent = exponent;
    }

    public int Coefficient { get; }
    public int Exponent { get; }
}
