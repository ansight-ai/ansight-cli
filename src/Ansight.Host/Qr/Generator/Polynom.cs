using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Ansight.Host.Qr.Generator;

internal class Polynom
{
    public Polynom()
    {
        this.PolyItems = new List<PolynomItem>();
    }

    public List<PolynomItem> PolyItems { get; set; }

    public override string ToString()
    {
        var sb = new StringBuilder();
        //this.PolyItems.ForEach(x => sb.Append("a^" + x.Coefficient + "*x^" + x.Exponent + " + "));
        foreach (var polyItem in this.PolyItems)
        {
            sb.Append("a^" + polyItem.Coefficient + "*x^" + polyItem.Exponent + " + ");
        }

        return sb.ToString().TrimEnd(new[] { ' ', '+' });
    }
}
