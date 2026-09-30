using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Ansight.Host.Qr.Generator;

internal class Rectangle
{
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }

    public Rectangle(int x, int y, int w, int h)
    {
        this.X = x;
        this.Y = y;
        this.Width = w;
        this.Height = h;
    }
}
