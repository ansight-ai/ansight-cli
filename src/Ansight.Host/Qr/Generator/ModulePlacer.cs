using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Ansight.Host.Qr.Generator;

internal static class ModulePlacer
{
    public static void AddQuietZone(ref QRCodeData qrCode, int quietZoneSize)
    {
        if (quietZoneSize <= 0)
        {
            return;
        }

        var quietLine = new bool[qrCode.ModuleMatrix.Count + quietZoneSize * 2];
        for (var i = 0; i < quietLine.Length; i++)
            quietLine[i] = false;
        for (var i = 0; i < quietZoneSize; i++)
            qrCode.ModuleMatrix.Insert(0, new BitArray(quietLine));
        for (var i = 0; i < quietZoneSize; i++)
            qrCode.ModuleMatrix.Add(new BitArray(quietLine));
        for (var i = quietZoneSize; i < qrCode.ModuleMatrix.Count - quietZoneSize; i++)
        {
            bool[] quietPart = new bool[quietZoneSize];
            var tmpLine = new List<bool>(quietPart);
            tmpLine.AddRange(qrCode.ModuleMatrix[i].Cast<bool>());
            tmpLine.AddRange(quietPart);
            qrCode.ModuleMatrix[i] = new BitArray(tmpLine.ToArray());
        }
    }

    private static string ReverseString(string inp)
    {
        string newStr = string.Empty;
        if (inp.Length > 0)
        {
            for (int i = inp.Length - 1; i >= 0; i--)
                newStr += inp[i];
        }
        return newStr;
    }

    public static void PlaceVersion(ref QRCodeData qrCode, string versionStr)
    {
        var size = qrCode.ModuleMatrix.Count;

        var vStr = ReverseString(versionStr);

        for (var x = 0; x < 6; x++)
        {
            for (var y = 0; y < 3; y++)
            {
                qrCode.ModuleMatrix[y + size - 11][x] = vStr[x * 3 + y] == '1';
                qrCode.ModuleMatrix[x][y + size - 11] = vStr[x * 3 + y] == '1';
            }
        }
    }

    public static void PlaceFormat(ref QRCodeData qrCode, string formatStr)
    {
        var size = qrCode.ModuleMatrix.Count;
        var fStr = ReverseString(formatStr);
        var modules = new[,] { { 8, 0, size - 1, 8 }, { 8, 1, size - 2, 8 }, { 8, 2, size - 3, 8 }, { 8, 3, size - 4, 8 }, { 8, 4, size - 5, 8 }, { 8, 5, size - 6, 8 }, { 8, 7, size - 7, 8 }, { 8, 8, size - 8, 8 }, { 7, 8, 8, size - 7 }, { 5, 8, 8, size - 6 }, { 4, 8, 8, size - 5 }, { 3, 8, 8, size - 4 }, { 2, 8, 8, size - 3 }, { 1, 8, 8, size - 2 }, { 0, 8, 8, size - 1 } };
        for (var i = 0; i < 15; i++)
        {
            var p1 = new Point(modules[i, 0], modules[i, 1]);
            var p2 = new Point(modules[i, 2], modules[i, 3]);
            qrCode.ModuleMatrix[p1.Y][p1.X] = fStr[i] == '1';
            qrCode.ModuleMatrix[p2.Y][p2.X] = fStr[i] == '1';
        }
    }


