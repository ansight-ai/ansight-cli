using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Ansight.Host.Qr.Generator;

internal class Point
{
    public int X { get; }
    public int Y { get; }
    public Point(int x, int y)
    {
        this.X = x;
        this.Y = y;
    }
}