    public static int MaskCode(ref QRCodeData qrCode, int version, ref List<Rectangle> blockedModules, ECCLevel eccLevel)
    {
        var patternName = string.Empty;
        var patternScore = 0;

        var size = qrCode.ModuleMatrix.Count;

        var methods = typeof(MaskPattern).GetTypeInfo().DeclaredMethods;

        foreach (var pattern in methods)
        {
            if (pattern.Name.Length == 8 && pattern.Name.Substring(0, 7) == "Pattern")
            {
                var qrTemp = new QRCodeData(version);
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        qrTemp.ModuleMatrix[y][x] = qrCode.ModuleMatrix[y][x];
                    }

                }

                var formatStr = QRCodeGenerator.GetFormatString(eccLevel, Convert.ToInt32((pattern.Name.Substring(7, 1))) - 1);
                ModulePlacer.PlaceFormat(ref qrTemp, formatStr);
                if (version >= 7)
                {
                    var versionString = QRCodeGenerator.GetVersionString(version);
                    ModulePlacer.PlaceVersion(ref qrTemp, versionString);
                }

                for (var x = 0; x < size; x++)
                {
                    for (var y = 0; y < size; y++)
                    {
                        if (!IsBlocked(new Rectangle(x, y, 1, 1), blockedModules))
                        {
                            qrTemp.ModuleMatrix[y][x] ^= (bool)pattern.Invoke(null, new object[] { x, y })!;
                        }
                    }
                }

                var score = MaskPattern.Score(ref qrTemp);
                if (string.IsNullOrEmpty(patternName) || patternScore > score)
                {
                    patternName = pattern.Name;
                    patternScore = score;
                }

            }
        }

        var patterMethod = typeof(MaskPattern).GetTypeInfo().GetDeclaredMethod(patternName)
            ?? throw new InvalidOperationException($"Mask pattern '{patternName}' was not found.");

        for (var x = 0; x < size; x++)
        {
            for (var y = 0; y < size; y++)
            {
                if (!IsBlocked(new Rectangle(x, y, 1, 1), blockedModules))
                {
                    qrCode.ModuleMatrix[y][x] ^= (bool)patterMethod.Invoke(null, new object[] { x, y })!;
                }
            }
        }
        return Convert.ToInt32(patterMethod.Name.Substring(patterMethod.Name.Length - 1, 1)) - 1;
    }


    public static void PlaceDataWords(ref QRCodeData qrCode, string data, ref List<Rectangle> blockedModules)
    {
        var size = qrCode.ModuleMatrix.Count;
        var up = true;
        var datawords = new Queue<bool>();
        for (int i = 0; i < data.Length; i++)
        {
            datawords.Enqueue(data[i] != '0');
        }
        for (var x = size - 1; x >= 0; x = x - 2)
        {
            if (x == 6)
                x = 5;
            for (var yMod = 1; yMod <= size; yMod++)
            {
                int y;
                if (up)
                {
                    y = size - yMod;
                    if (datawords.Count > 0 && !IsBlocked(new Rectangle(x, y, 1, 1), blockedModules))
                        qrCode.ModuleMatrix[y][x] = datawords.Dequeue();
                    if (datawords.Count > 0 && x > 0 && !IsBlocked(new Rectangle(x - 1, y, 1, 1), blockedModules))
                        qrCode.ModuleMatrix[y][x - 1] = datawords.Dequeue();
                }
                else
                {
                    y = yMod - 1;
                    if (datawords.Count > 0 && !IsBlocked(new Rectangle(x, y, 1, 1), blockedModules))
                        qrCode.ModuleMatrix[y][x] = datawords.Dequeue();
                    if (datawords.Count > 0 && x > 0 && !IsBlocked(new Rectangle(x - 1, y, 1, 1), blockedModules))
                        qrCode.ModuleMatrix[y][x - 1] = datawords.Dequeue();
                }
            }
            up = !up;
        }
    }

    public static void ReserveSeperatorAreas(int size, ref List<Rectangle> blockedModules)
    {
        blockedModules.AddRange(new[]{
            new Rectangle(7, 0, 1, 8),
            new Rectangle(0, 7, 7, 1),
            new Rectangle(0, size-8, 8, 1),
            new Rectangle(7, size-7, 1, 7),
            new Rectangle(size-8, 0, 1, 8),
            new Rectangle(size-7, 7, 7, 1)
        });
    }

    public static void ReserveVersionAreas(int size, int version, ref List<Rectangle> blockedModules)
    {
        blockedModules.AddRange(new[]{
            new Rectangle(8, 0, 1, 6),
            new Rectangle(8, 7, 1, 1),
            new Rectangle(0, 8, 6, 1),
            new Rectangle(7, 8, 2, 1),
            new Rectangle(size-8, 8, 8, 1),
            new Rectangle(8, size-7, 1, 7)
        });

        if (version >= 7)
        {
            blockedModules.AddRange(new[]{
                new Rectangle(size-11, 0, 3, 6),
                new Rectangle(0, size-11, 6, 3)
            });
        }
    }
    public static void PlaceDarkModule(ref QRCodeData qrCode, int version, ref List<Rectangle> blockedModules)
    {
        qrCode.ModuleMatrix[4 * version + 9][8] = true;
        blockedModules.Add(new Rectangle(8, 4 * version + 9, 1, 1));
    }

    public static void PlaceFinderPatterns(ref QRCodeData qrCode, ref List<Rectangle> blockedModules)
    {
        var size = qrCode.ModuleMatrix.Count;
        int[] locations = { 0, 0, size - 7, 0, 0, size - 7 };

        for (var i = 0; i < 6; i = i + 2)
        {
            for (var x = 0; x < 7; x++)
            {
                for (var y = 0; y < 7; y++)
                {
                    if (!(((x == 1 || x == 5) && y > 0 && y < 6) || (x > 0 && x < 6 && (y == 1 || y == 5))))
                    {
                        qrCode.ModuleMatrix[y + locations[i + 1]][x + locations[i]] = true;
                    }
                }
            }
            blockedModules.Add(new Rectangle(locations[i], locations[i + 1], 7, 7));
        }
    }

    public static void PlaceAlignmentPatterns(ref QRCodeData qrCode, List<Point> alignmentPatternLocations, ref List<Rectangle> blockedModules)
    {
        foreach (var loc in alignmentPatternLocations)
        {
            var alignmentPatternRect = new Rectangle(loc.X, loc.Y, 5, 5);
            var blocked = false;
            foreach (var blockedRect in blockedModules)
            {
                if (Intersects(alignmentPatternRect, blockedRect))
                {
                    blocked = true;
                    break;
                }
            }
            if (blocked)
                continue;

            for (var x = 0; x < 5; x++)
            {
                for (var y = 0; y < 5; y++)
                {
                    if (y == 0 || y == 4 || x == 0 || x == 4 || (x == 2 && y == 2))
                    {
                        qrCode.ModuleMatrix[loc.Y + y][loc.X + x] = true;
                    }
                }
            }
            blockedModules.Add(new Rectangle(loc.X, loc.Y, 5, 5));
        }
    }

    public static void PlaceTimingPatterns(ref QRCodeData qrCode, ref List<Rectangle> blockedModules)
    {
        var size = qrCode.ModuleMatrix.Count;
        for (var i = 8; i < size - 8; i++)
        {
            if (i % 2 == 0)
            {
                qrCode.ModuleMatrix[6][i] = true;
                qrCode.ModuleMatrix[i][6] = true;
            }
        }
        blockedModules.AddRange(new[]{
            new Rectangle(6, 8, 1, size-16),
            new Rectangle(8, 6, size-16, 1)
        });
    }

    private static bool Intersects(Rectangle r1, Rectangle r2)
    {
        return r2.X < r1.X + r1.Width && r1.X < r2.X + r2.Width && r2.Y < r1.Y + r1.Height && r1.Y < r2.Y + r2.Height;
    }

    private static bool IsBlocked(Rectangle r1, List<Rectangle> blockedModules)
    {
        var isBlocked = false;
        foreach (var blockedMod in blockedModules)
        {
            if (Intersects(blockedMod, r1))
                isBlocked = true;
        }
        return isBlocked;
    }


}
